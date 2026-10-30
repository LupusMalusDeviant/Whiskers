using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>R1-23 / INV-49: the audit trail is tamper-evident. Every written version of a row is a link of one SHA-256
/// chain; signed checkpoints (database + file) pin how far the chain had got, so an edit, an inserted row, a row deleted
/// from the middle and an end cut off are all reported — the last one because the chain no longer reaches a checkpoint
/// it once reached.</summary>
public sealed class AuditHashChainTests : IDisposable
{
    internal const string VaultKey = "test-vault-passphrase-for-the-audit-chain";

    private readonly AuditTrailFixture _trail = new(VaultKey);

    public void Dispose() => _trail.Dispose();

    [Fact]
    public async Task An_untouched_trail_verifies_with_signed_checkpoints()
    {
        await _trail.LogAsync(3);
        await _trail.Chain.CheckpointAsync();

        var report = await _trail.Chain.VerifyAsync();

        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(3, report.RowsChecked);
        Assert.Equal(3, report.LinksChecked);
        Assert.Equal(3, report.HeadSeq);
        Assert.Equal(3, report.TrustedCheckpointSeq);
        Assert.True(report.CheckpointsSigned);
    }

    [Fact]
    public async Task A_changed_row_is_reported()
    {
        var ids = await _trail.LogAsync(3);
        await _trail.SqlAsync($"UPDATE AuditLog SET Details = 'rewritten' WHERE Id = {ids[1]}");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains($"Audit row {ids[1]} was changed after it was written.", report.Problems);
    }

    [Fact]
    public async Task A_row_inserted_outside_the_chain_is_reported()
    {
        await _trail.LogAsync(2);
        long forged;
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var row = new AuditLogEntity { Actor = "mallory", ActorType = "web", Action = "container.start", TargetType = "container" };
            db.AuditLog.Add(row);
            await db.SaveChangesAsync();
            forged = row.Id;
        }

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains($"Audit row {forged} has no link: it was added outside the trail.", report.Problems);
    }

    [Fact]
    public async Task A_row_deleted_from_the_middle_is_reported()
    {
        var ids = await _trail.LogAsync(3);
        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id = {ids[1]}");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains($"Audit row {ids[1]} was deleted.", report.Problems);
    }

    [Fact]
    public async Task A_row_deleted_with_its_link_breaks_the_chain()
    {
        var ids = await _trail.LogAsync(3);
        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id = {ids[1]}");
        await _trail.SqlAsync($"DELETE FROM AuditChain WHERE AuditId = {ids[1]}");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains(report.Problems, p => p.Contains("does not continue the link before it"));
    }

    [Fact]
    public async Task Removing_the_end_and_the_database_checkpoints_is_caught_by_the_checkpoint_file()
    {
        // The checkpoint file sits outside the database: a database-only edit cannot take it along.
        var ids = await _trail.LogAsync(3);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id = {ids[2]}");
        await _trail.SqlAsync($"DELETE FROM AuditChain WHERE AuditId = {ids[2]}");
        await _trail.SqlAsync("DELETE FROM AuditCheckpoints");

        var report = await _trail.Chain.VerifyAsync();   // the file still holds the checkpoint at link 3

        Assert.False(report.Intact);
        Assert.Contains(report.Problems, p => p.Contains("before the checkpoint at link 3: its end was cut off"));
    }

    [Fact]
    public async Task A_cut_off_end_is_reported_through_the_signed_checkpoint()
    {
        var ids = await _trail.LogAsync(4);
        await _trail.Chain.CheckpointAsync();
        // The last two rows and their links go — exactly what a quiet truncation looks like.
        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id >= {ids[2]}");
        await _trail.SqlAsync("DELETE FROM AuditChain WHERE Seq >= 3");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains("The trail ends at link 2, before the checkpoint at link 4: its end was cut off.", report.Problems);
    }

    [Fact]
    public async Task The_checkpoint_timer_never_signs_over_a_cut_off_end()
    {
        // The running app checkpoints every minute: that must not replace the file that shows the cut.
        var ids = await _trail.LogAsync(4);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id >= {ids[2]}");
        await _trail.SqlAsync("DELETE FROM AuditChain WHERE Seq >= 3");
        await _trail.SqlAsync("DELETE FROM AuditCheckpoints");

        await _trail.Chain.CheckpointAsync();                 // the next tick
        await _trail.LogAsync(3);                               // the trail grows past the old end
        await _trail.Chain.CheckpointAsync();

        var report = await _trail.Chain.VerifyAsync();
        Assert.False(report.Intact);
        Assert.Contains("The trail does not match the checkpoint at link 4.", report.Problems);
        var file = System.Text.Json.JsonSerializer.Deserialize<AuditCheckpointEntity>(await File.ReadAllTextAsync(_trail.CheckpointFile))!;
        Assert.Equal(4, file.Seq);                              // kept, not signed over
    }

    [Fact]
    public async Task The_checkpoint_timer_never_signs_over_a_wiped_trail()
    {
        await _trail.LogAsync(3);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync("DELETE FROM AuditLog");
        await _trail.SqlAsync("DELETE FROM AuditChain");
        await _trail.SqlAsync("DELETE FROM AuditCheckpoints");

        await _trail.Chain.SealExistingAsync();   // a restart
        await _trail.Chain.CheckpointAsync();
        await _trail.LogAsync(1);
        await _trail.Chain.CheckpointAsync();

        var report = await _trail.Chain.VerifyAsync();
        Assert.False(report.Intact);
        Assert.Contains(report.Problems, p => p.Contains("checkpoint at link 3"));
    }

    [Fact]
    public async Task A_VAULT_KEY_outage_does_not_erase_the_signed_evidence()
    {
        // A day without the key: the keyless process checkpoints every minute but never thins or replaces a signature.
        await _trail.LogAsync(3);
        await _trail.Chain.CheckpointAsync();
        var keyless = new AuditChain(vaultKey: null, _trail.CheckpointFile, scopes: _trail.Scopes);
        var ids = new List<long>();
        for (var i = 0; i < AuditChain.KeptHeadCheckpoints + 1; i++)
        {
            ids.AddRange(await _trail.LogAsync(1));
            await keyless.CheckpointAsync();
        }
        var signedFile = System.Text.Json.JsonSerializer.Deserialize<AuditCheckpointEntity>(await File.ReadAllTextAsync(_trail.CheckpointFile))!;
        Assert.NotNull(signedFile.Mac);   // not replaced by an unsigned one
        using (var scope = _trail.Scope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditCheckpoints
                .AnyAsync(c => c.Kind == AuditChain.HeadKind && c.Mac != null));

        // Meanwhile the end is cut below the last signed checkpoint; the key comes back.
        await _trail.SqlAsync("DELETE FROM AuditLog WHERE Id > (SELECT MIN(Id) FROM AuditLog)");
        await _trail.SqlAsync("DELETE FROM AuditChain WHERE Seq > 1");
        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains(report.Problems, p => p.Contains("its end was cut off"));
    }

    [Fact]
    public async Task Rows_written_before_the_first_link_are_sealed_with_it()
    {
        // An audit write that runs ahead of the start-up sealing must not leave the older rows outside the trail.
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.AuditLog.AddRange(new AuditLogEntity { Actor = "a", Action = "old.one" }, new AuditLogEntity { Actor = "b", Action = "old.two" });
            await db.SaveChangesAsync();
        }

        await _trail.LogAsync(1);
        await _trail.Chain.SealExistingAsync();

        var report = await _trail.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(3, report.RowsChecked);
        Assert.Equal(3, report.LinksChecked);
    }

    [Fact]
    public async Task Verification_reads_one_snapshot_while_writes_go_on()
    {
        await _trail.LogAsync(5);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested) await _trail.LogAsync(1);
        });
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var report = await _trail.Chain.VerifyAsync();
                Assert.True(report.Intact, string.Join("; ", report.Problems));
            }
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
    }

    [Fact]
    public async Task A_planted_checkpoint_with_a_garbage_signature_never_reaches_the_file()
    {
        await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        var signed = await File.ReadAllTextAsync(_trail.CheckpointFile);
        await _trail.SqlAsync("INSERT INTO AuditCheckpoints (Kind, Seq, Hash, PrunedThroughId, At, Mac) " +
                              "SELECT 'head', Seq, Hash, 0, At, '" + new string('b', 64) + "' FROM AuditChain WHERE Seq = 2");

        await _trail.Chain.CheckpointAsync();   // the head did not move: must still sign, not copy the planted row

        var file = System.Text.Json.JsonSerializer.Deserialize<AuditCheckpointEntity>(await File.ReadAllTextAsync(_trail.CheckpointFile))!;
        Assert.NotEqual(new string('b', 64), file.Mac);
        Assert.False((await _trail.Chain.VerifyAsync()).Intact);   // the planted row is still reported
        Assert.NotNull(signed);
    }

    [Fact]
    public async Task Key_adoption_waits_while_the_checkpoint_file_cannot_be_read()
    {
        using var before = new AuditTrailFixture(vaultKey: null);
        await before.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await before.LogAsync(1);
        await before.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);
        await File.WriteAllTextAsync(before.CheckpointFile, "{ broken");

        var keyed = new AuditChain(VaultKey, before.CheckpointFile, scopes: before.Scopes);
        await keyed.SealExistingAsync();
        await keyed.CheckpointAsync();   // pending: signs nothing
        using (var scope = before.Scope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditCheckpoints.AnyAsync(c => c.Mac != null));
        var pending = await keyed.VerifyAsync();
        Assert.False(pending.Complete);   // no clean result while nothing can be signed
        Assert.False(pending.CheckpointsSigned);
        Assert.Contains(pending.Findings!, f => f.Code == "AdoptionPending");
        // An unsigned row is not changed meanwhile — refused as pending, not reported as changed outside Whiskers.
        using (var scope = before.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var id = await db.AuditLog.Select(a => a.Id).MaxAsync();
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => keyed.UpdateAsync(db, [id], async ct =>
            {
                var changed = await db.AuditLog.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, "x"), ct);
                return (changed, (IReadOnlyCollection<long>)[id]);
            }));
            Assert.Contains("adoption is pending", refused.Message);
        }

        File.Delete(before.CheckpointFile);   // the file is readable again (here: gone) at the next start
        var restarted = new AuditChain(VaultKey, before.CheckpointFile, scopes: before.Scopes);
        await restarted.SealExistingAsync();
        var report = await restarted.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(report.HeadSeq, report.TrustedCheckpointSeq);
    }

    [Fact]
    public async Task Only_the_newest_head_checkpoints_are_kept()
    {
        for (var i = 0; i < AuditChain.KeptHeadCheckpoints + 5; i++)
        {
            await _trail.LogAsync(1);
            await _trail.Chain.CheckpointAsync();
        }

        using (var scope = _trail.Scope())
            Assert.Equal(AuditChain.KeptHeadCheckpoints, await scope.ServiceProvider.GetRequiredService<MetricsDbContext>()
                .AuditCheckpoints.CountAsync(c => c.Kind == AuditChain.HeadKind));
        Assert.True((await _trail.Chain.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task A_superseding_link_forged_for_an_old_row_is_reported_and_never_signed()
    {
        // A database writer changes an old row and appends a "new version" link with a correct SHA-256 — everything
        // but the link signature, which needs VAULT_KEY.
        var ids = await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync($"UPDATE AuditLog SET Actor = 'someone-else' WHERE Id = {ids[0]}");
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var row = await db.AuditLog.AsNoTracking().SingleAsync(a => a.Id == ids[0]);
            var head = await db.AuditChain.AsNoTracking().OrderByDescending(c => c.Seq).FirstAsync();
            var rowHash = AuditChain.RowHash(row);
            var hash = AuditChain.LinkHash(head.Hash, head.Seq + 1, row.Id, rowHash);
            await _trail.SqlAsync("INSERT INTO AuditChain (Seq, AuditId, RowHash, PrevHash, Hash, At) VALUES " +
                                  $"({head.Seq + 1}, {row.Id}, '{rowHash}', '{head.Hash}', '{hash}', '2026-10-30 00:00:00')");
        }

        // The next checkpoint must not launder it.
        await _trail.Chain.CheckpointAsync();
        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains("Link 3 is not signed: it was added outside Whiskers.", report.Problems);
        Assert.Contains(report.Findings!, f => f.Code == "LinkUnsigned");
    }

    [Fact]
    public async Task The_finding_count_is_every_finding_also_above_the_cap()
    {
        await _trail.LogAsync(AuditChain.MaxProblems + 3);
        await _trail.SqlAsync("UPDATE AuditLog SET Actor = 'someone-else'");

        var report = await _trail.Chain.VerifyAsync();

        Assert.Equal(AuditChain.MaxProblems + 3, report.FindingCount);
        Assert.Equal(AuditChain.MaxProblems + 1, report.Problems.Count);   // the cap and the "… and 3 more." line
    }

    [Fact]
    public async Task A_link_with_a_wrong_signature_is_reported()
    {
        await _trail.LogAsync(2);
        await _trail.SqlAsync("UPDATE AuditChain SET Mac = '" + new string('b', 64) + "' WHERE Seq = 2");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains("Link 2 has an invalid signature.", report.Problems);
    }

    [Fact]
    public async Task Without_VAULT_KEY_a_signed_trail_takes_no_more_writes()
    {
        // An unsigned link on a signed trail is indistinguishable from a forged one: the write fails (the gateway then
        // refuses the mutation) until the key is back.
        await _trail.LogAsync(1);
        await _trail.Chain.CheckpointAsync();
        var keyless = new AuditChain(vaultKey: null, _trail.CheckpointFile, scopes: _trail.Scopes);
        using var scope = _trail.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => keyless.WriteAsync(db, async token =>
        {
            var row = new AuditLogEntity { Actor = "a", Action = "x" };
            db.AuditLog.Add(row);
            await db.SaveChangesAsync(token);
            return (0, (IReadOnlyCollection<long>)[row.Id]);
        }));
        Assert.Equal(1, await db.AuditLog.CountAsync());   // nothing written: row and link are one transaction
    }

    [Fact]
    public async Task Every_finding_has_both_texts()
    {
        foreach (var code in new[] { "FileUnreadable", "AdoptionPending", "CheckpointSignature", "LinkBroken", "LinkChanged",
                     "LinkSignature", "LinkUnsigned", "CutOff", "CheckpointMismatch", "RowUnlinked", "RowChanged",
                     "RowDeleted", "RetentionUnsigned", "More" })
        {
            foreach (var file in new[] { "SharedResource.resx", "SharedResource.de.resx" })
            {
                var path = Directory.GetFiles(FindRepo(), file, SearchOption.AllDirectories)
                    .Single(p => p.Contains("Resources") && !p.Contains("bin") && !p.Contains("obj"));
                Assert.Contains($"name=\"AuditFinding_{code}\"", await File.ReadAllTextAsync(path));
            }
        }

        static string FindRepo()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Whiskers"))) dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "Whiskers");
        }
    }

    [Fact]
    public async Task A_rewritten_trail_does_not_match_its_checkpoint()
    {
        // A trail rebuilt from scratch with consistent hashes still differs from what was signed.
        await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync("UPDATE AuditLog SET Actor = 'someone-else'");
        await _trail.SqlAsync("DELETE FROM AuditChain");
        await _trail.SqlAsync("DELETE FROM AuditCheckpoints");   // the database's checkpoints go with it; the file stays
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var ids = await db.AuditLog.OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();
            await AuditChain.Shared.WriteAsync(db, _ => Task.FromResult((0, (IReadOnlyCollection<long>)ids)));
        }

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains("The trail does not match the checkpoint at link 2.", report.Problems);
    }

    [Fact]
    public async Task A_forged_checkpoint_signature_is_reported()
    {
        await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync("UPDATE AuditCheckpoints SET Mac = '" + new string('0', 64) + "'");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains(report.Problems, p => p.Contains("has an invalid signature"));
    }

    [Fact]
    public async Task Checkpoints_signed_under_another_key_are_not_trusted()
    {
        await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        var other = new AuditChain("another-vault-key", _trail.CheckpointFile, scopes: _trail.Scopes);

        var report = await other.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Null(report.TrustedCheckpointSeq);
        Assert.Contains(report.Problems, p => p.Contains("has an invalid signature"));
    }

    [Fact]
    public async Task Checkpoints_written_before_VAULT_KEY_was_set_are_untrusted_but_no_tamper_finding()
    {
        // Running without VAULT_KEY is a supported mode; setting the key later must not turn the trail red.
        using var before = new AuditTrailFixture(vaultKey: null);
        await before.LogAsync(2);
        await before.Chain.CheckpointAsync();
        var keyed = new AuditChain(VaultKey, before.CheckpointFile, scopes: before.Scopes);
        await before.LogAsync(1);

        var report = await keyed.VerifyAsync();

        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Null(report.TrustedCheckpointSeq);
        Assert.True(report.KeyConfigured);
        Assert.False(report.CheckpointsSigned);   // nothing authenticated yet: the UI says so instead of "intact"
    }

    [Fact]
    public async Task A_trail_pruned_before_VAULT_KEY_was_set_is_adopted_once_and_stays_verifiable()
    {
        using var before = new AuditTrailFixture(vaultKey: null);
        await before.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await before.LogAsync(1);
        await before.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);
        await before.LogAsync(1);
        await before.Chain.CheckpointAsync();

        // The next start with the key: the unsigned anchor is signed once, and the end is signed.
        var keyed = new AuditChain(VaultKey, before.CheckpointFile, scopes: before.Scopes);
        await keyed.SealExistingAsync();
        var report = await keyed.VerifyAsync();

        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(report.HeadSeq, report.TrustedCheckpointSeq);

        // Later starts adopt nothing more: an unsigned checkpoint planted afterwards is not taken as an anchor.
        await before.SqlAsync("INSERT INTO AuditCheckpoints (Kind, Seq, Hash, PrunedThroughId, At, Mac) VALUES " +
                              $"('retention', 0, '{AuditChain.Genesis}', 999999, '2026-01-01 00:00:00', NULL)");
        await keyed.SealExistingAsync();
        using var scope = before.Scope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditCheckpoints
            .CountAsync(c => c.Kind == AuditChain.RetentionKind && c.Mac != null));
        Assert.True((await keyed.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task A_signed_trail_whose_signatures_were_removed_is_not_adopted_again()
    {
        // Database-only tampering: nulling every signature in the database. The file still carries one, so nothing
        // is re-signed and the removed rows stay visible.
        var ids = await _trail.LogAsync(3);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id = {ids[0]}");
        await _trail.SqlAsync("UPDATE AuditCheckpoints SET Mac = NULL");

        await _trail.Chain.SealExistingAsync();

        using (var scope = _trail.Scope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditCheckpoints.AnyAsync(c => c.Mac != null));
        var report = await _trail.Chain.VerifyAsync();
        Assert.False(report.Intact);
        Assert.Contains($"Audit row {ids[0]} was deleted.", report.Problems);
    }

    [Fact]
    public async Task An_unreadable_checkpoint_file_makes_the_check_incomplete_not_a_tamper_finding()
    {
        await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        await File.WriteAllTextAsync(_trail.CheckpointFile, "{ not json");

        var report = await _trail.Chain.VerifyAsync();

        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.False(report.Complete);
        Assert.Contains(report.Problems, p => p.Contains("checkpoint file cannot be read"));
    }

    [Theory]
    [InlineData("zz")]   // 64 characters that are not hex
    [InlineData("0")]
    public async Task A_garbled_signature_is_a_finding_not_an_error(string filler)
    {
        await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        var garbled = string.Concat(Enumerable.Repeat(filler, 64 / filler.Length));
        await _trail.SqlAsync($"UPDATE AuditCheckpoints SET Mac = '{garbled}'");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains(report.Problems, p => p.Contains("has an invalid signature"));
    }

    [Fact]
    public async Task An_intent_row_completed_later_stays_valid()
    {
        // Mutation rows are written as an intent and completed with their outcome: each version is a link.
        var ids = await _trail.LogAsync(2);
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await _trail.Chain.UpdateAsync(db, [ids[0]], async ct =>
            {
                var changed = await db.AuditLog.Where(a => a.Id == ids[0])
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Success, false).SetProperty(a => a.Details, "outcome: failed"), ct);
                return (changed, (IReadOnlyCollection<long>)[ids[0]]);
            });
        }

        var report = await _trail.Chain.VerifyAsync();

        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(3, report.LinksChecked);
        Assert.Equal(2, report.RowsChecked);
    }

    [Fact]
    public async Task A_row_changed_in_the_database_is_never_given_a_new_version()
    {
        // The application must not authenticate someone else's edit by writing the row's next version over it.
        var ids = await _trail.LogAsync(1);
        await _trail.SqlAsync($"UPDATE AuditLog SET Actor = 'someone-else' WHERE Id = {ids[0]}");
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _trail.Chain.UpdateAsync(db, [ids[0]], async ct =>
            {
                var changed = await db.AuditLog.Where(a => a.Id == ids[0])
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, "outcome"), ct);
                return (changed, (IReadOnlyCollection<long>)[ids[0]]);
            }));
            Assert.Equal(1, await db.AuditChain.CountAsync());   // no new link
        }

        var report = await _trail.Chain.VerifyAsync();
        Assert.Contains($"Audit row {ids[0]} was changed after it was written.", report.Problems);
    }

    [Fact]
    public async Task A_row_changed_together_with_its_link_is_never_given_a_new_version()
    {
        // The link's RowHash is rewritten to match the edited row; its Hash and signature are left as they were.
        var ids = await _trail.LogAsync(1);
        await _trail.SqlAsync($"UPDATE AuditLog SET Actor = 'someone-else' WHERE Id = {ids[0]}");
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var edited = AuditChain.RowHash(await db.AuditLog.AsNoTracking().SingleAsync(a => a.Id == ids[0]));
            await _trail.SqlAsync($"UPDATE AuditChain SET RowHash = '{edited}' WHERE AuditId = {ids[0]}");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _trail.Chain.UpdateAsync(db, [ids[0]], async ct =>
            {
                var changed = await db.AuditLog.Where(a => a.Id == ids[0])
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, "outcome"), ct);
                return (changed, (IReadOnlyCollection<long>)[ids[0]]);
            }));
            Assert.Equal(1, await db.AuditChain.CountAsync());
        }
    }

    [Fact]
    public async Task A_row_whose_link_was_recomputed_without_a_signature_is_never_given_a_new_version()
    {
        // Row, RowHash and Hash rewritten consistently, the signature removed: only the signature is missing.
        var ids = await _trail.LogAsync(2);
        await _trail.SqlAsync($"UPDATE AuditLog SET Actor = 'someone-else' WHERE Id = {ids[0]}");
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var link = await db.AuditChain.AsNoTracking().SingleAsync(c => c.AuditId == ids[0]);
            var rowHash = AuditChain.RowHash(await db.AuditLog.AsNoTracking().SingleAsync(a => a.Id == ids[0]));
            var hash = AuditChain.LinkHash(link.PrevHash, link.Seq, link.AuditId, rowHash);
            await _trail.SqlAsync($"UPDATE AuditChain SET RowHash = '{rowHash}', Hash = '{hash}', Mac = NULL WHERE Seq = {link.Seq}");

            await Assert.ThrowsAsync<InvalidOperationException>(() => _trail.Chain.UpdateAsync(db, [ids[0]], async ct =>
            {
                var changed = await db.AuditLog.Where(a => a.Id == ids[0])
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, "outcome"), ct);
                return (changed, (IReadOnlyCollection<long>)[ids[0]]);
            }));
        }
    }

    [Fact]
    public async Task Key_adoption_signs_the_links_written_before_it()
    {
        using var before = new AuditTrailFixture(vaultKey: null);
        await before.LogAsync(3);
        await before.Chain.CheckpointAsync();

        var keyed = new AuditChain(VaultKey, before.CheckpointFile, scopes: before.Scopes);
        await keyed.SealExistingAsync();

        using (var scope = before.Scope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditChain.AnyAsync(c => c.Mac == null));
        // From now on an unsigned link is a finding — also one of the old ones.
        await before.SqlAsync("UPDATE AuditChain SET Mac = NULL WHERE Seq = 1");
        var report = await keyed.VerifyAsync();
        Assert.Contains("Link 1 is not signed: it was added outside Whiskers.", report.Problems);
    }

    [Fact]
    public async Task A_planted_unsigned_retention_checkpoint_is_a_finding_and_does_not_move_the_start()
    {
        await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync("INSERT INTO AuditCheckpoints (Kind, Seq, Hash, PrunedThroughId, At, Mac) VALUES " +
                              $"('retention', 999, '{AuditChain.Genesis}', 999, '2026-01-01 00:00:00', NULL)");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains("Retention checkpoint at link 999 is not signed: it was added outside Whiskers.", report.Problems);
        using var scope = _trail.Scope();
        var verified = await ((AuditChain)_trail.Chain).VerifyAsync();
        Assert.True(await ((AuditChain)_trail.Chain).UnchangedSinceAsync(
            scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), verified, 0, default));   // the start is still the real one
    }

    [Fact]
    public async Task Without_a_readable_checkpoint_file_no_chain_is_started_beside_unsealed_rows()
    {
        // The upgrade start finds old rows and a checkpoint file it cannot read: it neither seals nor starts a chain.
        using var fresh = new AuditTrailFixture(VaultKey);
        using (var scope = fresh.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.AuditLog.Add(new AuditLogEntity { Actor = "a", Action = "legacy" });
            await db.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(fresh.CheckpointFile, "{ broken");
        await fresh.Chain.SealExistingAsync();

        Assert.False(await fresh.Audit.TryLogAtAsync(DateTime.UtcNow, "admin@local.test", "web", "container.restart",
            "container", "c1", "c1", details: "x", serverId: "s1"));
        using (var scope = fresh.Scope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditChain.AnyAsync());

        // Readable again (here: gone): the next start seals the old rows first.
        File.Delete(fresh.CheckpointFile);
        await fresh.Chain.SealExistingAsync();
        var report = await fresh.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(1, report.RowsChecked);
    }

    [Fact]
    public async Task A_checkpoint_file_that_cannot_be_opened_is_not_taken_for_a_missing_one()
    {
        // File.Exists says "no" for a path it may not open: that must make the check incomplete, not skip the file.
        await _trail.LogAsync(1);
        await _trail.Chain.CheckpointAsync();
        File.Delete(_trail.CheckpointFile);
        Directory.CreateDirectory(_trail.CheckpointFile);   // opening it as a file fails on every platform

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Complete);
        Assert.Contains(report.Findings!, f => f.Code == "FileUnreadable");
    }

    [Fact]
    public async Task A_change_of_an_existing_row_outside_UpdateAsync_is_refused()
    {
        var ids = await _trail.LogAsync(1);
        using var scope = _trail.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _trail.Chain.WriteAsync(db, async ct =>
        {
            var changed = await db.AuditLog.Where(a => a.Id == ids[0])
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, "changed"), ct);
            return (changed, (IReadOnlyCollection<long>)[ids[0]]);
        }));
        Assert.NotEqual("changed", (await db.AuditLog.AsNoTracking().SingleAsync()).Details);   // rolled back
    }

    [Fact]
    public async Task Key_adoption_writes_the_signed_anchor_and_its_boundary_together()
    {
        // A trail pruned before the key: adoption signs the anchor and writes the genesis boundary in one go, so the
        // unsigned links between them never become findings.
        using var before = new AuditTrailFixture(vaultKey: null);
        await before.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await before.LogAsync(2);
        await before.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);
        await before.LogAsync(1);

        var keyed = new AuditChain(VaultKey, before.CheckpointFile, scopes: before.Scopes);
        await keyed.SealExistingAsync();

        using (var scope = before.Scope())
        {
            var signed = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditCheckpoints
                .Where(c => c.Mac != null).Select(c => c.Kind).ToListAsync();
            Assert.Contains(AuditChain.RetentionKind, signed);
            Assert.Contains(AuditChain.GenesisKind, signed);
        }
        // The keyless process can no longer continue the now signed trail.
        Assert.False(await before.Audit.TryLogAtAsync(DateTime.UtcNow, "admin@local.test", "web", "container.restart",
            "container", "c9", "container-9", details: "late", serverId: "s1"));
        var report = await keyed.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.True(report.CheckpointsSigned);
    }

    [Fact]
    public async Task A_write_that_fails_leaves_neither_row_nor_link()
    {
        await _trail.LogAsync(1);
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _trail.Chain.WriteAsync<int>(db, async ct =>
            {
                db.AuditLog.Add(new AuditLogEntity { Actor = "a", Action = "x" });
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("boom");
            }));
        }

        var report = await _trail.Chain.VerifyAsync();

        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(1, report.RowsChecked);
        Assert.Equal(1, report.LinksChecked);
    }

    [Fact]
    public async Task Existing_rows_are_sealed_once_at_the_first_start()
    {
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.AuditLog.AddRange(new AuditLogEntity { Actor = "a", Action = "old.one" }, new AuditLogEntity { Actor = "b", Action = "old.two" });
            await db.SaveChangesAsync();
        }

        await _trail.Chain.SealExistingAsync();
        await _trail.Chain.SealExistingAsync();   // a second start changes nothing

        var report = await _trail.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(2, report.LinksChecked);
        using var check = _trail.Scope();
        var checkpoints = await check.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditCheckpoints.ToListAsync();
        Assert.Equal(AuditChain.GenesisKind, Assert.Single(checkpoints).Kind);
    }

    [Fact]
    public async Task An_emptied_chain_beside_its_checkpoint_file_is_not_sealed_again()
    {
        // Wiping the chain tables and restarting must not launder the trail into a fresh, "intact" chain.
        await _trail.LogAsync(3);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync("UPDATE AuditLog SET Details = 'rewritten'");
        await _trail.SqlAsync("DELETE FROM AuditChain");
        await _trail.SqlAsync("DELETE FROM AuditCheckpoints");

        await _trail.Chain.SealExistingAsync();

        var report = await _trail.Chain.VerifyAsync();
        Assert.False(report.Intact);
        Assert.Equal(0, report.LinksChecked);
        Assert.Contains(report.Problems, p => p.Contains("its end was cut off"));
        Assert.Contains(report.Problems, p => p.Contains("has no link"));
    }

    [Fact]
    public async Task Without_a_vault_key_checkpoints_are_unsigned_and_say_so()
    {
        // The documented limit: without VAULT_KEY nothing can be signed, so a cut-off end is not detectable.
        using var unsigned = new AuditTrailFixture(vaultKey: null);
        var ids = await unsigned.LogAsync(3);
        await unsigned.Chain.CheckpointAsync();
        await unsigned.SqlAsync($"DELETE FROM AuditLog WHERE Id = {ids[2]}");
        await unsigned.SqlAsync("DELETE FROM AuditChain WHERE Seq = 3");

        var report = await unsigned.Chain.VerifyAsync();

        Assert.False(report.CheckpointsSigned);
        Assert.Null(report.TrustedCheckpointSeq);
        Assert.True(report.Intact);
        using var scope = unsigned.Scope();
        Assert.All(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditCheckpoints.ToListAsync(),
            c => Assert.Null(c.Mac));
    }

    [Fact]
    public void Every_sealed_row_hashes_exactly_these_columns()
    {
        // ADR-0006: every property is in the hash. A new one must be nullable without a default (old rows keep their
        // hash because nulls are left out); a computed or defaulted property would change every sealed row.
        var properties = typeof(AuditLogEntity).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var fresh = new AuditLogEntity();
        var nonNullByDefault = properties.Where(n => typeof(AuditLogEntity).GetProperty(n)!.GetValue(fresh) is not null).ToList();
        Assert.Equal(["Action", "Actor", "ActorType", "Id", "Success", "TargetId", "TargetName", "TargetType", "Timestamp"],
            nonNullByDefault);
        Assert.All(properties, n => Assert.True(typeof(AuditLogEntity).GetProperty(n)!.CanWrite, $"{n} is computed"));
    }

    [Fact]
    public void The_hashes_are_standard_SHA256_over_documented_input()
    {
        // ADR-0006: Hash = SHA256("{prev}|{seq}|{auditId}|{rowHash}") in lower-case hex — checkable without Whiskers.
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{AuditChain.Genesis}|1|7|abc")));
        Assert.Equal(expected, AuditChain.LinkHash(AuditChain.Genesis, 1, 7, "abc"));

        var row = new AuditLogEntity { Id = 7, Actor = "a", Action = "x", Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        var same = new AuditLogEntity { Id = 7, Actor = "a", Action = "x", Timestamp = row.Timestamp.AddTicks(3) };   // below a microsecond
        Assert.Equal(AuditChain.RowHash(row), AuditChain.RowHash(same));
        Assert.NotEqual(AuditChain.RowHash(row), AuditChain.RowHash(new AuditLogEntity
            { Id = 7, Actor = "b", Action = "x", Timestamp = row.Timestamp }));
    }
}

/// <summary>A file-backed SQLite database with the app's audit writer and a chain wired as in the app.</summary>
internal sealed class AuditTrailFixture : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"audit-chain-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly ServiceProvider _sp;

    /// <param name="redacting">Register the sink-redaction interceptor, as the app's database registration does.</param>
    public AuditTrailFixture(string? vaultKey, TimeProvider? time = null, bool redacting = false)
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "metrics.db");
        CheckpointFile = Path.Combine(_dir, "audit-checkpoint.json");
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o =>
        {
            o.UseSqlite($"Data Source={_dbPath}");
            if (redacting) o.AddInterceptors(SinkRedactionInterceptor.Instance);
        });
        services.AddSingleton<IAuditChain>(sp =>
            new AuditChain(vaultKey, CheckpointFile, time: time, scopes: sp.GetRequiredService<IServiceScopeFactory>()));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        Audit = new AuditLogService(Scopes, NullLogger<AuditLogService>.Instance);
    }

    public string CheckpointFile { get; }
    public IServiceScopeFactory Scopes => _sp.GetRequiredService<IServiceScopeFactory>();
    public IAuditChain Chain => _sp.GetRequiredService<IAuditChain>();
    public AuditLogService Audit { get; }

    public IServiceScope Scope() => _sp.CreateScope();

    /// <summary>Writes <paramref name="count"/> rows through the app's audit writer; returns their ids.</summary>
    public async Task<List<long>> LogAsync(int count, DateTime? occurredUtc = null)
    {
        for (var i = 0; i < count; i++)
            Assert.True(await Audit.TryLogAtAsync(occurredUtc ?? DateTime.UtcNow, "admin@local.test", "web",
                "container.restart", "container", $"c{i}", $"container-{i}", details: $"n={i}", serverId: "s1"));
        using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog
            .OrderByDescending(a => a.Id).Take(count).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();
    }

    /// <summary>Tampers with the database directly, as someone with file access would.</summary>
    public async Task SqlAsync(string sql)
    {
        using var scope = Scope();
#pragma warning disable EF1002 // test-only literal SQL
        await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.ExecuteSqlRawAsync(sql);
#pragma warning restore EF1002
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_dbPath);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}

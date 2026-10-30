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
    public async Task A_rewritten_trail_does_not_match_its_checkpoint()
    {
        // A trail rebuilt from scratch with consistent hashes still differs from what was signed.
        await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync("UPDATE AuditLog SET Actor = 'someone-else'");
        await _trail.SqlAsync("DELETE FROM AuditChain");
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
        Assert.True(report.CheckpointsSigned);
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
            await _trail.Chain.WriteAsync(db, async ct =>
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

    public AuditTrailFixture(string? vaultKey, TimeProvider? time = null)
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "metrics.db");
        CheckpointFile = Path.Combine(_dir, "audit-checkpoint.json");
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
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

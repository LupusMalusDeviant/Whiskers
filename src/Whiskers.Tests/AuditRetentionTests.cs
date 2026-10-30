using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>R1-23 / INV-50: audit retention is configurable with a floor, removing old rows is itself an audited
/// event, the trail still verifies after it (a signed retention checkpoint is the chain's new start), and the export
/// carries everything needed to check the trail again outside Whiskers.</summary>
public sealed class AuditRetentionTests : IDisposable
{
    private readonly AuditTrailFixture _trail = new(AuditHashChainTests.VaultKey);

    public void Dispose() => _trail.Dispose();

    [Theory]
    [InlineData(365, 365)]
    [InlineData(730, 730)]
    [InlineData(30, 30)]
    [InlineData(5, 30)]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    public void Retention_is_configurable_but_never_below_the_floor(int configured, int effective) =>
        Assert.Equal(effective, new AuditSettings { RetentionDays = configured }.EffectiveRetentionDays);

    [Fact]
    public void The_default_keeps_a_year()
    {
        Assert.Equal(365, new AuditSettings().EffectiveRetentionDays);
        Assert.Equal(AuditSettings.MinRetentionDays, 30);
    }

    [Fact]
    public async Task Pruning_is_audited_and_the_trail_still_verifies()
    {
        var old = await _trail.LogAsync(3, DateTime.UtcNow.AddDays(-400));
        var young = await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();

        var removed = await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);

        Assert.Equal(3, removed);
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var ids = await db.AuditLog.OrderBy(a => a.Id).Select(a => a.Id).ToListAsync();
            Assert.DoesNotContain(ids, id => old.Contains(id));
            Assert.All(young, id => Assert.Contains(id, ids));
            var record = await db.AuditLog.SingleAsync(a => a.Action == AuditChain.RetentionAction);
            Assert.Equal("system", record.Actor);
            Assert.Contains($"Removed 3 audit row(s) through id {old[^1]}", record.Details);
            Assert.Contains("retention 365 days", record.Details);
            var retention = await db.AuditCheckpoints.SingleAsync(c => c.Kind == AuditChain.RetentionKind);
            Assert.Equal(old[^1], retention.PrunedThroughId);
            Assert.NotNull(retention.Mac);
            // Only the links that named removed rows went, and the head checkpoints that named them.
            Assert.DoesNotContain(await db.AuditChain.Select(c => c.AuditId).ToListAsync(), id => old.Contains(id));
            Assert.DoesNotContain(await db.AuditCheckpoints.ToListAsync(),
                c => c.Kind == AuditChain.HeadKind && c.Seq < retention.Seq);
        }

        var report = await _trail.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(3, report.RowsChecked);   // two young rows + the retention record
    }

    [Fact]
    public async Task After_a_prune_the_checkpoint_file_names_the_newest_checkpoint()
    {
        await _trail.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await _trail.LogAsync(1);
        await _trail.Chain.CheckpointAsync();

        await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);
        await _trail.Chain.CheckpointAsync();   // the timer's next tick: nothing new

        using var scope = _trail.Scope();
        var newest = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditCheckpoints
            .OrderByDescending(c => c.Seq).ThenByDescending(c => c.Id).FirstAsync();
        var file = JsonSerializer.Deserialize<AuditCheckpointEntity>(await File.ReadAllTextAsync(_trail.CheckpointFile))!;
        Assert.Equal(newest.Seq, file.Seq);
        Assert.Equal(newest.Hash, file.Hash);
        Assert.Equal(newest.Mac, file.Mac);
    }

    [Fact]
    public async Task A_signed_trail_is_not_pruned_while_VAULT_KEY_is_missing()
    {
        await _trail.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await _trail.Chain.CheckpointAsync();
        var keyless = new AuditChain(vaultKey: null, _trail.CheckpointFile, scopes: _trail.Scopes);

        Assert.Equal(0, await keyless.PruneAsync(DateTime.UtcNow.AddDays(-365), 365));

        using var scope = _trail.Scope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.CountAsync());
        Assert.True((await _trail.Chain.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task A_retention_run_removes_at_most_one_batch_and_the_next_run_continues()
    {
        // Rows from before the chain (sealed at the first start), enough for more than one batch.
        await _trail.SqlAsync(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < " + (AuditChain.MaxPruneRows + 5) + ") " +
            "INSERT INTO AuditLog (Timestamp, Actor, ActorType, Action, TargetType, TargetId, TargetName, Success) " +
            "SELECT '2025-01-01 00:00:00', 'a', 'web', 'container.restart', 'container', 'c', 'c', 1 FROM n");
        await _trail.Chain.SealExistingAsync();

        Assert.Equal(AuditChain.MaxPruneRows, await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365));
        Assert.Equal(5, await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365));

        var report = await _trail.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(2, report.RowsChecked);   // the two retention records
    }

    [Fact]
    public async Task Nothing_old_means_nothing_removed_and_no_record()
    {
        await _trail.LogAsync(2);

        Assert.Equal(0, await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365));

        using var scope = _trail.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        Assert.False(await db.AuditLog.AnyAsync(a => a.Action == AuditChain.RetentionAction));
        Assert.False(await db.AuditCheckpoints.AnyAsync(c => c.Kind == AuditChain.RetentionKind));
    }

    [Fact]
    public async Task A_young_row_is_never_removed_even_behind_an_older_one()
    {
        // Ids and timestamps need not agree (an event recorded late): the boundary stops at the first young id.
        var first = await _trail.LogAsync(1, DateTime.UtcNow.AddDays(-400));
        var young = await _trail.LogAsync(1);
        var lateOld = await _trail.LogAsync(1, DateTime.UtcNow.AddDays(-400));

        Assert.Equal(1, await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365));

        using var scope = _trail.Scope();
        var ids = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.Select(a => a.Id).ToListAsync();
        Assert.DoesNotContain(first[0], ids);
        Assert.Contains(young[0], ids);
        Assert.Contains(lateOld[0], ids);
        Assert.True((await _trail.Chain.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task Checkpoints_stay_continuous_across_prunes_and_still_catch_tampering()
    {
        await _trail.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await _trail.LogAsync(1);
        await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);
        var later = await _trail.LogAsync(2);
        await _trail.Chain.CheckpointAsync();
        // A second run with nothing old changes nothing.
        Assert.Equal(0, await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365));

        var intact = await _trail.Chain.VerifyAsync();
        Assert.True(intact.Intact, string.Join("; ", intact.Problems));
        Assert.Equal(intact.HeadSeq, intact.TrustedCheckpointSeq);

        // A row deleted after a prune is still reported — retention covers only what it recorded.
        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id = {later[0]}");
        var broken = await _trail.Chain.VerifyAsync();
        Assert.False(broken.Intact);
        Assert.Contains($"Audit row {later[0]} was deleted.", broken.Problems);
    }

    [Fact]
    public async Task A_backdated_row_is_not_pruned_and_no_new_start_is_signed()
    {
        // Someone moves a young row's timestamp into the past, hoping retention removes it and signs the new start.
        var ids = await _trail.LogAsync(3);
        await _trail.Chain.CheckpointAsync();
        await _trail.SqlAsync($"UPDATE AuditLog SET Timestamp = '2024-01-01 00:00:00' WHERE Id = {ids[0]}");

        var removed = await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);

        Assert.Equal(0, removed);
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            Assert.Equal(3, await db.AuditLog.CountAsync());
            Assert.False(await db.AuditCheckpoints.AnyAsync(c => c.Kind == AuditChain.RetentionKind));
            Assert.False(await db.AuditLog.AnyAsync(a => a.Action == AuditChain.RetentionAction));
        }
        var report = await _trail.Chain.VerifyAsync();
        Assert.Contains($"Audit row {ids[0]} was changed after it was written.", report.Problems);
    }

    [Fact]
    public async Task The_MCP_call_history_goes_through_the_same_recorded_retention()
    {
        await _trail.LogAsync(1);
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.McpToolCalls.AddRange(
                new McpToolCallEntity { Timestamp = DateTime.UtcNow.AddDays(-400), Actor = "a", ToolName = "old_tool" },
                new McpToolCallEntity { Timestamp = DateTime.UtcNow, Actor = "a", ToolName = "young_tool" });
            await db.SaveChangesAsync();
        }

        var removed = await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);

        Assert.Equal(0, removed);   // no audit row was old enough
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            Assert.Equal(["young_tool"], await db.McpToolCalls.Select(e => e.ToolName).ToListAsync());
            var record = await db.AuditLog.SingleAsync(a => a.Action == AuditChain.RetentionAction);
            Assert.Contains("Removed 0 audit row(s)", record.Details);
            Assert.Contains("and 1 MCP call history row(s)", record.Details);
        }
        var report = await _trail.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
    }

    [Fact]
    public async Task A_row_changed_after_the_verification_is_caught_under_the_lock()
    {
        // Retention verifies the whole trail outside the chain lock; what changes after that is checked again inside.
        var old = await _trail.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await _trail.LogAsync(1);
        var chain = (AuditChain)_trail.Chain;
        var verified = await chain.VerifyAsync();
        Assert.True(verified.Intact, string.Join("; ", verified.Problems));
        using var scope = _trail.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        Assert.True(await chain.UnchangedSinceAsync(db, verified, old[^1], default));

        await _trail.LogAsync(1);   // written by Whiskers meanwhile: fine
        Assert.True(await chain.UnchangedSinceAsync(db, verified, old[^1], default));

        await _trail.SqlAsync($"UPDATE AuditLog SET Actor = 'x' WHERE Id = {old[0]}");   // an old row, edited meanwhile
        Assert.False(await chain.UnchangedSinceAsync(db, verified, old[^1], default));
    }

    [Fact]
    public async Task A_young_row_deleted_after_the_verification_is_not_excused_by_the_removal()
    {
        // Old, young, old, young: once the first young row is gone, the batch would reach past it.
        var first = await _trail.LogAsync(1, DateTime.UtcNow.AddDays(-400));
        var young = await _trail.LogAsync(1);
        var third = await _trail.LogAsync(1, DateTime.UtcNow.AddDays(-400));
        await _trail.LogAsync(1);
        var chain = (AuditChain)_trail.Chain;
        var verified = await chain.VerifyAsync();
        Assert.True(verified.Intact, string.Join("; ", verified.Problems));

        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id = {young[0]}");

        using var scope = _trail.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        Assert.False(await chain.UnchangedSinceAsync(db, verified, third[0], default));
        Assert.True(first[0] < young[0]);
    }

    [Fact]
    public async Task A_young_row_backdated_with_a_recomputed_unsigned_link_is_not_removed()
    {
        await _trail.LogAsync(1, DateTime.UtcNow.AddDays(-400));
        var young = await _trail.LogAsync(1);
        var third = await _trail.LogAsync(1, DateTime.UtcNow.AddDays(-400));
        var chain = (AuditChain)_trail.Chain;
        var verified = await chain.VerifyAsync();

        await _trail.SqlAsync($"UPDATE AuditLog SET Timestamp = '2024-01-01 00:00:00' WHERE Id = {young[0]}");
        using var scope = _trail.Scope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var link = await db.AuditChain.AsNoTracking().SingleAsync(c => c.AuditId == young[0]);
        var rowHash = AuditChain.RowHash(await db.AuditLog.AsNoTracking().SingleAsync(a => a.Id == young[0]));
        var hash = AuditChain.LinkHash(link.PrevHash, link.Seq, link.AuditId, rowHash);
        await _trail.SqlAsync($"UPDATE AuditChain SET RowHash = '{rowHash}', Hash = '{hash}', Mac = NULL WHERE Seq = {link.Seq}");

        Assert.False(await chain.UnchangedSinceAsync(db, verified, third[0], default));
    }

    [Fact]
    public async Task The_MCP_call_history_is_removed_in_batches_too()
    {
        await _trail.LogAsync(1);
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.McpToolCalls.AddRange(Enumerable.Range(0, AuditChain.MaxPruneRows + 5).Select(i =>
                new McpToolCallEntity { Timestamp = DateTime.UtcNow.AddDays(-400), Actor = "a", ToolName = $"t{i}" }));
            await db.SaveChangesAsync();
        }

        await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);
        using (var scope = _trail.Scope())
            Assert.Equal(5, await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().McpToolCalls.CountAsync());
        await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);
        using (var scope = _trail.Scope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().McpToolCalls.CountAsync());
        Assert.True((await _trail.Chain.VerifyAsync()).Intact);
    }

    [Fact]
    public async Task A_tampered_trail_keeps_its_MCP_call_history_too()
    {
        var ids = await _trail.LogAsync(1);
        using (var scope = _trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.McpToolCalls.Add(new McpToolCallEntity { Timestamp = DateTime.UtcNow.AddDays(-400), Actor = "a", ToolName = "old_tool" });
            await db.SaveChangesAsync();
        }
        await _trail.SqlAsync($"UPDATE AuditLog SET Actor = 'x' WHERE Id = {ids[0]}");

        await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);

        using var check = _trail.Scope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<MetricsDbContext>().McpToolCalls.CountAsync());
    }

    [Fact]
    public async Task A_forged_retention_checkpoint_cannot_hide_deleted_rows()
    {
        var ids = await _trail.LogAsync(3);
        await _trail.Chain.CheckpointAsync();
        // Someone deletes rows and claims retention removed them — without the key the claim is not trusted.
        await _trail.SqlAsync($"DELETE FROM AuditLog WHERE Id = {ids[0]}");
        await _trail.SqlAsync("INSERT INTO AuditCheckpoints (Kind, Seq, Hash, PrunedThroughId, At, Mac) VALUES " +
                              $"('retention', 0, '{AuditChain.Genesis}', {ids[0]}, '2026-01-01 00:00:00', '{new string('a', 64)}')");

        var report = await _trail.Chain.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains($"Audit row {ids[0]} was deleted.", report.Problems);
        Assert.Contains(report.Problems, p => p.Contains("(retention) has an invalid signature"));
    }

    [Fact]
    public async Task The_export_holds_the_report_checkpoints_chain_and_rows_and_checks_out_offline()
    {
        await _trail.LogAsync(2, DateTime.UtcNow.AddDays(-400));
        await _trail.LogAsync(3);
        await _trail.Chain.PruneAsync(DateTime.UtcNow.AddDays(-365), 365);

        using var output = new MemoryStream();
        var report = await _trail.Chain.ExportAsync(output);

        Assert.True(report.Intact, string.Join("; ", report.Problems));
        using var json = JsonDocument.Parse(output.ToArray());
        var root = json.RootElement;
        Assert.Equal("whiskers-audit-export/v1", root.GetProperty("format").GetString());
        // The checkpoint file — the one part outside the database — is in it too.
        var file = root.GetProperty("checkpointFile");
        Assert.Equal(JsonValueKind.Object, file.ValueKind);
        Assert.False(string.IsNullOrEmpty(file.GetProperty("Mac").GetString()));
        Assert.True(root.GetProperty("verification").GetProperty("Intact").GetBoolean());
        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);   // three young rows + the retention record
        Assert.Contains(rows, r => r.GetProperty("Action").GetString() == AuditChain.RetentionAction);
        Assert.Contains(root.GetProperty("checkpoints").EnumerateArray(),
            c => c.GetProperty("Kind").GetString() == AuditChain.RetentionKind);

        // The chain re-checks with nothing but SHA-256 (ADR-0006), starting at the retention checkpoint.
        var anchor = root.GetProperty("checkpoints").EnumerateArray()
            .Last(c => c.GetProperty("Kind").GetString() == AuditChain.RetentionKind);
        var previous = anchor.GetProperty("Hash").GetString();
        foreach (var link in root.GetProperty("chain").EnumerateArray())
        {
            Assert.Equal(previous, link.GetProperty("PrevHash").GetString());
            var expected = AuditChain.LinkHash(previous!, link.GetProperty("Seq").GetInt64(),
                link.GetProperty("AuditId").GetInt64(), link.GetProperty("RowHash").GetString()!);
            Assert.Equal(expected, link.GetProperty("Hash").GetString());
            previous = expected;
        }
    }

    [Fact]
    public async Task The_export_needs_a_stream_it_can_start_over()
    {
        await _trail.LogAsync(1);
        await Assert.ThrowsAsync<ArgumentException>(() => _trail.Chain.ExportAsync(new ForwardOnlyStream()));
    }

    private sealed class ForwardOnlyStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}

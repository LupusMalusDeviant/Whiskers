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
            // Only the links that named removed rows went.
            Assert.DoesNotContain(await db.AuditChain.Select(c => c.AuditId).ToListAsync(), id => old.Contains(id));
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

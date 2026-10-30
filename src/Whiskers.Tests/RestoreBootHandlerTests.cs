using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Services.Backup;

namespace Whiskers.Tests;

/// <summary>The crash-safe deferred restore swap (RestoreBootHandler), exercised directly as file operations —
/// no container restart needed. Covers the happy-path swap (incl. backups/ preservation + stale-WAL deletion),
/// the no-marker no-op, a corrupt marker being abandoned without bricking, and idempotency across boots.</summary>
public class RestoreBootHandlerTests
{
    private static DataPathOptions NewRoot(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"sw-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new DataPathOptions(root);
    }

    private static void WriteMarker(DataPathOptions p)
        => File.WriteAllText(p.RestorePendingMarker,
            JsonSerializer.Serialize(new RestoreMarker { StagingDir = p.RestoreStagingDir, Provider = "sqlite" }));

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Swaps_staging_over_live_preserves_backups_and_removes_stale_wal()
    {
        var p = NewRoot(out var root);
        try
        {
            // Live state
            File.WriteAllText(p.DbPath, "OLD-DB");
            File.WriteAllText(p.DbPath + "-wal", "STALE-WAL");
            File.WriteAllText(Path.Combine(root, "roles.json"), "OLD");
            Directory.CreateDirectory(p.SelfBackupsDir);
            File.WriteAllText(Path.Combine(p.SelfBackupsDir, "pre.tar.gz"), "SAFETY");

            // Staging (an extracted backup)
            Directory.CreateDirectory(p.RestoreStagingDir);
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "metrics.db"), "NEW-DB");
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "roles.json"), "NEW");
            Directory.CreateDirectory(Path.Combine(p.RestoreStagingDir, "keys"));
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "keys", "k.txt"), "K");
            WriteMarker(p);

            RestoreBootHandler.ApplyPendingRestore(p);

            Assert.Equal("NEW-DB", File.ReadAllText(p.DbPath));
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(root, "roles.json")));
            Assert.Equal("K", File.ReadAllText(Path.Combine(root, "keys", "k.txt")));
            Assert.False(File.Exists(Path.Combine(root, "manifest.json")));                 // archive artifact, not copied
            Assert.False(File.Exists(p.DbPath + "-wal"));                                    // stale WAL removed
            Assert.Equal("SAFETY", File.ReadAllText(Path.Combine(p.SelfBackupsDir, "pre.tar.gz"))); // backups/ preserved
            Assert.False(File.Exists(p.RestorePendingMarker));                               // marker consumed last
            Assert.False(Directory.Exists(p.RestoreStagingDir));                             // staging GC'd
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_database_restored_without_its_audit_checkpoint_does_not_keep_the_old_one(bool archiveHasCheckpoint)
    {
        // R1-23: an archive from before the audit chain carries no checkpoint; the live one signs the replaced chain.
        var p = NewRoot(out var root);
        try
        {
            File.WriteAllText(p.DbPath, "OLD-DB");
            File.WriteAllText(p.AuditCheckpointJson, "OLD-CHECKPOINT");
            Directory.CreateDirectory(p.RestoreStagingDir);
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "metrics.db"), "NEW-DB");
            if (archiveHasCheckpoint)
                File.WriteAllText(Path.Combine(p.RestoreStagingDir, "audit-checkpoint.json"), "NEW-CHECKPOINT");
            WriteMarker(p);

            RestoreBootHandler.ApplyPendingRestore(p);

            if (archiveHasCheckpoint)
                Assert.Equal("NEW-CHECKPOINT", File.ReadAllText(p.AuditCheckpointJson));
            else
            {
                Assert.False(File.Exists(p.AuditCheckpointJson));
                var kept = Assert.Single(Directory.GetFiles(root, "audit-checkpoint.json.pre-restore-*"));
                Assert.Equal("OLD-CHECKPOINT", File.ReadAllText(kept));   // kept aside, never deleted
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task An_applied_restore_is_recorded_on_the_next_start_and_kept_until_it_is()
    {
        // R1-23: the restore's own event lived in the replaced database; the note carries it across the swap.
        var p = NewRoot(out var root);
        try
        {
            File.WriteAllText(p.DbPath, "OLD-DB");
            Directory.CreateDirectory(p.RestoreStagingDir);
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "metrics.db"), "NEW-DB");
            File.WriteAllText(p.RestorePendingMarker, JsonSerializer.Serialize(new RestoreMarker
            {
                StagingDir = p.RestoreStagingDir, Provider = "sqlite", Actor = "admin@local.test", Archive = "abc.tar.gz.enc",
                SourceCreatedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            }));

            RestoreBootHandler.ApplyPendingRestore(p);
            Assert.True(File.Exists(p.RestoreAppliedNote));

            // The audit store is down: the note stays for the next start.
            await RestoreBootHandler.RecordAppliedAsync(p, new Audit(works: false));
            Assert.True(File.Exists(p.RestoreAppliedNote));

            var audit = new Audit(works: true);
            await RestoreBootHandler.RecordAppliedAsync(p, audit);
            Assert.False(File.Exists(p.RestoreAppliedNote));
            var row = Assert.Single(audit.Rows);
            Assert.Equal(("admin@local.test", RestoreBootHandler.RestoredAction, "abc.tar.gz.enc"), (row.Actor, row.Action, row.Target));
            Assert.Contains("Archive created 2026-10-01", row.Details);
        }
        finally { Cleanup(root); }
    }

    private sealed class Audit(bool works) : Whiskers.Services.AuditLog.IAuditLogService
    {
        public List<(string Actor, string Action, string Target, string? Details)> Rows { get; } = [];

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) =>
            TryLogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

        public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            if (works) Rows.Add((actor, action, targetName, details));
            return Task.FromResult(works);
        }

        public Task<List<Whiskers.Models.AuditLogEntity>> GetRecentAsync(int count = 100, int skip = 0,
            string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(new List<Whiskers.Models.AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    [Fact]
    public void No_marker_is_a_no_op()
    {
        var p = NewRoot(out var root);
        try
        {
            File.WriteAllText(p.DbPath, "LIVE");
            RestoreBootHandler.ApplyPendingRestore(p);
            Assert.Equal("LIVE", File.ReadAllText(p.DbPath));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Corrupt_marker_is_abandoned_without_swapping()
    {
        var p = NewRoot(out var root);
        try
        {
            File.WriteAllText(p.DbPath, "LIVE");
            Directory.CreateDirectory(p.RestoreStagingDir);
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "metrics.db"), "NEW");
            File.WriteAllText(p.RestorePendingMarker, "{ not valid json");

            RestoreBootHandler.ApplyPendingRestore(p);

            Assert.Equal("LIVE", File.ReadAllText(p.DbPath));                 // no swap on a corrupt marker
            Assert.False(File.Exists(p.RestorePendingMarker));               // moved aside
            Assert.True(File.Exists(p.RestorePendingMarker + ".corrupt"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Second_boot_after_success_does_not_swap_again()
    {
        var p = NewRoot(out var root);
        try
        {
            File.WriteAllText(p.DbPath, "OLD");
            Directory.CreateDirectory(p.RestoreStagingDir);
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "metrics.db"), "NEW");
            WriteMarker(p);

            RestoreBootHandler.ApplyPendingRestore(p);
            Assert.Equal("NEW", File.ReadAllText(p.DbPath));

            File.WriteAllText(p.DbPath, "MUTATED-AFTER");   // normal running writes after the restore
            RestoreBootHandler.ApplyPendingRestore(p);      // no marker now → must not re-swap
            Assert.Equal("MUTATED-AFTER", File.ReadAllText(p.DbPath));
        }
        finally { Cleanup(root); }
    }
}

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
    public void Each_target_is_made_equal_to_the_archive_and_nothing_else_is_touched()
    {
        var p = NewRoot(out var root);
        try
        {
            // Live: stores and directories the archive does not hold, two kept backups, an unknown file, an outbox event.
            File.WriteAllText(p.DbPath, "OLD-DB");
            File.WriteAllText(p.GuardrailsJson, "{}");
            File.WriteAllText(p.SetupTokenFile, "TOKEN");
            File.WriteAllText(Path.Combine(root, "notes.txt"), "MINE");
            Directory.CreateDirectory(p.SshKeysDir);
            File.WriteAllText(Path.Combine(p.SshKeysDir, "id_old"), "OLD-KEY");
            Directory.CreateDirectory(p.AuditOutboxDir);
            File.WriteAllText(Path.Combine(p.AuditOutboxDir, "live-event.json"), "LIVE");
            Directory.CreateDirectory(p.SelfBackupsDir);
            File.WriteAllText(Path.Combine(p.SelfBackupsDir, "abc.tar.gz.enc"), "SAFETY");
            File.WriteAllText(Path.Combine(p.SelfBackupsDir, "abc.meta.json"), "{}");

            // The archive: one store, the outbox with another event, no database (a PostgreSQL instance's backup).
            Directory.CreateDirectory(p.RestoreStagingDir);
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "roles.json"), "NEW");
            Directory.CreateDirectory(Path.Combine(p.RestoreStagingDir, "audit-outbox"));
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "audit-outbox", "archived-event.json"), "ARCHIVED");
            WriteMarker(p);

            RestoreBootHandler.ApplyPendingRestore(p);

            Assert.Equal("NEW", File.ReadAllText(p.RolesJson));
            Assert.False(File.Exists(p.GuardrailsJson));                  // not in the archive: removed
            Assert.False(Directory.Exists(p.SshKeysDir));                 // not in the archive: removed
            Assert.Equal("OLD-DB", File.ReadAllText(p.DbPath));           // the database is only ever replaced
            Assert.Equal("LIVE", File.ReadAllText(Path.Combine(p.AuditOutboxDir, "live-event.json")));       // merged
            Assert.Equal("ARCHIVED", File.ReadAllText(Path.Combine(p.AuditOutboxDir, "archived-event.json")));
            Assert.Equal("TOKEN", File.ReadAllText(p.SetupTokenFile));    // local to this install
            Assert.Equal("MINE", File.ReadAllText(Path.Combine(root, "notes.txt")));
            Assert.Equal("SAFETY", File.ReadAllText(Path.Combine(p.SelfBackupsDir, "abc.tar.gz.enc")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(p.SelfBackupsDir, "abc.meta.json")));
            Assert.False(File.Exists(p.RestorePendingMarker));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void A_swap_interrupted_half_way_is_completed_on_the_next_start()
    {
        var p = NewRoot(out var root);
        try
        {
            Directory.CreateDirectory(p.SelfBackupsDir);
            File.WriteAllText(Path.Combine(p.SelfBackupsDir, "pre.tar.gz.enc"), "SAFETY");
            Directory.CreateDirectory(p.RestoreStagingDir);
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "metrics.db"), "NEW-DB");
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "roles.json"), "NEW");
            Directory.CreateDirectory(Path.Combine(p.RestoreStagingDir, "keys"));
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "keys", "k.xml"), "<k/>");
            Directory.CreateDirectory(Path.Combine(p.RestoreStagingDir, "ssh-keys"));
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "ssh-keys", "id"), "NEW-KEY");
            WriteMarker(p);

            // The state a crash leaves: keys/ already swapped, ssh-keys/ deleted with its copy half made beside it,
            // roles.json and the database still old, an old WAL next to it.
            Directory.CreateDirectory(p.KeysDir);
            File.WriteAllText(Path.Combine(p.KeysDir, "k.xml"), "<k/>");
            Directory.CreateDirectory(p.SshKeysDir + ".__restoring");
            File.WriteAllText(Path.Combine(p.SshKeysDir + ".__restoring", "partial"), "HALF");
            File.WriteAllText(p.RolesJson, "OLD");
            File.WriteAllText(p.DbPath, "OLD-DB");
            File.WriteAllText(p.DbPath + "-wal", "OLD-WAL");
            File.WriteAllText(p.DbPath + "-journal", "OLD-JOURNAL");   // removed like WAL/SHM, strictly

            RestoreBootHandler.ApplyPendingRestore(p);

            Assert.Equal("NEW-DB", File.ReadAllText(p.DbPath));
            Assert.False(File.Exists(p.DbPath + "-wal"));
            Assert.False(File.Exists(p.DbPath + "-journal"));
            Assert.Equal("NEW", File.ReadAllText(p.RolesJson));
            Assert.Equal("NEW-KEY", File.ReadAllText(Path.Combine(p.SshKeysDir, "id")));
            Assert.False(File.Exists(Path.Combine(p.SshKeysDir, "partial")));
            Assert.False(Directory.Exists(p.SshKeysDir + ".__restoring"));
            Assert.Equal("<k/>", File.ReadAllText(Path.Combine(p.KeysDir, "k.xml")));
            Assert.Equal("SAFETY", File.ReadAllText(Path.Combine(p.SelfBackupsDir, "pre.tar.gz.enc")));
            Assert.False(File.Exists(p.RestorePendingMarker));
            Assert.False(Directory.Exists(p.RestoreStagingDir));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Vpn_state_is_replaced_but_never_removed_and_half_made_copies_are_cleaned_up()
    {
        var p = NewRoot(out var root);
        try
        {
            // A running daemon's state the archive cannot know; a copy left by a swap whose marker was removed.
            Directory.CreateDirectory(p.TailscaleStateDir);
            File.WriteAllText(Path.Combine(p.TailscaleStateDir, "tailscaled.state"), "NODE");
            Directory.CreateDirectory(Path.Combine(root, "netbird"));
            File.WriteAllText(p.NetbirdConfigPath, "OLD-NETBIRD");
            Directory.CreateDirectory(p.KeysDir + ".__restoring");
            File.WriteAllText(p.RolesJson + ".__restoring", "HALF");
            Directory.CreateDirectory(p.AuditOutboxDir);
            File.WriteAllText(Path.Combine(p.AuditOutboxDir, "e.json.__restoring"), "HALF");
            Directory.CreateDirectory(p.SelfBackupsDir);
            File.WriteAllText(Path.Combine(p.SelfBackupsDir, "kept.tar.gz.enc"), "A");
            File.WriteAllText(Path.Combine(p.SelfBackupsDir, "kept.meta.json"), "{}");
            File.WriteAllText(Path.Combine(p.SelfBackupsDir, "orphan.meta.json"), "{}");   // its archive never got its name

            Directory.CreateDirectory(Path.Combine(p.RestoreStagingDir, "netbird"));
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "netbird", "config.json"), "NEW-NETBIRD");
            WriteMarker(p);

            RestoreBootHandler.ApplyPendingRestore(p);

            Assert.Equal("NODE", File.ReadAllText(Path.Combine(p.TailscaleStateDir, "tailscaled.state")));
            Assert.Equal("NEW-NETBIRD", File.ReadAllText(p.NetbirdConfigPath));
            Assert.False(Directory.Exists(p.KeysDir + ".__restoring"));
            Assert.False(File.Exists(p.RolesJson + ".__restoring"));
            Assert.False(File.Exists(Path.Combine(p.AuditOutboxDir, "e.json.__restoring")));
            Assert.True(File.Exists(Path.Combine(p.SelfBackupsDir, "kept.meta.json")));
            Assert.False(File.Exists(Path.Combine(p.SelfBackupsDir, "orphan.meta.json")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Key_directories_and_the_vault_are_readable_by_the_owner_only_after_a_restore()
    {
        if (OperatingSystem.IsWindows()) return;   // no file modes
        var p = NewRoot(out var root);
        try
        {
            Directory.CreateDirectory(Path.Combine(p.RestoreStagingDir, "ssh-keys", "sub"));
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "ssh-keys", "sub", "id"), "KEY");
            File.WriteAllText(Path.Combine(p.RestoreStagingDir, "vault.json"), "{}");
            var open = (UnixFileMode)0b111_111_111;
            File.SetUnixFileMode(Path.Combine(p.RestoreStagingDir, "ssh-keys", "sub", "id"), open);
            File.SetUnixFileMode(Path.Combine(p.RestoreStagingDir, "vault.json"), open);
            Directory.CreateDirectory(p.MtlsDir);   // live, not in the archive: removed, not opened up
            WriteMarker(p);

            RestoreBootHandler.ApplyPendingRestore(p);

            const UnixFileMode dir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            const UnixFileMode file = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(dir, File.GetUnixFileMode(p.SshKeysDir));
            Assert.Equal(dir, File.GetUnixFileMode(Path.Combine(p.SshKeysDir, "sub")));
            Assert.Equal(file, File.GetUnixFileMode(Path.Combine(p.SshKeysDir, "sub", "id")));
            Assert.Equal(file, File.GetUnixFileMode(p.VaultJson));
            Assert.False(Directory.Exists(p.MtlsDir));
        }
        finally { Cleanup(root); }
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

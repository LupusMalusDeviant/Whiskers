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

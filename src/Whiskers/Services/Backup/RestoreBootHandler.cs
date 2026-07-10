using System.Text.Json;
using Whiskers.Configuration;

namespace Whiskers.Services.Backup;

/// <summary>
/// Applies a pending restore at the very START of process boot — BEFORE the DI container, the DbContext or the
/// DataProtection key ring are built — so the file swap happens in a fresh process with no open SQLite handle
/// and no in-memory cache. This is the consistency boundary the restore design relies on: <c>BeginRestoreAsync</c>
/// stages the (decrypted, validated) contents and stops the process; the restart policy brings the container
/// back and this handler swaps staging over the live data, after which normal startup migrates the restored DB
/// and rebuilds every cache from the restored files.
///
/// Crash-safety: the commit marker is the ONLY authorisation to swap (staging without a marker is ignored and
/// garbage-collected). Staging stays immutable and each swap step is idempotent, so an interrupted swap is
/// simply retried on the next boot. If the swap cannot complete it THROWS — Program.cs lets it propagate so the
/// process exits and the restart policy retries, rather than booting on half-swapped state (fail-closed). The
/// pre-restore safety backup remains available throughout. NEVER touches <c>backups/</c> (never in staging).
/// </summary>
public static class RestoreBootHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string SwapTempSuffix = ".__restoring";

    public static void ApplyPendingRestore(DataPathOptions dataPaths)
    {
        var marker = dataPaths.RestorePendingMarker;
        var staging = dataPaths.RestoreStagingDir;

        if (!File.Exists(marker))
        {
            // No pending restore: garbage-collect any orphan staging (extraction that never committed) + temps.
            CleanupTransients(dataPaths, includeStaging: true);
            return;
        }

        // A corrupt/unreadable marker must never brick boot — set it aside and continue on the current state.
        RestoreMarker? parsed = null;
        try { parsed = JsonSerializer.Deserialize<RestoreMarker>(File.ReadAllText(marker), JsonOpts); }
        catch (Exception ex) { Console.Error.WriteLine($"[restore] Ignoring unreadable restore marker: {ex.Message}"); }

        if (parsed is null || !Directory.Exists(staging) || IsEmptyDir(staging))
        {
            Console.Error.WriteLine("[restore] Restore marker present but staging is missing/empty — abandoning the restore.");
            MoveAside(marker);
            CleanupTransients(dataPaths, includeStaging: true);
            return;
        }

        Console.Out.WriteLine(
            $"[restore] Applying pending restore from a backup created {parsed.SourceCreatedAtUtc:o} (provider={parsed.Provider}).");

        // Swap each staged top-level entry over the live data. Throws on an unrecoverable failure → process
        // exits → restart retries from the (immutable) staging.
        SwapStagingOverLive(dataPaths, staging);

        // Success: remove the marker LAST (commit consumed), then staging + transient temps.
        TryDelete(marker);
        CleanupTransients(dataPaths, includeStaging: true);
        Console.Out.WriteLine(
            "[restore] Restore applied. Continuing normal startup (migrations + cache warm-up run against the restored data).");
    }

    private static void SwapStagingOverLive(DataPathOptions dataPaths, string staging)
    {
        var root = dataPaths.RootDir;
        Directory.CreateDirectory(root);

        foreach (var dir in Directory.EnumerateDirectories(staging))
            ReplaceDirectory(dir, Path.Combine(root, Path.GetFileName(dir)));

        foreach (var file in Directory.EnumerateFiles(staging))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, SelfBackupFormat.ManifestEntryName, StringComparison.Ordinal))
                continue;   // archive artifact, not a live data file

            ReplaceFile(file, Path.Combine(root, name));

            if (string.Equals(name, SelfBackupFormat.DbEntryName, StringComparison.Ordinal))
            {
                // The restored DB is a clean DELETE-mode file; a stale live WAL/SHM from the OLD database must
                // not be paired with it or SQLite would replay the wrong journal → corruption.
                TryDelete(Path.Combine(root, SelfBackupFormat.DbEntryName + "-wal"));
                TryDelete(Path.Combine(root, SelfBackupFormat.DbEntryName + "-shm"));
            }
        }
    }

    private static void ReplaceFile(string source, string target)
    {
        var tmp = target + SwapTempSuffix;
        Retry(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);   // atomic replace on the same volume
        });
    }

    private static void ReplaceDirectory(string source, string target)
    {
        var tmp = target + SwapTempSuffix;
        Retry(() =>
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
            CopyDirectory(source, tmp);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(tmp, target);               // atomic rename on the same volume
        });
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void CleanupTransients(DataPathOptions dataPaths, bool includeStaging)
    {
        if (includeStaging) TryDeleteDir(dataPaths.RestoreStagingDir);

        var selfDir = dataPaths.SelfBackupsDir;
        if (!Directory.Exists(selfDir)) return;
        TryDeleteDir(Path.Combine(selfDir, ".build"));
        foreach (var f in SafeEnumerate(selfDir, ".decrypt-*")) TryDelete(f);
        foreach (var f in SafeEnumerate(selfDir, ".upload-*")) TryDelete(f);
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static bool IsEmptyDir(string dir)
    {
        try { return !Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return true; }
    }

    private static void MoveAside(string marker)
    {
        try { File.Move(marker, marker + ".corrupt", overwrite: true); }
        catch { TryDelete(marker); }
    }

    // A prior in-process boot (in the test harness) holding a SQLite handle can transiently fail a move/delete
    // on Windows; one short retry clears it. Production is Linux where open files do not block renames.
    private static void Retry(Action action)
    {
        try { action(); }
        catch (IOException)
        {
            Thread.Sleep(150);
            action();   // a second failure propagates → fail-closed (process exits, restart retries)
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }
}

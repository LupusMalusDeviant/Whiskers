using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Services.Persistence;

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
///
/// R1-32: only the targets of <see cref="SelfBackupLayout"/> are written, and each is made equal to the archive — a
/// target the archive does not hold is removed (the database is only replaced, the audit outbox merged, the audit
/// checkpoint kept aside). Afterwards the key directories and the vault file are readable by the app's user only.
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
        catch (Exception ex) { Console.Error.WriteLine($"[restore] Ignoring unreadable restore marker: {Whiskers.Utils.SinkRedaction.Text(ex.Message)}"); }

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
        try { SwapStagingOverLive(dataPaths, staging); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[restore] The restore could not be applied ({Whiskers.Utils.SinkRedaction.Text(ex.Message)}). " +
                $"The start stops and the restore is repeated at the next one; to give it up, remove {dataPaths.RestorePendingMarker} " +
                "(the pre-restore safety backup holds the previous data).");
            throw;
        }

        // R1-23: the restored database no longer holds the restore's own event; the note keeps who restored what
        // until the start-up writes it as a chained audit row (RecordAppliedAsync). Written before the marker goes,
        // so a crash in between repeats the (idempotent) swap and writes the note again.
        parsed.AppliedAtUtc = DateTime.UtcNow;
        var note = dataPaths.RestoreAppliedNote;
        File.WriteAllText(note + ".tmp", JsonSerializer.Serialize(parsed));
        File.Move(note + ".tmp", note, overwrite: true);

        // Success: remove the marker LAST (commit consumed), then staging + transient temps.
        TryDelete(marker);
        CleanupTransients(dataPaths, includeStaging: true);
        Console.Out.WriteLine(
            "[restore] Restore applied. Continuing normal startup (migrations + cache warm-up run against the restored data).");
    }

    /// <summary>The audit action of an applied restore.</summary>
    public const string RestoredAction = "selfbackup.restored";

    /// <summary>Writes the audit row of a restore applied at this start (after the audit chain is sealed), then removes
    /// the note. Without the row the note stays and the next start tries again.</summary>
    public static async Task RecordAppliedAsync(DataPathOptions dataPaths, Whiskers.Services.AuditLog.IAuditLogService audit,
        ILogger? logger = null)
    {
        var note = dataPaths.RestoreAppliedNote;
        if (!File.Exists(note)) return;
        RestoreMarker? applied = null;
        try { applied = JsonSerializer.Deserialize<RestoreMarker>(await File.ReadAllTextAsync(note), JsonOpts); }
        catch (Exception ex) when (ex is IOException or JsonException) { logger?.LogError(ex, "Restore note unreadable: {Path}", note); }
        var details = applied is null
            ? "A restore was applied; its note could not be read."
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Archive created {applied.SourceCreatedAtUtc:O}; restore committed {applied.CreatedAtUtc:O}; applied {applied.AppliedAtUtc:O}; provider={applied.Provider}. Audit events after the archive was taken are not in this database (the pre-restore safety backup holds them).");
        var actor = string.IsNullOrEmpty(applied?.Actor) ? "(unknown)" : applied.Actor;
        if (await audit.TryLogAsync(actor, "web", RestoredAction, "whiskers", "self-backup",
                string.IsNullOrEmpty(applied?.Archive) ? "Self-backup" : applied.Archive, details))
            TryDelete(note);
        else
            logger?.LogError("AUDIT ROW OF THE APPLIED RESTORE NOT WRITTEN — kept {Path} for the next start", note);
    }

    private static void SwapStagingOverLive(DataPathOptions dataPaths, string staging)
    {
        var root = dataPaths.RootDir;
        Directory.CreateDirectory(root);

        foreach (var name in SelfBackupLayout.Directories)
        {
            var staged = Path.Combine(staging, name);
            var live = Path.Combine(root, name);
            if (name == SelfBackupLayout.AuditOutbox)
            {
                if (Directory.Exists(staged)) MergeDirectory(staged, live);
            }
            else if (Directory.Exists(staged)) ReplaceDirectory(staged, live);
            else if (Directory.Exists(live) && !SelfBackupLayout.KeptWhenAbsent.Contains(name))
                Retry(() => Directory.Delete(live, recursive: true));
        }

        foreach (var name in SelfBackupLayout.Files)
        {
            var staged = Path.Combine(staging, name);
            var live = Path.Combine(root, name);
            // R1-35: a JSON store's previous generation belongs to the replaced data; left in place it would come back
            // the first time the restored file cannot be read, or bring back a store the archive does not hold.
            var previous = live + DurableFile.BackupSuffix;
            Retry(() => { if (File.Exists(previous)) File.Delete(previous); });
            if (File.Exists(staged))
            {
                ReplaceFile(staged, live);
                if (name == SelfBackupFormat.DbEntryName)
                {
                    // The restored DB is a clean DELETE-mode file; a stale live WAL/SHM from the OLD database must
                    // not be paired with it or SQLite would replay the wrong journal → corruption.
                    // A failure throws like any other step: a stale journal must never meet the restored file.
                    foreach (var suffix in new[] { "-wal", "-shm", "-journal" })   // a hot journal would be rolled back into it
                    {
                        var journal = Path.Combine(root, SelfBackupFormat.DbEntryName + suffix);
                        Retry(() => { if (File.Exists(journal)) File.Delete(journal); });
                    }
                }
            }
            else if (name != SelfBackupFormat.DbEntryName && name != SelfBackupLayout.AuditCheckpoint && File.Exists(live))
                Retry(() => File.Delete(live));
        }

        // R1-23: a restored database comes with the audit checkpoint taken with it. An archive from before the audit
        // chain has none: the live file signs the replaced database's chain and would make the restored one look
        // tampered with (and stop it from being sealed). Kept aside, never deleted.
        var checkpointName = Path.GetFileName(dataPaths.AuditCheckpointJson);
        if (File.Exists(Path.Combine(staging, SelfBackupFormat.DbEntryName))
            && !File.Exists(Path.Combine(staging, checkpointName))
            && File.Exists(dataPaths.AuditCheckpointJson))
        {
            var kept = $"{dataPaths.AuditCheckpointJson}.pre-restore-{DateTime.UtcNow:yyyyMMddHHmmss}";
            Retry(() => File.Move(dataPaths.AuditCheckpointJson, kept));
            Console.Out.WriteLine($"[restore] The archive has no audit checkpoint; the current one was kept as {kept}.");
        }

        // Throws like any other step; the caller names the way out.
        RestrictSecrets(root);
    }

    // Owner-only for what holds private keys or the vault, whatever the archive or an earlier install left. A failure
    // throws like any other swap step: the next start repeats it rather than running with readable keys.
    private static void RestrictSecrets(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        foreach (var name in SelfBackupLayout.SecretDirectories)
        {
            var dir = Path.Combine(root, name);
            if (!Directory.Exists(dir)) continue;
            Restrict(dir, dirMode);
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
                Restrict(sub, dirMode);
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                Restrict(file, fileMode);
        }
        foreach (var name in SelfBackupLayout.SecretFiles)
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file)) Restrict(file, fileMode);
        }
    }

    // A mode that grants nothing beyond the owner is left as it is: only a path that is too open needs chmod, so a
    // mount that refuses chmod fails the swap only when it would really leave a key readable.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Restrict(string path, UnixFileMode allowed)
    {
        if ((File.GetUnixFileMode(path) & ~allowed) != 0) File.SetUnixFileMode(path, allowed);
    }

    // Each outbox file is one audit event no database holds yet: the archive's are added, the live ones stay.
    private static void MergeDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            if (!File.Exists(dest)) ReplaceFile(file, dest);
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

        // Copies a swap left half made (only reached without a pending restore, or after one completed), at the
        // root and in the merged outbox.
        foreach (var dir in new[] { dataPaths.RootDir, dataPaths.AuditOutboxDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var d in SafeEnumerateDirs(dir, "*" + SwapTempSuffix)) TryDeleteDir(d);
            foreach (var f in SafeEnumerate(dir, "*" + SwapTempSuffix)) TryDelete(f);
        }

        // R1-35: a durable write cut off before its rename leaves its own temporary file; nothing reads it. This runs
        // before any store is opened.
        if (Directory.Exists(dataPaths.RootDir))
            foreach (var f in SafeEnumerate(dataPaths.RootDir, "*" + DurableFile.TempSuffix, SearchOption.AllDirectories)) TryDelete(f);

        var selfDir = dataPaths.SelfBackupsDir;
        if (!Directory.Exists(selfDir)) return;
        TryDeleteDir(Path.Combine(selfDir, ".build"));
        foreach (var f in SafeEnumerate(selfDir, ".decrypt-*")) TryDelete(f);
        foreach (var f in SafeEnumerate(selfDir, ".upload-*")) TryDelete(f);

        // A sidecar whose archive never got its name (a crash between the two): listed by nothing, pruned by nothing.
        foreach (var sidecar in SafeEnumerate(selfDir, "*.meta.json"))
        {
            var id = Path.GetFileName(sidecar)[..^".meta.json".Length];
            if (!File.Exists(Path.Combine(selfDir, id + ".tar.gz")) && !File.Exists(Path.Combine(selfDir, id + ".tar.gz.enc")))
                TryDelete(sidecar);
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
    {
        try { return Directory.EnumerateFiles(dir, pattern, option).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateDirs(string dir, string pattern)
    {
        try { return Directory.EnumerateDirectories(dir, pattern).ToList(); }
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

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Utils;

namespace Whiskers.Services.Backup;

/// <summary>
/// Read-backs of the backup capabilities (R1-37, INV-43): an archive that was written must be listed and on disk and not
/// empty; one that was deleted must be gone; a retention must have left no more than it keeps. The "run now" button of a
/// scheduled task answers with its own summary — it runs the task's call again under this capability, and that call is
/// the one verified, so the outer one is recorded as not verifiable on its own.
/// </summary>
internal static partial class BackupReadBack
{
    public const string RunNow = "the scheduled run's own call is verified; this summary of it is not.";

    /// <summary>A one-file name a service wrote (no path): anything else names nothing to read.</summary>
    public static string SafeFile(string? name, string what) =>
        name is not null && SafeName().IsMatch(name)
            ? name
            : throw new MutationNotVerifiableException($"the call named no valid {what} to read back.");

    /// <summary>The file on the host is there and not empty.</summary>
    public static async Task RequireFileAsync(IServiceProvider services, string server, string path, CancellationToken ct)
    {
        var test = await VerifierSupport.HostReadAsync(services, server, $"test -s {ShellUtils.Quote(path)}", ct);
        if (test.ExitCode is not (0 or 1))
            throw new MutationVerificationFailedException($"the host could not check '{path}' (exit code {test.ExitCode}).", stateUnknown: true);
        if (!test.Success) throw new MutationVerificationFailedException($"'{path}' is missing or empty on the host.");
    }

    /// <summary>The file on the host is gone.</summary>
    public static async Task RequireGoneAsync(IServiceProvider services, string server, string path, CancellationToken ct)
    {
        var test = await VerifierSupport.HostReadAsync(services, server, $"test -e {ShellUtils.Quote(path)}", ct);
        if (test.ExitCode is not (0 or 1))
            throw new MutationVerificationFailedException($"the host could not check '{path}' (exit code {test.ExitCode}).", stateUnknown: true);
        if (test.Success) throw new MutationVerificationFailedException($"'{path}' is still on the host.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*\z")]
    private static partial Regex SafeName();
}

/// <summary>selfbackup.create: the archive the call returned is listed and on disk, not empty.</summary>
public sealed class SelfBackupCreateVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "selfbackup.create";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        if (result is not SelfBackupInfo info) throw new MutationNotVerifiableException(BackupReadBack.RunNow);
        var backups = Services.GetRequiredService<IBackupService>();
        var path = backups.ResolveArchivePath(info.Id);
        if (path is null || !File.Exists(path))
            throw new MutationVerificationFailedException($"self-backup {info.Id} is not on disk.");
        if (new FileInfo(path).Length == 0)
            throw new MutationVerificationFailedException($"self-backup {info.Id} is empty.");
        if ((await backups.ListBackupsAsync(ct)).All(b => b.Id != info.Id))
            throw new MutationVerificationFailedException($"self-backup {info.Id} is not listed.");
    }
}

/// <summary>selfbackup.delete: the archive is no longer on disk. A retention run ("run now" with retention) prunes
/// through selfbackup.prune, which is verified there.</summary>
public sealed class SelfBackupDeleteVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "selfbackup.delete";

    protected override Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var archive = VerifierSupport.Arg(context, "archive")
                      ?? throw new MutationNotVerifiableException(BackupReadBack.RunNow);
        var file = BackupReadBack.SafeFile(archive, "archive");
        var id = file.EndsWith(".tar.gz.enc", StringComparison.Ordinal) ? file[..^".tar.gz.enc".Length]
            : file.EndsWith(".tar.gz", StringComparison.Ordinal) ? file[..^".tar.gz".Length]
            : file;
        if (Services.GetRequiredService<IBackupService>().ResolveArchivePath(id) is { } path && File.Exists(path))
            throw new MutationVerificationFailedException($"self-backup {id} is still on disk.");
        return Task.CompletedTask;
    }
}

/// <summary>selfbackup.prune: no more than the kept number of self-backups remain (pre-upgrade backups are kept apart).</summary>
public sealed class SelfBackupPruneVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "selfbackup.prune";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var keep = VerifierSupport.LongArg(context, "keep");
        // A negative count prunes nothing (BackupService.PruneAsync): there is no effect to read back.
        if (keep is not >= 0) throw new MutationNotVerifiableException("the call named no number to keep.");
        // A backup taken between the prune and this count can make a legitimate retention look short; accepted, as for
        // db.backup_delete: it fails towards "check again", never towards success.
        var left = (await Services.GetRequiredService<IBackupService>().ListBackupsAsync(ct))
            .Count(b => !b.Label.StartsWith(BackupService.PreUpgradeLabelPrefix, StringComparison.Ordinal));
        if (left > keep)
            throw new MutationVerificationFailedException($"{left} self-backups remain, more than the {keep} kept.");
    }
}

/// <summary>db.backup: the dump the call reports is on the host and not empty. The path is in the answer (a page's or a
/// task's result tuple, the MCP tool's "File:" line).</summary>
public sealed partial class DbBackupVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "db.backup";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var path = DumpPath(result) ?? throw new MutationNotVerifiableException(BackupReadBack.RunNow);
        var dir = Services.GetRequiredService<DataPathOptions>().BackupsDir.TrimEnd('/') + "/";
        if (!path.StartsWith(dir, StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal))
            throw new MutationNotVerifiableException("the reported dump is outside the backup directory.");
        await BackupReadBack.RequireFileAsync(Services, RequireServer(context), path, ct);
    }

    internal static string? DumpPath(object? result)
    {
        // (bool Success, string FilePath, long SizeBytes, string Error) from IDatabaseService.BackupDatabaseAsync.
        if (result is ITuple { Length: 4 } tuple && tuple[1] is string file && file.Length > 0) return file;
        var text = VerifierSupport.ResultText(result);
        return text is null ? null : FileLine().Match(text) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"^\s*File:\s*(\S.*)$", RegexOptions.Multiline)]
    private static partial Regex FileLine();
}

/// <summary>db.backup_delete: no more than the kept number of dumps of the database remain on the host.</summary>
public sealed partial class DbBackupDeleteVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "db.backup_delete";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var database = VerifierSupport.Arg(context, "database");
        var keep = VerifierSupport.LongArg(context, "keep");
        if (database is null || keep is null) throw new MutationNotVerifiableException(BackupReadBack.RunNow);
        if (!SafeDb().IsMatch(database))
            throw new MutationNotVerifiableException("the call named no valid database to read back.");
        var dir = Services.GetRequiredService<DataPathOptions>().BackupsDir;
        // The directory is listed as a whole (a glob that matches nothing is no error, an unreadable directory is) and
        // filtered like the retention's glob {database}_*.sql*. A backup taken meanwhile can make a legitimate
        // retention look short; that race is accepted: it fails towards "check again", never towards success.
        var listing = await VerifierSupport.HostOutputAsync(Services, RequireServer(context), $"ls -1 {ShellUtils.Quote(dir)}", ct);
        var left = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(f => f.StartsWith(database + "_", StringComparison.Ordinal)
                        && f.IndexOf(".sql", database.Length + 1, StringComparison.Ordinal) >= 0);
        if (left > keep)
            throw new MutationVerificationFailedException($"{left} dumps of '{database}' remain, more than the {keep} kept.");
    }

    // The same charset the retention itself accepts before the name enters a shell glob.
    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex SafeDb();
}

/// <summary>volume.backup: the archive is recorded and on the host, not empty.</summary>
public sealed class VolumeBackupVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "volume.backup";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        if (result is not VolumeBackupEntity backup) throw new MutationNotVerifiableException(BackupReadBack.RunNow);
        var file = BackupReadBack.SafeFile(backup.FileName, "archive");
        var server = backup.ServerId is { Length: > 0 } s ? s : RequireServer(context);
        var recorded = await Services.GetRequiredService<IVolumeBackupService>().ListBackupsAsync(server, backup.VolumeName);
        if (recorded.All(b => b.BackupId != backup.BackupId))
            throw new MutationVerificationFailedException($"volume backup {backup.BackupId} is not recorded.");
        var dir = Services.GetRequiredService<DataPathOptions>().BackupsDir;
        await BackupReadBack.RequireFileAsync(Services, server, $"{dir}/{file}", ct);
    }
}

/// <summary>volume.backup_delete: the archive is no longer recorded, and no longer on the host.</summary>
public sealed class VolumeBackupDeleteVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "volume.backup_delete";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var backupId = VerifierSupport.Arg(context, "backupId");
        var fileName = VerifierSupport.Arg(context, "fileName");
        if (backupId is null || fileName is null) throw new MutationNotVerifiableException(BackupReadBack.RunNow);
        var file = BackupReadBack.SafeFile(fileName, "archive");
        var server = RequireServer(context);
        var recorded = await Services.GetRequiredService<IVolumeBackupService>()
            .ListBackupsAsync(server, VerifierSupport.Arg(context, "volumeName"));
        if (recorded.Any(b => b.BackupId == backupId))
            throw new MutationVerificationFailedException($"volume backup {backupId} is still recorded.");
        var dir = Services.GetRequiredService<DataPathOptions>().BackupsDir;
        await BackupReadBack.RequireGoneAsync(Services, server, $"{dir}/{file}", ct);
    }
}

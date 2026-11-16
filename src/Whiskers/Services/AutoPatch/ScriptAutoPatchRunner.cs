using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Services.Server;

namespace Whiskers.Services.AutoPatch;

/// <summary>
/// Runs <c>img-update.sh</c> on the container's host.
///
/// <para><b>Why a host script and not <c>RecreateContainerAsync</c>.</b> The Docker-API recreate copies the old
/// container's <c>Env</c> and <c>Cmd</c> — which include the values baked into the OLD image (its
/// <c>PATH</c>, its <c>PG_VERSION</c>) — and drops what compose added on top (healthcheck, user, stop
/// signal). Fine for a manual one-off, too blind for an unattended night. Compose recreates the service from
/// the same file it was deployed from, and its config hash tells whether that file still matches what runs.
/// The script is the procedure that patched the fleet by hand on 2026-10-25, unchanged in its guards.</para>
///
/// <para>Installed on first use and re-installed whenever its hash differs, so a Whiskers update ships a new
/// script without anybody copying files around. The hash is checked again before every run.</para>
/// </summary>
public sealed class ScriptAutoPatchRunner : IAutoPatchRunner
{
    internal const string ResourceName = "Whiskers.AutoPatch.img-update.sh";
    internal const string RemotePath = "/root/.whiskers/img-update.sh";
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant);

    private readonly IHostCommandExecutor _executor;
    private readonly ILogger<ScriptAutoPatchRunner> _logger;
    private readonly Lazy<(string Base64, string Sha256)> _script = new(LoadScript);

    public ScriptAutoPatchRunner(IHostCommandExecutor executor, ILogger<ScriptAutoPatchRunner> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<(AutoPatchOutcome Outcome, string LogTail)> PatchAsync(
        string serverId, string containerName, bool dryRun, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        // The name ends up on a root shell. Docker's own name rule is narrower than this check, so a
        // legitimate container never fails it — anything that does was not produced by Docker.
        if (!SafeName.IsMatch(containerName ?? ""))
            throw new ArgumentException($"Not a container name: '{containerName}'", nameof(containerName));

        var command = BuildCommand(containerName!, dryRun, _script.Value.Base64, _script.Value.Sha256);

        // A database dump plus the health wait runs for minutes; well past the default.
        var result = await _executor.ExecuteAsync(serverId, command, TimeSpan.FromMinutes(15), ct);
        var log = string.IsNullOrEmpty(result.Output) ? result.Error : result.Output;
        var outcome = AutoPatchOutcome.Parse(log);

        if (outcome.Kind == AutoPatchResultKind.Unknown)
            _logger.LogWarning("Auto-patch of {Container} on {Server} ended without a result (exit {Exit})",
                containerName, serverId, result.ExitCode);

        // Only after a verified update: the container runs the new image healthily, so its older backup tags are what
        // OldImageCleanupPolicy decides on. Best effort — a failed cleanup never turns a good update into a failure.
        if (!dryRun && outcome.Kind == AutoPatchResultKind.Updated && BackupRepository(log, containerName!) is { } repo)
        {
            // Its own short time limit, not the caller's token: a cancellation now (a stop, the preview deadline) must not
            // turn the update that already happened into an exception that loses its outcome and history row.
            using var pruneTime = new CancellationTokenSource(PruneTimeout);
            try { await PruneBackupTagsAsync(serverId, containerName!, repo, pruneTime.Token); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Old backup tags of {Container} on {Server} were not pruned", containerName, serverId);
            }
        }

        return (outcome, Tail(log, 3000));
    }

    /// <summary>The time the backup-tag cleanup may take after a verified update.</summary>
    internal static readonly TimeSpan PruneTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The clock the grace period is measured on (tests).</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>The repository the script tagged the previous image in (<c>backup_tag=&lt;repo&gt;:pre-upd-…-&lt;c&gt;</c>),
    /// or null when it tagged nothing.</summary>
    internal static string? BackupRepository(string? log, string containerName)
    {
        var match = Regex.Match(log ?? "", @"^backup_tag=(?<repo>\S+):pre-upd-\d{8}-" + Regex.Escape(containerName) + @"\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["repo"].Value : null;
    }

    private const string InUseMark = "--in-use--";

    /// <summary>
    /// Removes this container's older <c>pre-upd-&lt;yyyyMMdd&gt;-&lt;c&gt;</c> backup tags that
    /// <see cref="ImageUpdate.OldImageCleanupPolicy"/> lets go: never the newest (the way back from the update just
    /// made), never a database's, never an image any container on the host still uses (running or stopped), never one
    /// inside the grace period — counted from the end of the tag's day, so a day-granular tag is never judged too old.
    /// Removing a tag only untags (<c>docker image rm</c> without <c>-f</c>); Docker itself refuses an image a container
    /// uses.
    /// </summary>
    private async Task<IReadOnlyList<string>> PruneBackupTagsAsync(string serverId, string containerName, string repo,
        CancellationToken ct)
    {
        var listing = await _executor.ExecuteAsync(serverId,
            $"docker image ls --no-trunc --format '{{{{.Tag}}}} {{{{.ID}}}}' {Whiskers.Utils.ShellUtils.Quote(repo)} " +
            // Every step must succeed and the host must list containers (the patched one at least): a failed or empty
            // listing would read as "no image is in use" and is no basis to remove anything.
            $"&& echo {InUseMark} && ids=$(docker ps -aq --no-trunc) && [ -n \"$ids\" ] && docker inspect --format '{{{{.Image}}}}' $ids",
            TimeSpan.FromSeconds(30), ct);
        if (!listing.Success) return [];

        var lines = (listing.Output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mark = Array.IndexOf(lines, InUseMark);
        if (mark < 0) return [];   // the in-use half is missing: nothing is decided without it
        var inUse = lines[(mark + 1)..].ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inUse.Count == 0) return [];   // the patched container runs an image: an empty list is a failed read
        var tagPattern = new Regex(@"^pre-upd-(?<day>\d{8})-" + Regex.Escape(containerName) + @"$", RegexOptions.CultureInvariant);
        var backups = lines[..mark]
            .Select(l => l.Split(' ', 2))
            .Where(p => p.Length == 2)
            .Select(p => (Tag: p[0], Id: p[1], Match: tagPattern.Match(p[0])))
            .Where(b => b.Match.Success)
            .Select(b => (b.Tag, b.Id, Day: DateTime.TryParseExact(b.Match.Groups["day"].Value, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var day) ? day : (DateTime?)null))
            .Where(b => b.Day is not null)
            .OrderByDescending(b => b.Day)
            .ToList();

        var isDatabase = Whiskers.Services.Database.DatabaseDetector.DetectType(repo) != Whiskers.Models.DatabaseType.None;
        var removed = new List<string>();
        foreach (var backup in backups.Skip(1))   // the newest stays: the way back from the update just made
        {
            var since = UtcNow() - backup.Day!.Value.AddDays(1);
            var decision = ImageUpdate.OldImageCleanupPolicy.Evaluate(isDatabase, since, containerHealthy: true,
                imageStillInUse: inUse.Contains(backup.Id));
            if (!decision.MayDelete)
            {
                _logger.LogDebug("Keeping {Repo}:{Tag}: {Reason}", repo, backup.Tag, decision.Reason);
                continue;
            }
            var reference = $"{repo}:{backup.Tag}";
            var rm = await _executor.ExecuteAsync(serverId,
                $"docker image rm {Whiskers.Utils.ShellUtils.Quote(reference)}", TimeSpan.FromSeconds(30), ct);
            if (rm.Success)
            {
                removed.Add(reference);
                _logger.LogInformation("Removed old backup tag {Reference} on {Server}: {Reason}", reference, serverId, decision.Reason);
            }
            else
                _logger.LogWarning("Old backup tag {Reference} on {Server} was not removed (exit {Exit})", reference, serverId, rm.ExitCode);
        }
        return removed;
    }

    /// <summary>One round trip: install if the hash differs, verify, run, print the log. Internal for tests.</summary>
    internal static string BuildCommand(string containerName, bool dryRun, string scriptBase64, string sha256)
    {
        var dir = Path.GetDirectoryName(RemotePath)!.Replace('\\', '/');
        var sb = new StringBuilder();
        sb.Append($"install -d -m 700 {dir}; ");
        sb.Append($"if [ \"$(sha256sum {RemotePath} 2>/dev/null | cut -c1-64)\" != \"{sha256}\" ]; then ");
        sb.Append($"echo {scriptBase64} | base64 -d > {RemotePath}.tmp && chmod 700 {RemotePath}.tmp && mv {RemotePath}.tmp {RemotePath}; fi; ");
        sb.Append($"if [ \"$(sha256sum {RemotePath} | cut -c1-64)\" != \"{sha256}\" ]; then echo 'UPD-ABORT script-integrity'; exit 3; fi; ");
        sb.Append(dryRun ? "DRY_RUN=1 " : "");
        sb.Append($"/bin/bash {RemotePath} {containerName}; ");
        sb.Append($"tail -c 6000 /var/log/img-update-{containerName}.log");
        return sb.ToString();
    }

    /// <summary>The embedded script with LF line endings — a Windows checkout would otherwise hand bash a
    /// CRLF file, and <c>then\r</c> is a syntax error.</summary>
    internal static (string Base64, string Sha256) LoadScript()
    {
        using var stream = typeof(ScriptAutoPatchRunner).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var bytes = Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("\r\n", "\n"));
        return (Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static string Tail(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[^max..];
}

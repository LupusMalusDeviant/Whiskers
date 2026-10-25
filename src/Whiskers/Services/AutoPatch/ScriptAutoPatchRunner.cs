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
    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

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

        return (outcome, Tail(log, 3000));
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

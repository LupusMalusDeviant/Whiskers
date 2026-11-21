using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Services.Onboarding;

/// <summary>Contract 0018 part D: gives Whiskers SSH access to a freshly created cloud VM.</summary>
public interface ICloudBootstrap
{
    BootstrapMethod Method { get; }

    /// <summary>Gives Whiskers SSH access to a freshly created VM. Runs only inside "server.onboard". Without options there
    /// is no confirmed VM name (precondition 1).</summary>
    [MutatingOperation("server.onboard")]
    Task<CloudBootstrapResult> PrepareAsync(Models.ServerConfig server, IProgress<OnboardingProgress> progress, CancellationToken ct) =>
        PrepareAsync(server, new OnboardingOptions(Method), progress, ct);

    /// <summary>As <see cref="PrepareAsync(Models.ServerConfig, IProgress{OnboardingProgress}, CancellationToken)"/>, with the
    /// run's options: <see cref="OnboardingOptions.ConfirmVmName"/> is what precondition 1 compares with the VM's name.</summary>
    [MutatingOperation("server.onboard")]
    Task<CloudBootstrapResult> PrepareAsync(Models.ServerConfig server, OnboardingOptions options,
        IProgress<OnboardingProgress> progress, CancellationToken ct);
}

/// <summary>What a cloud bootstrap did: success or why not, the keys read from the host's disk (pinned), the injected
/// public key's fingerprint, whether the line stays on the host (standing bootstrap key), and what the final clean-up
/// could not undo.</summary>
public sealed record CloudBootstrapResult(bool Success, string? Error, SshHostKeyPin? HostKeyFromDisk,
    string? InjectedKeyFingerprint, bool StandingBootstrapKey, IReadOnlyList<string> CleanupWarnings);

/// <summary>Contract 0018 part D: removes the bootstrap lines of a server from its <c>/root/.ssh/authorized_keys</c>
/// before <c>server.remove</c> takes the record.</summary>
public interface IBootstrapCleanup
{
    /// <summary>Runs the fixed removal command of <see cref="RescueCommands.RemoveMarkerLines"/> through the host executor
    /// (the mTLS host shell when the host has it, otherwise SSH with the generated key), bounded by 30 seconds. A timeout
    /// or a host that cannot be reached is reported as unreachable, never thrown.</summary>
    [MutatingOperation("server.remove")]
    Task<BootstrapCleanupResult> RemoveMarkerLinesAsync(Models.ServerConfig server, CancellationToken ct);
}

/// <param name="Removed">No bootstrap line of the server is left on the host (read back).</param>
/// <param name="Error">Why not, without any secret.</param>
public sealed record BootstrapCleanupResult(bool Removed, string? Error);

/// <inheritdoc cref="IBootstrapCleanup" />
public sealed class BootstrapCleanup : IBootstrapCleanup
{
    /// <summary>The host command's own bound inside <c>server.remove</c>'s short deadline.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private readonly IHostCommandExecutor _exec;
    private readonly ISshHostKeyPins? _pins;

    public BootstrapCleanup(IHostCommandExecutor exec, ISshHostKeyPins? pins = null)
    {
        _exec = exec;
        _pins = pins;
    }

    public async Task<BootstrapCleanupResult> RemoveMarkerLinesAsync(Models.ServerConfig server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Bound);
        try
        {
            // INV-63: an SSH record is reached only against a known host key. Without the pins service, or without a pin
            // or known_hosts line to verify against, no SSH command is sent (accept-new could trust a rescue system's key):
            // the caller reports the dead line and the removal goes on.
            if (server.ConnectionType == ConnectionType.SSH)
            {
                if (_pins is null) return new BootstrapCleanupResult(false, "no host-key service; no SSH command sent");
                if (await _pins.BackfillAsync(server, cts.Token) is null)
                    return new BootstrapCleanupResult(false, "no pinned host key to verify against; no SSH command sent");
                await _pins.VerifyAsync(server, cts.Token);
            }
            return Judge(await _exec.ExecuteAsync(server.Id, RescueCommands.RemoveMarkerLines(server.Id), Bound, cts.Token));
        }
        catch (SshHostKeyMismatchException ex)
        {
            return new BootstrapCleanupResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            return new BootstrapCleanupResult(false, $"host not reachable ({ex.GetType().Name})");
        }
    }

    /// <summary>The removal's answer: removed only when the command succeeded and printed the clean marker.</summary>
    public static BootstrapCleanupResult Judge(CommandResult r) =>
        r.Success && r.Output.Contains(RescueCommands.Clean, StringComparison.Ordinal)
            ? new BootstrapCleanupResult(true, null)
            : new BootstrapCleanupResult(false, Whiskers.Utils.SecretRedactor.Redact(
                string.IsNullOrWhiteSpace(r.Error) ? $"exit {r.ExitCode}" : r.Error.Trim()));

    /// <summary>The standing-key warning (contract 0018), with the public fingerprint only.</summary>
    public static string StandingKeyWarning(string fingerprint, string serverName) =>
        $"standing bootstrap key {fingerprint} in /root/.ssh/authorized_keys of {serverName}: resume the onboarding, abandon it, or remove the server";

    /// <summary>The dead line left after the key is gone (no private key exists any more; the line grants nothing).</summary>
    public static string DeadLineWarning(string fingerprint, string? host) =>
        $"bootstrap line {fingerprint} left on {host ?? "the host"} without its private key (removed with the record); it grants nothing";
}

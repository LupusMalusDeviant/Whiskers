using Whiskers.Services.Mutations;
using Whiskers.Services.Onboarding;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0018, INV-51: the secrets of an onboarding reach no persistent or outbound sink. The rescue root password in
/// Hetzner's answer is neither returned, logged, audited nor stored; the Tailscale login link reaches the person's
/// progress view only — never the result or the audit — and the onboarding auth key reaches the host only through the
/// secret channel (also <c>OnboardingServiceStepTests.Tailscale_auth_key_reaches_the_host_only_through_the_secret_channel</c>).
/// </summary>
public sealed class OnboardingSecretsSinkTests : IDisposable
{
    private readonly RescueBootstrapRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Rescue_password_is_neither_returned_logged_audited_nor_stored()
    {
        using var scope = MutationScope.Begin("server.onboard", "op-sink-pw");

        var result = await _rig.RunAsync(await _rig.RecordAsync());

        Assert.True(result.Success, result.Error);
        var everything = string.Join("\n", [
            result.Error ?? "", result.InjectedKeyFingerprint ?? "", .. result.CleanupWarnings,
            .. scope.Results.Select(r => r.Details), .. _rig.Log.Lines,
            File.ReadAllText(Path.Combine(_rig.Store.Root, "servers.json")),
            .. _rig.Ssh.SshCalls.Select(c => c.Command),
        ]);
        Assert.DoesNotContain(_rig.Password, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(_rig.Password[..12], everything, StringComparison.Ordinal);   // not even a prefix
    }

    [Fact]
    public async Task Rescue_password_stays_out_of_a_failed_run_too()
    {
        _rig.Ssh.SshAnswer = (_, _) => new CommandResult { ExitCode = 3, Error = "no root partition" };
        using var scope = MutationScope.Begin("server.onboard", "op-sink-pw-fail");

        var result = await _rig.RunAsync(await _rig.RecordAsync());

        Assert.False(result.Success);
        var everything = string.Join("\n", [
            result.Error ?? "", .. result.CleanupWarnings, .. scope.Results.Select(r => r.Details), .. _rig.Log.Lines,
            File.ReadAllText(Path.Combine(_rig.Store.Root, "servers.json")),
        ]);
        Assert.DoesNotContain(_rig.Password, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tailscale_login_link_reaches_the_progress_only_never_the_result_or_audit()
    {
        const string Id = OnboardingServiceStepTests.Id;
        var link = "https://login.tailscale.com/a/" + Guid.NewGuid().ToString("N")[..12];
        using var w = new CredentialWorld(true, (Id, "100.64.0.20"));
        await PerHostCredentialTests.SshRecordAsync(w, Id);
        var issued = false;
        var exec = new PerHostCredentialTests.OnboardingExecutor(w.Fleet, "100.64.0.20")
        {
            Answer = c =>
            {
                if (c.Contains("journalctl -u tailscaled", StringComparison.Ordinal))
                {
                    issued = true;
                    return new CommandResult { ExitCode = 0, Output = link + "\n" };
                }
                return c.Contains("BackendState", StringComparison.Ordinal) && !issued ? new CommandResult { ExitCode = 0, Output = "" } : null;
            },
        };
        var lines = new OnboardingServiceStepTests.Lines();
        using var scope = MutationScope.Begin("server.onboard", "op-sink-ts");

        var result = await OnboardingServiceStepTests.RunAsync(
            OnboardingServiceStepTests.Service(exec, w.Servers, w, new AcceptingHostKeyPins()), w, lines);

        Assert.True(result.Success, result.Error);
        Assert.Contains(lines.All, l => l.TailscaleLoginUrl == link);   // the person sees it
        Assert.Contains(scope.Results, r => r.Details.Contains("login link issued", StringComparison.Ordinal));
        Assert.All(scope.Results, r => Assert.DoesNotContain(link, r.Details, StringComparison.Ordinal));
        Assert.DoesNotContain(link, System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }
}

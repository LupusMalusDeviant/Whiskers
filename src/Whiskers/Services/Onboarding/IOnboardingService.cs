namespace Whiskers.Services.Onboarding;

/// <summary>Onboards a server into the zero-SSH-key managed stack (Tailscale + mTLS proxy + telemetry).</summary>
public interface IOnboardingService
{
    Task<bool> OnboardServerAsync(string serverId, IProgress<string> progress, CancellationToken ct = default);
}

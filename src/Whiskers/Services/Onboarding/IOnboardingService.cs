namespace Whiskers.Services.Onboarding;

/// <summary>Onboards a server into the zero-SSH-key managed stack (Tailscale + mTLS proxy + telemetry).
/// The run reports human-readable progress lines and returns a step-tracked
/// <see cref="OnboardingResult"/>; every step is idempotent, so a re-run resumes after a failure.</summary>
public interface IOnboardingService
{
    Task<OnboardingResult> OnboardServerAsync(string serverId, IProgress<string> progress, CancellationToken ct = default);
}

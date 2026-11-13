namespace Whiskers.Services.Onboarding;

/// <summary>Onboards a server into the zero-SSH-key managed stack (Tailscale + mTLS proxy + telemetry).
/// The run reports human-readable progress lines and returns a step-tracked
/// <see cref="OnboardingResult"/>; every step is idempotent, so a re-run resumes after a failure.</summary>
public interface IOnboardingService
{
    /// <param name="mayReplaceMtlsState">INV-59 (#187): what the gateway decided on — true only when it admitted the
    /// run as a re-onboarding of a host that already holds mTLS state
    /// (<see cref="Whiskers.Services.Mutations.MutationTarget.HasControlPlaneState"/>), which needs the control-plane
    /// approval. When false and the host holds such state by the time the run takes the host lock (another onboarding
    /// finished meanwhile), the run is refused before anything is issued.</param>
    [Whiskers.Services.Mutations.MutatingOperation("server.onboard")]
    Task<OnboardingResult> OnboardServerAsync(string serverId, bool mayReplaceMtlsState, IProgress<string> progress,
        CancellationToken ct = default);
}

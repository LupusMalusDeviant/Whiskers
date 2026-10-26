using Whiskers.Services.Mutations;
namespace Whiskers.Services.Observability;

/// <summary>Why a server's background loops are paused, and until when.</summary>
/// <param name="Reason">Free text from whoever paused it, or the circuit breaker's reason.</param>
/// <param name="Automatic">True when Whiskers paused itself (an open circuit), false when a person did.
/// The distinction matters: a person who sees their own pause stops looking for a cause, so an automatic one
/// must never look like a click.</param>
/// <param name="Since">When the pause started. The reminder needs this and not <paramref name="Until"/>:
/// an "until revoked" pause has a deadline ten years out, so measuring against the end would mean the most
/// dangerous pauses — the open-ended ones — are the only kind that never gets a reminder.</param>
public sealed record LoopSuspension(string ServerId, DateTime Until, string Reason, bool Automatic, DateTime Since);

/// <summary>
/// The emergency stop (Plan-0005).
///
/// <para>When the log monitor was flattening a host on 2026-08-26 there was no way to stop it from inside
/// Whiskers — the fix went over SSH on the affected server, past the tool that was causing the problem. A
/// tool that can cause an outage needs a way to take itself back that does not require reaching the machine
/// it is hurting.</para>
///
/// <para><b>Fail-open by design.</b> If this service cannot answer, the loops run. Observing is the normal
/// state, and a suspension service that fails closed would silently stop the entire fleet's monitoring —
/// trading a loud problem for a quiet one. A failure to read the state is itself reported.</para>
///
/// <para><b>Not everything may be paused.</b> The supervisory rule that reports the absence of checks
/// (<see cref="ScanSupervisor"/>) must keep running: a switch that can silence the alarm about being silent
/// is not a switch, it is a blindfold. That is enforced by a test, not by convention.</para>
/// </summary>
public interface ILoopSuspensionService
{
    /// <summary>Whether background loops should skip this server right now. Never throws.</summary>
    bool IsSuspended(string serverId);

    /// <summary>Pauses one server's background loops. A null <paramref name="until"/> means "until revoked",
    /// which carries a recurring reminder — a pause nobody remembers is a blind spot nobody knows about.</summary>
    [MutatingOperation("checks.pause")]
    void Suspend(string serverId, DateTime? until, string reason, bool automatic = false);

    [MutatingOperation("checks.resume")]
    void Resume(string serverId);

    /// <summary>Everything currently paused, for the dashboard and the supervisory report.</summary>
    IReadOnlyList<LoopSuspension> Current();
}

using Whiskers.Models;

namespace Whiskers.Services.Agent.Triggers;

/// <summary>Core no-op default for <see cref="IAiTriggerDispatcher"/> (RoadToSAP Phase 1 §3.8). Registered
/// before the module loop so the notification composite (Modules/Notifications) — which resolves the
/// dispatcher <b>lazily</b> to avoid a DI cycle — still has something to resolve when the Agent module is
/// disabled. The real <see cref="AiTriggerDispatcher"/> (Modules/Agent) is registered inside the loop and
/// wins by last-registration when the module is on.
///
/// With the agent off there is deliberately no autonomous, event-driven agent execution, so dispatching an
/// event does nothing. That is a genuine absence of behaviour (no triggers can fire), not a faked success —
/// nothing destructive is being swallowed here, unlike the data no-ops that throw.</summary>
public sealed class NoopAiTriggerDispatcher : IAiTriggerDispatcher
{
    public Task OnEventAsync(NotificationEvent evt) => Task.CompletedTask;
}

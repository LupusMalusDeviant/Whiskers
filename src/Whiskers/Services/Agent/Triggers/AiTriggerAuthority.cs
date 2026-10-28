using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;

namespace Whiskers.Services.Agent.Triggers;

/// <summary>The authority an AI trigger's run starts with: the level its principal gets, or why it does not start.</summary>
public sealed record TriggerRunAuthority(string Level, string? Refusal);

/// <summary>The trigger side of <see cref="ITriggerAuthority"/>: also decides what a run starts with.</summary>
public interface IAiTriggerAuthority : ITriggerAuthority
{
    /// <summary>A trigger without a write grant runs read-only. One with a grant runs at its owner's level as it is
    /// now; an owner who lost their authority suspends the trigger, and the run does not start.</summary>
    Task<TriggerRunAuthority> BeginRunAsync(AiTrigger trigger, CancellationToken ct = default);
}

/// <summary>
/// PC-D6 / PC-D7 for AI triggers (R1-13), read from the store on every call so an Admin's change — a narrower grant,
/// the trigger switched off — reaches a run already under way. The owner's authority is re-read the same way; when it
/// is gone the trigger is suspended and the Admins are told (<c>automation_suspended</c>).
/// </summary>
public sealed class AiTriggerAuthority : IAiTriggerAuthority
{
    private readonly IAiTriggerStore _store;
    private readonly IAutomationOwnerResolver _owners;
    private readonly IServiceProvider? _sp;
    private readonly ILogger<AiTriggerAuthority>? _logger;

    /// <param name="sp">Resolves the notification service lazily: it depends on the trigger dispatcher.</param>
    public AiTriggerAuthority(IAiTriggerStore store, IAutomationOwnerResolver owners, IServiceProvider? sp = null,
        ILogger<AiTriggerAuthority>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _sp = sp;
        _logger = logger;
    }

    public async Task<TriggerRunAuthority> BeginRunAsync(AiTrigger trigger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!trigger.HasWriteGrant) return new(McpPermissionLevels.Read, null);
        var owner = _owners.Resolve(trigger.OwnerId!);
        var state = _owners.Describe(trigger.OwnerId!).State;
        if (state == OwnerState.Unverified)
            return new(McpPermissionLevels.Read, $"its owner {owner.DisplayName} has not signed in since the grant");
        if (state != OwnerState.Active || !McpPermissionLevels.HasAccess(owner.PermissionLevel, McpPermissionLevels.Write))
            return new(McpPermissionLevels.Read, await SuspendAsync(trigger, $"its owner {owner.DisplayName} has no authority left"));
        return new(McpPermissionLevels.Normalize(owner.PermissionLevel), null);
    }

    public async Task<string?> RefusalAsync(string triggerId, Capability capability, MutationTarget target,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(target);
        var trigger = _store.Triggers.FirstOrDefault(t => t.Id == triggerId);
        if (trigger is null) return "the AI trigger no longer exists";
        if (!trigger.Enabled) return "the AI trigger is switched off";
        if (!trigger.HasWriteGrant) return "the AI trigger is read-only: an Admin has granted it no capabilities and targets";

        // PC-D7: only what an Admin listed. Anything else would need a confirmation, which an unattended run cannot get.
        if (!trigger.WriteCapabilities.Contains(capability.Id, StringComparer.Ordinal))
            return $"'{capability.McpTool ?? capability.Id}' is not among the capabilities granted to the AI trigger";
        if (!trigger.WriteTargets.Any(g => TriggerAuthority.GlobMatch(g, target.Id) || TriggerAuthority.GlobMatch(g, target.Name)))
            return $"{target.Kind} '{target.Name ?? target.Id}' is not among the targets granted to the AI trigger";

        // PC-D6: the owner's authority as it is now, for this capability at its effective level.
        var owner = _owners.Resolve(trigger.OwnerId!);
        var state = _owners.Describe(trigger.OwnerId!).State;
        if (state == OwnerState.Unverified)
            return $"its owner {owner.DisplayName} has not signed in since the grant";
        var tool = capability.McpTool ?? capability.Id;
        var lost = state != OwnerState.Active
            ? $"its owner {owner.DisplayName} has no authority left"
            : !McpPermissionLevels.HasAccess(owner.PermissionLevel, capability.RequiredLevel)
                ? $"its owner {owner.DisplayName} no longer holds '{capability.RequiredLevel}' for {capability.Id}"
                : owner.AllowedTools is { } allowed && !allowed.Contains(tool)
                    ? $"its owner {owner.DisplayName} may no longer use '{tool}'"
                    : null;
        return lost is null ? null : await SuspendAsync(trigger, lost);
    }

    /// <summary>Suspends the trigger (unless an Admin took it over meanwhile) and tells the Admins. Returns the
    /// refusal for the caller.</summary>
    private async Task<string> SuspendAsync(AiTrigger trigger, string lost)
    {
        var refusal = $"The AI trigger cannot act: {lost}.";
        bool suspended;
        try
        {
            suspended = await _store.SuspendAsync(trigger.Id, trigger.OwnerId!, lost);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AI trigger {TriggerId} could not be suspended", trigger.Id);
            return refusal;
        }
        if (!suspended) return refusal;

        _logger?.LogWarning("AI trigger {TriggerId} was suspended: {Reason}", trigger.Id, lost);
        try
        {
            if (_sp?.GetService<INotificationService>() is { } notify)
                await notify.SendAsync(new NotificationEvent
                {
                    EventType = "automation_suspended",
                    ContainerName = trigger.Name,
                    ImageInfo = $"AI trigger \"{trigger.Name}\" was suspended: {lost}. It stays off until an admin switches " +
                                "it on again in AI triggers, which makes that admin its owner.",
                });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Notification for suspended AI trigger {TriggerId} failed", trigger.Id);
        }
        return refusal;
    }
}

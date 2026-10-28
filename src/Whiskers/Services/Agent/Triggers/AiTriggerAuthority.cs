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
    /// <summary>Every run re-checks the trigger's owner (PC-D6): no owner, or an owner who lost their authority —
    /// for a trigger with a write grant, for any capability granted — suspends the trigger, and the run does not
    /// start. Otherwise a trigger without a write grant runs read-only, one with a grant at its owner's level as it
    /// is now.</summary>
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
    private readonly ICapabilityCatalog _catalog;
    private readonly ICapabilityOverrides? _overrides;
    private readonly IServiceProvider? _sp;
    private readonly ILogger<AiTriggerAuthority>? _logger;

    /// <param name="sp">Resolves the notification service lazily: it depends on the trigger dispatcher.</param>
    public AiTriggerAuthority(IAiTriggerStore store, IAutomationOwnerResolver owners, ICapabilityCatalog catalog,
        ICapabilityOverrides? overrides = null, IServiceProvider? sp = null, ILogger<AiTriggerAuthority>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _overrides = overrides;
        _sp = sp;
        _logger = logger;
    }

    public async Task<TriggerRunAuthority> BeginRunAsync(AiTrigger trigger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (string.IsNullOrWhiteSpace(trigger.OwnerId))
            return new(McpPermissionLevels.Read, await SuspendAsync(trigger,
                "it has no owner (saved before owners were recorded); an admin must switch it on again to take it over"));
        var owner = _owners.Resolve(trigger.OwnerId);
        var state = _owners.Describe(trigger.OwnerId).State;
        // Unverified is refused but not suspended: it runs again once the owner signs in (or an admin takes over).
        if (state == OwnerState.Unverified)
            return new(McpPermissionLevels.Read, $"its owner {owner.DisplayName} has not signed in yet");
        if (state != OwnerState.Active)
            return new(McpPermissionLevels.Read, await SuspendAsync(trigger, $"its owner {owner.DisplayName} has no authority left"));
        if (!trigger.HasWriteGrant) return new(McpPermissionLevels.Read, null);

        // A grant is only as good as its owner: every granted capability must still be the owner's to give.
        foreach (var id in trigger.WriteCapabilities)
        {
            if (_catalog.Find(id) is not { } capability) continue;           // unknown ids grant nothing
            if (OwnerLacks(owner, Effective(capability)) is { } lost)
                return new(McpPermissionLevels.Read, await SuspendAsync(trigger, lost));
        }
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
        if (NotGrantable.Contains(capability.Id))
            return $"'{capability.McpTool ?? capability.Id}' cannot be granted to an AI trigger: what it acts on is not its resolved target";
        // Only canonical identities count: the resolved id, and the name where the resolver took it from the inventory.
        // A name the caller typed (a DNS record name, a compose project name) never stands in for the target.
        var own = Identities(target);
        if (!own.Any(i => Granted(trigger, i)))
            return $"{target.Kind} '{target.Name ?? target.Id}' is not among the targets granted to the AI trigger";
        // Every other resource the operation also touches (the containers a network removal cuts off, the container
        // it connects, the containers a compose plan recreates) must be granted too, by one of its own names or ids —
        // whatever names it shares with the target: a grant on a network is not one on what hangs off it. A resolved
        // target lists them per resource; for any other, every related value counts as a resource of its own.
        var resources = target.Resolved
            ? target.RelatedResources
            : target.Related.Select(r => (IReadOnlyList<string>)[r]).ToList();
        if (resources.FirstOrDefault(g => !g.Any(a => Granted(trigger, a))) is { } outside)
            return $"the operation also touches '{string.Join(" / ", outside)}', which is not among the targets granted to the AI trigger";

        // PC-D6: the owner's authority as it is now, for this capability at its effective level.
        var owner = _owners.Resolve(trigger.OwnerId!);
        var state = _owners.Describe(trigger.OwnerId!).State;
        if (state == OwnerState.Unverified)
            return $"its owner {owner.DisplayName} has not signed in yet";
        var lost = state != OwnerState.Active
            ? $"its owner {owner.DisplayName} has no authority left"
            : OwnerLacks(owner, capability);
        return lost is null ? null : await SuspendAsync(trigger, lost);
    }

    /// <summary>Capabilities an AI trigger can never be granted: the object they change is a further argument the
    /// resolver does not check (a snapshot deletion targets the cloud server that selects the account, while the
    /// snapshot is any id in that account), so a target grant would not bound what they change.</summary>
    internal static readonly IReadOnlySet<string> NotGrantable = new HashSet<string>(StringComparer.Ordinal)
    {
        "hetzner.snapshot_delete",
    };

    /// <summary>Kinds whose resolved <see cref="MutationTarget.Name"/> comes from the inventory, not from the call.</summary>
    private static readonly HashSet<string> InventoryNamed = new(StringComparer.Ordinal)
    {
        "container", "network", "cloud-server", "scheduled-task", "workload", "webhook",
    };

    /// <summary>The target's canonical identities: its id, and its name only when the resolver found the target in the
    /// inventory under that name (never for a creation, a host, a server or a DNS zone, whose name is the caller's).</summary>
    internal static HashSet<string> Identities(MutationTarget target)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(target.Id)) ids.Add(target.Id);
        if (!target.Resolved) return ids;
        if (InventoryNamed.Contains(target.Kind) && !string.IsNullOrEmpty(target.Name)) ids.Add(target.Name);
        ids.UnionWith(target.Aliases.Where(a => a.Length > 0));      // the inventory's other names for the target
        return ids;
    }

    private static bool Granted(AiTrigger trigger, string? value) =>
        trigger.WriteTargets.Any(g => TriggerAuthority.GlobMatch(g, value));

    /// <summary>Why the owner cannot give <paramref name="capability"/> (at its effective level and as a tool its key
    /// may use), or <c>null</c>.</summary>
    private static string? OwnerLacks(MutationPrincipal owner, Capability capability)
    {
        var tool = capability.McpTool ?? capability.Id;
        return !McpPermissionLevels.HasAccess(owner.PermissionLevel, capability.RequiredLevel)
            ? $"its owner {owner.DisplayName} no longer holds '{capability.RequiredLevel}' for {capability.Id}"
            : owner.AllowedTools is { } allowed && !allowed.Contains(tool)
                ? $"its owner {owner.DisplayName} may no longer use '{tool}'"
                : null;
    }

    /// <summary>The capability with a stricter level stored in Settings → MCP, as the gateway applies it. A switched-off
    /// tool is a policy, not a lost authority (the gateway refuses it anyway).</summary>
    private Capability Effective(Capability capability)
    {
        if (_overrides is null) return capability;
        try
        {
            var availability = _overrides.Resolve(capability);
            return availability.Enabled ? capability with { RequiredLevel = availability.RequiredLevel } : capability;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return capability;                                  // the gateway refuses the call fail-closed
        }
    }

    /// <summary>Suspends the trigger (unless an Admin took it over meanwhile) and tells the Admins. Returns the
    /// refusal for the caller.</summary>
    private async Task<string> SuspendAsync(AiTrigger trigger, string lost)
    {
        var refusal = $"The AI trigger cannot act: {lost}.";
        bool suspended;
        try
        {
            suspended = await _store.SuspendAsync(trigger.Id, trigger.OwnerId, lost);
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

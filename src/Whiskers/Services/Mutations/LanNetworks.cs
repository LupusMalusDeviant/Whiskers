using Whiskers.Models;
using Whiskers.Services.Notifications;

namespace Whiskers.Services.Mutations;

/// <summary>Contract 0021: whether a network driver puts a container on the LAN, and the
/// <see cref="ExposureKind.LanNetwork"/> exposures built from it.</summary>
public static class LanDrivers
{
    /// <summary>Drivers that never give a container a LAN address of its own (a fixed list, decision D7).</summary>
    public static readonly IReadOnlySet<string> NotLan = new HashSet<string>(StringComparer.Ordinal)
        { "bridge", "overlay", "host", "none", "null" };

    /// <summary>True for macvlan, ipvlan and every driver not in <see cref="NotLan"/> (decision D2). The comparison is
    /// ordinal and exact after trimming: <c>Macvlan</c> is not the built-in driver, and a plugin-qualified name
    /// (<c>vendor/driver:latest</c>) is not in the list either — both count as LAN.</summary>
    public static bool IsLan(string? driver) => !NotLan.Contains(Normalize(driver));

    /// <summary>The driver as <see cref="IsLan"/> judges it and as a network is created with: trimmed, never
    /// <c>null</c>.</summary>
    public static string Normalize(string? driver) => (driver ?? "").Trim();

    /// <summary>The service part of a network operation's identity (<c>network.create</c>): <c>network &lt;name&gt;</c>.</summary>
    public static string NetworkService(string networkName) => $"network {networkName}";

    /// <summary>A <see cref="ExposureKind.LanNetwork"/> exposure: key = the network's name on the host, value = the driver.
    /// <paramref name="isInternal"/> (decision D3: no parent or <c>internal: true</c>) only adds <c>" internal"</c> to
    /// the shown detail, never to the comparison value.</summary>
    public static ComposeExposure Exposure(string service, string networkName, string driver, bool isInternal = false,
        bool withheld = false) =>
        new(service, ExposureKind.LanNetwork, withheld
            ? "LAN network (withheld)"
            : $"LAN network {networkName} ({driver}{(isInternal ? " internal" : "")})")
        {
            Key = networkName, Value = driver, KeyWithheld = withheld, ValueWithheld = withheld,
        };

    /// <summary>The identity <c>network.create</c> of <paramref name="networkName"/> with <paramref name="driver"/> carries
    /// in its resolved target, or <c>null</c> when the driver is not LAN.</summary>
    public static string? CreateIdentity(string networkName, string driver) =>
        IsLan(driver) ? ComposeExposures.Identity(NetworkService(networkName), ExposureKind.LanNetwork, networkName, Normalize(driver)) : null;

    /// <summary>
    /// Contract 0021: the LAN exposure this call produces was decided in the open scope; refused otherwise. A call whose
    /// driver is not LAN passes. A delegate that creates a different driver or name than the one decided (a value
    /// captured before the approval, a caller that changes it) is refused before Docker is called.
    /// </summary>
    public static void EnsureLanDecided(string? serverId, string networkName, string driver)
    {
        if (CreateIdentity(networkName, driver) is not { } identity) return;
        if (MutationScope.Current is { } scope && scope.CoversExposureFor("network.create", serverId, identity)) return;
        throw new MutationScopeViolationException("network.create", "IDockerService.CreateNetworkAsync",
            MutationScope.Current?.CapabilityId, $"LAN network {networkName} ({driver})");
    }

    /// <summary>The <c>exposure_change</c> of a network operation a person ran (§ 1.7): the server, the kind, the network
    /// name and the driver; for <c>network.connect</c> also the container. Only after the operation succeeded, never on
    /// the agent rule set's path (it asked for a confirmation instead). A failed send undoes nothing.</summary>
    public static async Task NotifyAsync(INotificationService? notifications, string serverId, string networkName,
        string driver, string? container = null, ILogger? logger = null)
    {
        if (notifications is null || !IsLan(driver)) return;
        var subject = container is null ? $"Network '{networkName}'" : $"Container '{container}' on network '{networkName}'";
        try
        {
            await notifications.SendAsync(new NotificationEvent
            {
                EventType = NotificationEventTypes.ExposureChange,
                ContainerName = networkName,
                ServerId = serverId,
                ImageInfo = $"{subject}: {ExposureKind.LanNetwork} (driver {driver}); containers on it get their own " +
                            "address on the LAN, past the host's firewall.",
            });
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "The exposure_change notification for network {Network} could not be sent", networkName);
        }
    }

    /// <summary>Whether the open scope was decided by the agent rule set (then no notification: it asked for a
    /// confirmation instead).</summary>
    public static bool DecidedForAgent => MutationScope.Current?.AgentRuleSet ?? false;
}

/// <summary>
/// Contract 0021: a network a service joins, as the planner looks it up among the host's live networks (§ 1.4) and
/// step 4 looks it up again (§ 1.5). <paramref name="Name"/> is the name compose resolved; <paramref name="External"/>
/// is an external network or a <c>network_mode: &lt;name&gt;</c>, which must exist; <paramref name="Withheld"/> a name
/// or driver that came from a variable (or only compose's model read it).
/// </summary>
public sealed record ComposeNetworkRef(string Service, string Name, bool External, bool Withheld);

/// <summary>Contract 0021: the live part of the LAN detection — the host's networks looked up by name.</summary>
public static class ComposeLiveNetworks
{
    /// <summary>
    /// The <see cref="ExposureKind.LanNetwork"/> exposures of <paramref name="refs"/> by the host's
    /// <paramref name="live"/> networks: one found by name → LAN when its driver is; an external one not found →
    /// <see cref="ComposePlanException"/> (compose would refuse <c>up</c> the same way); a non-external one not found →
    /// nothing here (compose creates it with its declared driver, decided from the files); found more than once → refused.
    /// A withheld name is never quoted.
    /// </summary>
    public static IReadOnlyList<ComposeExposure> Decide(IEnumerable<ComposeNetworkRef> refs, IReadOnlyList<NetworkInfo> live,
        string serverId)
    {
        var found = new List<ComposeExposure>();
        foreach (var r in refs)
        {
            var named = r.Withheld ? "(withheld)" : $"'{r.Name}'";
            var matches = live.Where(n => string.Equals(n.Name, r.Name, StringComparison.Ordinal)).ToList();
            if (matches.Count > 1)
                throw new ComposePlanException($"The network {named} on {serverId} is not unique.");
            if (matches.Count == 0)
            {
                if (r.External)
                    throw new ComposePlanException($"The external network {named} does not exist on {serverId}.");
                continue;
            }
            var network = matches[0];
            if (LanDrivers.IsLan(network.Driver))
                found.Add(LanDrivers.Exposure(r.Service, network.Name, network.Driver, network.Internal, r.Withheld));
        }
        return found;
    }

    /// <summary>The message of a network list that cannot be read (fail closed).</summary>
    public static string Unreadable(string serverId) =>
        $"The networks of {serverId} cannot be read, so a LAN network cannot be ruled out.";
}

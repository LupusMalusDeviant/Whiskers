namespace Whiskers.Services.Guardrails;

/// <summary>
/// Whiskers' own control plane (INV-59, R1-16): its container (and the pre-rebrand name), its database, its data
/// volume, the Docker socket proxy and the mTLS tunnel every managed server runs, the step-ca authority, the reverse
/// proxy in front of it — and the files that hold its data and mTLS material. Protected on every path, whatever the
/// active policy says: a policy without protected resources still cannot reach them. Only an interactive Admin action
/// that is approved and audited may (<see cref="ProtectedResourceRule"/>).
/// </summary>
public static class ControlPlaneResources
{
    /// <summary>Names (glob, case-insensitive) of the control plane's containers, volumes and networks, matched
    /// against the resolved target, every argument value and every word of a free-text argument.</summary>
    public static IReadOnlyList<string> NamePatterns { get; } = BuildNames();

    /// <summary>Paths (glob) of the control plane's data and mTLS material on a host, matched against every
    /// path-like word of every argument (normalised), and as a mount source also against the directories above
    /// them.</summary>
    public static IReadOnlyList<string> PathPatterns { get; } =
    [
        // Whiskers' data volume on the host (database, keys, vault, mTLS), wherever Docker keeps it by default
        "/var/lib/docker/volumes/whiskers*", "/var/lib/docker/volumes/serverwatch*",
        "*/mtls", "*/mtls/*",                       // client certificates and keys of the mTLS tunnels
        "*/step-ca", "*/step-ca/*",                 // the certificate authority's keys
        "/opt/step-ca", "/opt/step-ca/*",           // … where onboarding puts it (literal, so /opt counts as above it)
        "*/dockerproxy", "*/dockerproxy/*",         // every managed host's ghostunnel key, certificate and CA
        "/opt/dockerproxy", "/opt/dockerproxy/*",
        "*/ghostunnel", "*/ghostunnel/*",
        "/var/run/docker.sock", "/run/docker.sock", // the socket the control plane drives Docker through
    ];

    /// <summary>Capabilities (and their MCP tools) that change the control plane whatever they name: rotating or
    /// issuing mTLS material (a new key, signed by step-ca, the tunnel restarted) and restoring Whiskers' own data.</summary>
    public static readonly IReadOnlySet<string> Capabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "certificate.renew_mtls", "renew_mtls_certificate", "certificate.issue_mtls", "selfbackup.restore",
    };

    /// <summary>Arguments that name a container, volume or network directly (the agent's own calls carry them raw).</summary>
    private static readonly HashSet<string> IdentityArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "containerId", "containerName", "container", "volumeName", "volume", "networkId", "networkName", "network",
        "serviceName",
    };

    /// <summary>Target kinds whose resolved id and name can be part of the control plane.</summary>
    private static readonly HashSet<string> ResourceKinds = new(StringComparer.Ordinal)
    {
        "container", "volume", "network", "workload",
    };

    /// <summary>
    /// Which arguments of a request the control plane's names are matched against (R1-16): what names a container,
    /// volume or network — identity arguments, free text, mount lists, what the operation also touches, and the
    /// resolved target when it is such a resource. A server, a DNS record, a task or an image namespace that shares
    /// the prefix is not the control plane.
    /// </summary>
    public static Func<string, bool> NamesApplyTo(IReadOnlyDictionary<string, System.Text.Json.JsonElement> arguments)
    {
        var resolvedKind = arguments.TryGetValue("__resolvedKind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String
            ? k.GetString()
            : null;
        var targetIsResource = resolvedKind is not null && ResourceKinds.Contains(resolvedKind);
        return key =>
            IdentityArguments.Contains(key)
            || ProtectedResourceMatcher.FreeTextArguments.Contains(key)
            || ProtectedResourceMatcher.ReferenceArguments.Contains(key)
            || key.StartsWith(ProtectedResourceMatcher.ResolvedRelatedPrefix, StringComparison.Ordinal)
            || (targetIsResource && key is "__resolvedTargetId" or "__resolvedTargetName");
    }

    private static IReadOnlyList<string> BuildNames()
    {
        var names = new List<string>
        {
            "whiskers*", "serverwatch*",            // the app, its database (whiskers-db), its data volume
            "socket-proxy*", "docker-socket-proxy*",
            "ghostunnel*", "step-ca*",
            "nginx-proxy-manager*",                 // the reverse proxy in front of Whiskers and its MCP gateway
        };
        // Names an operator gave Whiskers' own containers (the log monitor's self-exclusion uses the same variable).
        foreach (var name in Whiskers.Services.LogMonitor.Hygiene.LogScanExclusions.ManualNamesFromEnvironment())
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name.Trim());
        return names;
    }
}

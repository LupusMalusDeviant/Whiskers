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

    /// <summary>Paths (glob) of the control plane's data and mTLS material, matched against every path-like word of
    /// a free-text argument (a command, SQL, a compose file).</summary>
    public static IReadOnlyList<string> PathPatterns { get; } =
    [
        "/app/data", "/app/data/*",                 // Whiskers' data directory (database, keys, vault, mTLS)
        "*/mtls", "*/mtls/*",                       // client certificates and keys of the mTLS tunnels
        "*/step-ca", "*/step-ca/*",                 // the certificate authority's keys
        "*/ghostunnel", "*/ghostunnel/*",
        "/var/run/docker.sock", "/run/docker.sock", // the socket the control plane drives Docker through
    ];

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

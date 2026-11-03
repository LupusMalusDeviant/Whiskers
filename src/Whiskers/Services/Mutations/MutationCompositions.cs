using System.Collections.Frozen;

namespace Whiskers.Services.Mutations;

/// <summary>
/// The nested operations a capability's execution may perform on the authority of its own decision (R1-48, INV-38).
///
/// <para>A gateway decision covers exactly one capability. Some executions are made of other marked operations — an
/// app deployment pulls an image and creates a container, a registry save writes its credential to the vault, a
/// Kubernetes-or-Docker workload start is a container start on Docker. Each such pair is listed here, explicitly;
/// nothing else is implied. The scope guard lets a marked call through only when an open scope of the flow decided
/// that capability or one that composes it (transitively, <see cref="MutationScope.Authorizes"/>), and <c>MutationBypassArchitectureTests</c> checks every call
/// inside an operation's implementation against the same table.</para>
///
/// <para>A composed operation never needs more than the composing one: <c>MutationGatewayCoverageTests</c> pins that each
/// listed operation requires at most the level of the capability that composes it, so a nested step can never do what
/// the decided caller could not have been allowed to do on its own.</para>
/// </summary>
public static class MutationCompositions
{
    private static readonly (string Outer, string[] Inner)[] Table =
    [
        // Deployments: a form deployment is pull + create; a compose file deploys each of its services as a form.
        ("app.deploy", ["image.pull", "container.create"]),
        ("compose.deploy", ["app.deploy"]),
        // A volume restore takes a safety backup of the volume first.
        ("volume.restore", ["volume.backup"]),
        // A Git-deploy app's token and saved credentials live in the vault.
        ("gitdeploy.save", ["vault.secret_set", "vault.secret_delete"]),
        ("gitdeploy.set_token", ["gitdeploy.save"]),
        ("gitdeploy.delete", ["vault.secret_delete"]),
        // nginx: a changed or (de)activated site is reloaded to take effect.
        ("nginx.update", ["nginx.reload"]),
        ("nginx.site_enable", ["nginx.reload"]),
        ("nginx.site_disable", ["nginx.reload"]),
        // Registries keep their credential in the vault.
        ("registry.save", ["vault.secret_set", "vault.secret_delete"]),
        ("registry.delete", ["vault.secret_delete"]),
        // A server record owns its kubeconfig in the vault; removing the server removes it (ServerRemoval).
        ("server.remove", ["vault.secret_delete"]),
        // Onboarding issues the server's mTLS client certificate and switches its record to the managed transport.
        ("server.onboard", ["certificate.issue_mtls", "server.save"]),
        // On a Docker server a workload is a container.
        ("workload.start", ["container.start"]),
        ("workload.stop", ["container.stop"]),
        ("workload.restart", ["container.restart"]),
        // Assessing an update pulls the candidate image (it starts and recreates nothing).
        ("image.assess_update", ["image.pull"]),
    ];

    private static readonly FrozenDictionary<string, FrozenSet<string>> Closure = Build();

    /// <summary>Every pair as listed (outer → directly composed operations).</summary>
    public static IReadOnlyList<(string Outer, IReadOnlyList<string> Inner)> Entries { get; } =
        Table.Select(t => (t.Outer, (IReadOnlyList<string>)t.Inner)).ToList();

    /// <summary>True when an execution decided as <paramref name="outer"/> may perform <paramref name="inner"/>: the
    /// same capability, or one it composes directly or through another composed operation.</summary>
    public static bool Allows(string outer, string inner) =>
        string.Equals(outer, inner, StringComparison.Ordinal)
        || (Closure.TryGetValue(outer, out var set) && set.Contains(inner));

    /// <summary>The operations <paramref name="outer"/> composes, transitively (empty when none).</summary>
    public static IReadOnlySet<string> Of(string outer) =>
        Closure.TryGetValue(outer, out var set) ? set : FrozenSet<string>.Empty;

    private static FrozenDictionary<string, FrozenSet<string>> Build()
    {
        var direct = Table.ToDictionary(t => t.Outer, t => t.Inner, StringComparer.Ordinal);
        var result = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        foreach (var outer in direct.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(direct[outer]);
            while (stack.TryPop(out var next))
                if (seen.Add(next) && direct.TryGetValue(next, out var deeper))
                    foreach (var d in deeper) stack.Push(d);
            seen.Remove(outer);
            result[outer] = seen.ToFrozenSet(StringComparer.Ordinal);
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

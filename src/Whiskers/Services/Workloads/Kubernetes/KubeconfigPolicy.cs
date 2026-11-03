using k8s;
using k8s.KubeConfigModels;

namespace Whiskers.Services.Workloads.Kubernetes;

/// <summary>
/// Which kubeconfigs Whiskers hands to the Kubernetes client (R1-42, E-09). Established for the installed
/// <c>KubernetesClient</c> (21.0.8) by <c>KubeconfigExecutionTests</c>:
/// <list type="bullet">
/// <item><c>users[].user.exec</c> — <see cref="KubernetesClientConfiguration.BuildConfigFromConfigObject"/> starts the
/// named program on the Whiskers host right away, while building the configuration, before any request.</item>
/// <item><c>users[].user.auth-provider</c> — <c>oidc</c> builds a token provider that refreshes against the URL in the
/// kubeconfig; <c>azure</c> and <c>gcp</c> are refused by the client itself. Plugin-style authentication either way.</item>
/// <item><c>client-certificate</c>, <c>client-key</c>, <c>certificate-authority</c> — paths the client reads from the
/// Whiskers host's file system.</item>
/// </list>
/// The policy is the simplest one that fails closed: a kubeconfig must be self-contained. One that names an exec plugin,
/// an auth provider or a local file — for any user or cluster, not only the selected context — is refused, with no
/// setting to allow it. Supported are inline credentials: a bearer <c>token</c>, <c>client-certificate-data</c> /
/// <c>client-key-data</c>, <c>username</c>/<c>password</c>, and <c>certificate-authority-data</c>. It is checked on the
/// parsed object the client receives, both when the kubeconfig is saved and every time a client is built, so a
/// kubeconfig stored before R1-42 meets the same rule.
/// </summary>
public static class KubeconfigPolicy
{
    /// <summary>Parses <paramref name="yaml"/> the way the client does and returns it when the policy admits it;
    /// throws <see cref="KubeconfigRefusedException"/> otherwise (and whatever the parser throws for invalid YAML).</summary>
    public static K8SConfiguration ParseAndAdmit(string yaml)
    {
        var config = KubernetesYaml.Deserialize<K8SConfiguration>(yaml);
        var violations = Violations(config);
        if (violations.Count > 0) throw new KubeconfigRefusedException(violations);
        return config;
    }

    /// <summary>The reasons a kubeconfig about to be saved is refused, for the early answer on the Servers page. YAML the
    /// parser cannot read yields none here: it can never become a client and fails, as before, when one is built —
    /// where <see cref="ParseAndAdmit"/> decides again in any case.</summary>
    public static IReadOnlyList<string> AdmissionViolations(string yaml)
    {
        K8SConfiguration config;
        try { config = KubernetesYaml.Deserialize<K8SConfiguration>(yaml); }
        catch (Exception) { return []; }
        return Violations(config);
    }

    /// <summary>Every reason <paramref name="config"/> is refused; empty when it is admitted. Names users and clusters,
    /// never a credential value.</summary>
    public static IReadOnlyList<string> Violations(K8SConfiguration? config)
    {
        var violations = new List<string>();
        if (config is null) return violations;

        foreach (var user in config.Users ?? [])
        {
            var name = user?.Name ?? "(unnamed)";
            var credentials = user?.UserCredentials;
            if (credentials is null) continue;
            if (credentials.ExternalExecution is not null)
                violations.Add($"user '{name}' uses an exec credential plugin (users[].user.exec)");
            if (credentials.AuthProvider is not null)
                violations.Add($"user '{name}' uses the auth provider '{credentials.AuthProvider.Name}' (users[].user.auth-provider)");
            if (!string.IsNullOrEmpty(credentials.ClientCertificate))
                violations.Add($"user '{name}' reads its client certificate from a file (client-certificate)");
            if (!string.IsNullOrEmpty(credentials.ClientKey))
                violations.Add($"user '{name}' reads its client key from a file (client-key)");
        }

        foreach (var cluster in config.Clusters ?? [])
        {
            if (!string.IsNullOrEmpty(cluster?.ClusterEndpoint?.CertificateAuthority))
                violations.Add($"cluster '{cluster.Name ?? "(unnamed)"}' reads its CA from a file (certificate-authority)");
        }

        return violations;
    }
}

/// <summary>A kubeconfig the <see cref="KubeconfigPolicy"/> refuses. An <see cref="InvalidOperationException"/>, like the
/// other reasons a client cannot be built; the message lists every violation and how to fix it.</summary>
public sealed class KubeconfigRefusedException(IReadOnlyList<string> violations)
    : InvalidOperationException(
        "Kubeconfig refused: " + string.Join("; ", violations) +
        ". Whiskers runs no credential plugins and reads no files named in a kubeconfig — use a self-contained kubeconfig " +
        "with an inline token or client-certificate-data/client-key-data (e.g. a ServiceAccount token).")
{
    /// <summary>The reasons, one per offending entry.</summary>
    public IReadOnlyList<string> Violations { get; } = violations;
}

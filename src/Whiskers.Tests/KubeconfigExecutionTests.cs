using k8s;
using k8s.KubeConfigModels;
using Whiskers.Models;
using Whiskers.Services.Vault;
using Whiskers.Services.Workloads.Kubernetes;

namespace Whiskers.Tests;

/// <summary>R1-42 (E-09): what the installed KubernetesClient does with plugin and file entries of a kubeconfig, and the
/// policy Whiskers puts in front of it. The first group pins the client's behaviour — if an upgrade changes it, these
/// tests say so — and the second proves that <see cref="KubeconfigPolicy"/> refuses every such kubeconfig before the
/// client's builder sees it, on the path Whiskers actually uses (<see cref="KubernetesClientCache"/>).</summary>
public sealed class KubeconfigExecutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-r142-kube-" + Guid.NewGuid().ToString("N"));
    private readonly string _marker;

    public KubeconfigExecutionTests()
    {
        Directory.CreateDirectory(_dir);
        _marker = Path.Combine(_dir, "exec-ran.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- The installed client's behaviour --------------------------------------------------------------------------

    [Fact]
    public void The_client_runs_an_exec_plugin_while_building_the_configuration()
    {
        var config = KubernetesYaml.Deserialize<K8SConfiguration>(MarkerExecKubeconfig());

        // The plugin prints no ExecCredential, so the build fails — but only after the program has run on this host.
        Assert.ThrowsAny<Exception>(() => KubernetesClientConfiguration.BuildConfigFromConfigObject(config));

        // The client may give up on the plugin's output before the started program has finished (seen on Linux): the
        // program still runs on this host, so its marker appears shortly after.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(_marker) && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.True(File.Exists(_marker), "the client started the exec plugin during BuildConfigFromConfigObject");
    }

    [Fact]
    public void The_client_tries_to_start_an_exec_plugin_named_by_any_path()
    {
        var config = KubernetesYaml.Deserialize<K8SConfiguration>(ExecKubeconfig("/nonexistent/whiskers-r142-probe", []));

        var ex = Assert.ThrowsAny<Exception>(() => KubernetesClientConfiguration.BuildConfigFromConfigObject(config));

        Assert.Contains("whiskers-r142-probe", ex.Message);
    }

    [Fact]
    public void The_client_builds_an_oidc_auth_provider_that_refreshes_against_the_kubeconfig_url()
    {
        var config = KubernetesYaml.Deserialize<K8SConfiguration>(Kubeconfig("""
                auth-provider:
                  name: oidc
                  config:
                    client-id: whiskers
                    idp-issuer-url: https://127.0.0.1:1
                    id-token: placeholder
                    refresh-token: placeholder
            """));

        var built = KubernetesClientConfiguration.BuildConfigFromConfigObject(config);

        Assert.Equal("k8s.Authentication.OidcTokenProvider", built.TokenProvider?.GetType().FullName);
    }

    [Fact]
    public void The_client_reads_a_client_certificate_named_by_path_from_this_host()
    {
        var missing = Path.Combine(_dir, "no-such-client.crt");
        var config = KubernetesYaml.Deserialize<K8SConfiguration>(Kubeconfig($"""
                client-certificate: {missing}
                client-key: {missing}
            """));

        // Building the configuration only records the path; the client reaches for the file on the Whiskers host's disk
        // when it is created.
        var built = KubernetesClientConfiguration.BuildConfigFromConfigObject(config);
        Assert.Equal(missing, built.ClientCertificateFilePath);
        Assert.ThrowsAny<Exception>(() => new k8s.Kubernetes(built));
    }

    // ---- The policy --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_stored_exec_kubeconfig_never_reaches_the_client_and_never_runs()
    {
        var (cache, serverId) = CacheWith(MarkerExecKubeconfig());

        var ex = Assert.Throws<KubeconfigRefusedException>(() => cache.GetClient(serverId));

        Assert.False(File.Exists(_marker), "the exec plugin must not run");
        Assert.Contains("users[].user.exec", ex.Message);
    }

    [Theory]
    [InlineData("auth-provider:\n  name: oidc\n  config:\n    client-id: x\n    idp-issuer-url: https://127.0.0.1:1", "auth-provider")]
    [InlineData("auth-provider:\n  name: gcp\n  config:\n    cmd-path: /bin/sh", "auth-provider")]
    [InlineData("client-certificate: /app/data/client.crt\nclient-key-data: a2V5", "client-certificate")]
    [InlineData("client-certificate-data: Y2VydA==\nclient-key: /app/data/client.key", "client-key")]
    public void A_kubeconfig_with_an_auth_provider_or_a_file_reference_is_refused(string user, string named)
    {
        var yaml = Kubeconfig("    " + user.Replace("\n", "\n    "));
        var (cache, serverId) = CacheWith(yaml);

        var ex = Assert.Throws<KubeconfigRefusedException>(() => cache.GetClient(serverId));

        Assert.Contains(named, ex.Message);
        Assert.NotEmpty(KubeconfigPolicy.AdmissionViolations(yaml));
    }

    [Fact]
    public void A_cluster_ca_read_from_a_file_is_refused()
    {
        var yaml = Kubeconfig("    token: placeholder", cluster: "certificate-authority: /etc/ssl/certs/ca.pem");

        Assert.Contains(KubeconfigPolicy.AdmissionViolations(yaml), v => v.Contains("certificate-authority"));
        Assert.Throws<KubeconfigRefusedException>(() => KubeconfigPolicy.ParseAndAdmit(yaml));
    }

    [Fact]
    public void A_plugin_on_a_user_outside_the_selected_context_is_refused_too()
    {
        // The selected context uses a token; a second user carries the plugin. Whiskers refuses the whole kubeconfig.
        var yaml = Kubeconfig("    token: placeholder") + """

            - name: other
              user:
                exec:
                  apiVersion: client.authentication.k8s.io/v1beta1
                  command: /bin/sh
            """.Replace("\r\n", "\n");

        Assert.Contains(KubeconfigPolicy.AdmissionViolations(yaml), v => v.Contains("'other'"));
    }

    [Fact]
    public void A_self_contained_kubeconfig_is_admitted_and_builds_a_client()
    {
        var yaml = Kubeconfig("    token: placeholder", cluster: "certificate-authority-data: dGVzdA==");
        var (cache, serverId) = CacheWith(yaml);

        Assert.Empty(KubeconfigPolicy.AdmissionViolations(yaml));
        Assert.NotNull(cache.GetClient(serverId));
    }

    [Theory]
    [InlineData("    client-certificate-data: dGVzdA==\n    client-key-data: dGVzdA==")]
    [InlineData("    username: admin\n    password: placeholder")]
    [InlineData("    token: placeholder")]
    public void Every_inline_credential_form_is_admitted(string userBlock)
    {
        // Parsed for real (unreadable YAML would also show no violations) and admitted.
        var config = KubeconfigPolicy.ParseAndAdmit(Kubeconfig(userBlock));
        Assert.Single(config.Users!);
    }

    [Fact]
    public void An_exec_block_reached_through_a_yaml_alias_never_becomes_a_client()
    {
        // The anchor sits on a harmless-looking key; the user takes it by alias. The client's own reader refuses the alias
        // (21.0.8: AnchorNotFoundException); should an upgrade read it, the policy sees the exec block on the same object.
        var yaml = """
            apiVersion: v1
            kind: Config
            x-plugin: &plugin
              exec:
                apiVersion: client.authentication.k8s.io/v1beta1
                command: /bin/sh
            clusters:
            - name: c
              cluster:
                server: https://127.0.0.1:1
            contexts:
            - name: x
              context:
                cluster: c
                user: u
            current-context: x
            users:
            - name: u
              user: *plugin
            """.Replace("\r\n", "\n");

        // Refused by the policy or not readable at all: either way no client is built from it.
        Assert.ThrowsAny<Exception>(() => KubeconfigPolicy.ParseAndAdmit(yaml));
        var (cache, serverId) = CacheWith(yaml);
        Assert.ThrowsAny<Exception>(() => cache.GetClient(serverId));
    }

    [Fact]
    public void A_user_without_credentials_and_an_empty_user_list_are_admitted()
    {
        Assert.Single(KubeconfigPolicy.ParseAndAdmit(Kubeconfig("    {}")).Users!);
        Assert.Empty(KubeconfigPolicy.ParseAndAdmit("""
            apiVersion: v1
            kind: Config
            clusters: []
            contexts: []
            users: []
            """).Users ?? []);
    }

    [Fact]
    public void Unreadable_yaml_is_not_refused_by_the_page_check_but_never_becomes_a_client()
    {
        Assert.Empty(KubeconfigPolicy.AdmissionViolations("yaml-A"));
        var (cache, serverId) = CacheWith("yaml-A");
        Assert.ThrowsAny<Exception>(() => cache.GetClient(serverId));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    /// <summary>A kubeconfig whose only user runs <paramref name="command"/> as an exec credential plugin.</summary>
    internal static string ExecKubeconfig(string command, string[] args)
    {
        static string Quoted(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return Kubeconfig($"""
                exec:
                  apiVersion: client.authentication.k8s.io/v1beta1
                  command: {Quoted(command)}
                  args: [{string.Join(", ", args.Select(Quoted))}]
            """);
    }

    /// <summary>An exec plugin that leaves a file behind when it runs (a script on Linux, cmd on Windows).
    ///
    /// <para>Not <c>sh -c "…"</c> on Linux: the client joins the args with spaces into one command line, which .NET splits
    /// again, so <c>sh</c> would get <c>-c echo</c> and the redirect as separate words and never write the marker. An
    /// executable script with no args has nothing to split.</para></summary>
    private string MarkerExecKubeconfig()
    {
        if (OperatingSystem.IsWindows())
            return ExecKubeconfig("cmd", ["/c", $"echo x> {_marker}"]);

        var script = Path.Combine(_dir, "plugin.sh");
        File.WriteAllText(script, $"#!/bin/sh\necho x > '{_marker}'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return ExecKubeconfig(script, []);
    }

    private static string Kubeconfig(string userBlock, string cluster = "insecure-skip-tls-verify: true") => $"""
        apiVersion: v1
        kind: Config
        clusters:
        - name: c
          cluster:
            server: https://127.0.0.1:1
            {cluster}
        contexts:
        - name: x
          context:
            cluster: c
            user: u
        current-context: x
        users:
        - name: u
          user:
        {userBlock}
        """.Replace("\r\n", "\n");

    private static (KubernetesClientCache Cache, string ServerId) CacheWith(string yaml)
    {
        var server = new ServerConfig { Id = "k8s", Name = "k8s", ConnectionType = ConnectionType.Kubernetes };
        var vault = new OneSecretVault(KubernetesClientCache.KubeconfigKeyOf(server), yaml);
        return (new KubernetesClientCache(new FakeServerConfig(server), vault), server.Id);
    }

    private sealed class OneSecretVault(string key, string value) : IVaultService
    {
        public bool IsEnabled => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => [new VaultEntry { Key = key }];
        public Task SetSecretAsync(string k, string plainValue, string? containerId = null, string? containerName = null,
            int? rotateAfterDays = null) => throw new NotSupportedException();
        public string? GetSecret(string k) => k == key ? value : null;
        public Task DeleteSecretAsync(string k) => throw new NotSupportedException();
        public List<VaultEntry> GetExpiringSecrets() => [];
    }
}

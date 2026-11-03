using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Tests;

/// <summary>
/// R1-46 (F-12, F-20): the Helm chart's deployment defaults.
///
/// <list type="bullet">
/// <item>the Tailscale sidecar is pinned (release tag and digest, never <c>latest</c>) and its node identity is durable
/// (a PVC, with containerboot's Kubernetes-Secret storage explicitly off and the auth key used once);</item>
/// <item>the VPN mode is set explicitly on every render (<c>VPN_PROVIDER=none</c>, <c>Vpn__Provider=none</c>), so the
/// non-root, read-only pod never tries the image's legacy tailscaled bring-up — checked against the real
/// <c>entrypoint.sh</c> — and <c>extraEnv</c> cannot override it;</item>
/// <item>PostgreSQL without a connection-string Secret, an unknown provider and unpinned images are refused at render time;</item>
/// <item>the startup budget outlasts a pre-upgrade backup and migrations, and no secret has a default value.</item>
/// </list>
///
/// The structural checks read the chart's files and always run. The render checks run <c>helm template</c>; they skip
/// where helm is not installed and fail on a CI runner without it (GitHub's ubuntu runners ship helm).
/// </summary>
public class HelmDeploymentTests : IDisposable
{
    private const string PinnedImage = @"^[^\s@:]+(/[^\s@:]+)*:v?\d+\.\d+\.\d+@sha256:[0-9a-f]{64}$";
    private readonly DirectoryInfo _tmp = Directory.CreateTempSubdirectory("whiskers-helm-");

    public void Dispose()
    {
        try { _tmp.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private static string ChartDir => DeploymentScripts.RepoFile("deploy", "helm", "whiskers");
    private static string Deployment => DeploymentScripts.ReadRepoFile("deploy", "helm", "whiskers", "templates", "deployment.yaml");
    private static string Helpers => DeploymentScripts.ReadRepoFile("deploy", "helm", "whiskers", "templates", "_helpers.tpl");
    private static string Pvc => DeploymentScripts.ReadRepoFile("deploy", "helm", "whiskers", "templates", "pvc.yaml");

    private static YamlMappingNode Values()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(DeploymentScripts.ReadRepoFile("deploy", "helm", "whiskers", "values.yaml")));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string Scalar(YamlNode node, params string[] path)
    {
        foreach (var key in path) node = ((YamlMappingNode)node).Children[new YamlScalarNode(key)];
        return ((YamlScalarNode)node).Value ?? "";
    }

    // ---- structure (always) -----------------------------------------------------------------------------------

    [Fact]
    public void The_sidecar_image_is_pinned_to_a_release_and_a_digest()
    {
        var image = Scalar(Values(), "tailscaleSidecar", "image");
        Assert.Matches(PinnedImage, image);
        Assert.DoesNotContain(":latest", image);
    }

    [Fact]
    public void The_sidecar_state_is_durable_by_default()
    {
        Assert.Equal("true", Scalar(Values(), "tailscaleSidecar", "state", "persistent"));

        var sidecar = SidecarBlock();
        // containerboot's default inside Kubernetes is a Secret named "tailscale" (which the chart grants no RBAC for):
        // it must be switched off explicitly, or TS_STATE_DIR is not where the state goes.
        Assert.Matches(@"- name: TS_KUBE_SECRET\n\s*value: """"\n", sidecar);
        Assert.Matches(@"- name: TS_STATE_DIR\n\s*value: /var/lib/tailscale\n", sidecar);
        Assert.Matches(@"- name: TS_AUTH_ONCE\n\s*value: ""true""\n", sidecar);

        var volume = Deployment[LineIndex(Deployment, "        - name: tailscale-state")..];
        Assert.Matches(@"^        - name: tailscale-state\n\s*\{\{- if \.Values\.tailscaleSidecar\.state\.persistent \}\}\n\s*persistentVolumeClaim:\n\s*claimName: \{\{ include ""whiskers\.tailscaleStateClaim"" \. \}\}\n", volume);

        Assert.Contains("kind: PersistentVolumeClaim", Pvc[Pvc.IndexOf("{{- with .Values.tailscaleSidecar }}", StringComparison.Ordinal)..]);
        Assert.Contains("name: {{ include \"whiskers.tailscaleStateClaim\" $ }}", Pvc);
    }

    [Fact]
    public void The_vpn_mode_is_explicit_in_the_app_container_on_every_render()
    {
        var sidecarStart = LineIndex(Deployment, "        - name: tailscale");
        Assert.True(sidecarStart > 0);
        foreach (var name in new[] { "VPN_PROVIDER", "Vpn__Provider" })
        {
            var match = Regex.Match(Deployment, $@"            - name: {name}\n              value: ""none""\n");
            Assert.True(match.Success, $"{name}=none is missing from the app container's env");
            Assert.True(match.Index < sidecarStart, $"{name} must be in the Whiskers container, not the sidecar");
            Assert.True(TemplateDepthAt(Deployment, match.Index) == 0, $"{name} must not depend on a value (it sits inside an if/with/range)");
        }
    }

    [Fact]
    public void Every_render_is_validated_first()
    {
        Assert.StartsWith("{{- include \"whiskers.validate\" . }}\n", Deployment);
        foreach (var refusal in new[]
                 {
                     "database.provider=postgres needs database.existingSecret",
                     "database.provider must be sqlite or postgres",
                     "is not pinned",
                     "image.tag \\\"latest\\\" is refused",
                     "the chart fixes the VPN mode",
                 })
            Assert.Contains(refusal, Helpers);
        Assert.Contains("list \"vpn_provider\" \"vpn__provider\" \"tailscale_authkey\" \"vpn_force_legacy_tailscale\"", Helpers);
    }

    [Fact]
    public void The_startup_budget_outlasts_a_pre_upgrade_backup_and_migrations()
    {
        var values = Values();
        var period = int.Parse(Scalar(values, "probes", "startup", "periodSeconds"), System.Globalization.CultureInfo.InvariantCulture);
        var failures = int.Parse(Scalar(values, "probes", "startup", "failureThreshold"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(period * failures >= 300, $"startup budget {period * failures} s < 5 min");
        Assert.Contains("periodSeconds: {{ .Values.probes.startup.periodSeconds }}", Deployment);
        Assert.Contains("failureThreshold: {{ .Values.probes.startup.failureThreshold }}", Deployment);
    }

    [Fact]
    public void No_secret_has_a_default_value()
    {
        var values = Values();
        Assert.Equal("", Scalar(values, "vault", "key"));
        Assert.Equal("", Scalar(values, "vault", "existingSecret"));
        Assert.Equal("", Scalar(values, "database", "connectionString"));
        Assert.Equal("", Scalar(values, "database", "existingSecret"));
        Assert.Equal("", Scalar(values, "tailscaleSidecar", "authKeySecret"));
        Assert.Equal("", Scalar(values, "auth", "adminPasswordSecret"));
        Assert.Equal("", Scalar(values, "image", "tag"));
    }

    [Fact]
    public void The_install_notes_are_english()
    {
        var notes = DeploymentScripts.ReadRepoFile("deploy", "helm", "whiskers", "templates", "NOTES.txt");
        Assert.DoesNotMatch("[äöüÄÖÜß]", notes);
        Assert.Contains("is installed", notes);
        Assert.Contains("setup-token.txt", notes);
        Assert.Contains("Upgrade:", notes);
    }

    // ---- the real entrypoint with the chart's environment -----------------------------------------------------

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_charts_environment_keeps_the_entrypoint_off_the_legacy_vpn_daemon(bool chartEnv)
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh");
        var bin = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "bin")).FullName;
        var record = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "record")).FullName;
        var secrets = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "secrets")).FullName;
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "tailscaled"), $"#!/bin/sh\ntouch '{record}/tailscaled'\nexit 0\n");
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "tailscale"), "#!/bin/sh\nexit 1\n");
        DeploymentScripts.WriteExecutable(Path.Combine(bin, "dotnet"), $"#!/bin/sh\nenv > '{record}/app-env'\nexit 0\n");

        var env = new Dictionary<string, string?>
        {
            ["PATH"] = bin + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin"),
            ["WHISKERS_DATA_DIR"] = Path.Combine(_tmp.FullName, "data"),
            ["WHISKERS_SECRET_DIR"] = secrets,
            ["TMPDIR"] = secrets,
            ["VPN_FORCE_LEGACY_TAILSCALE"] = "1", // also on a runner that has tailscale0
            ["VPN_PROVIDER"] = null,
        };
        foreach (var prefix in new[] { "", "DOTNET_", "ASPNETCORE_" }) env[prefix + "Vpn__Provider"] = null;
        if (chartEnv)
            foreach (var (name, value) in ChartVpnEnvironment()) env[name] = value;

        var (exit, stdout, stderr) = DeploymentScripts.Run("sh", [DeploymentScripts.RepoFile("entrypoint.sh")], env);
        Assert.True(exit == 0, stdout + stderr);
        Assert.True(File.Exists(Path.Combine(record, "app-env")), "Whiskers was not started");

        if (chartEnv)
        {
            Assert.False(File.Exists(Path.Combine(record, "tailscaled")), "the legacy tailscaled was started: " + stdout + stderr);
            Assert.Contains("skipping legacy bring-up", stdout);
            Assert.Contains("Vpn__Provider=none", File.ReadAllText(Path.Combine(record, "app-env")));
        }
        else
        {
            // The control: without the chart's variables the same image does start tailscaled — which is what the
            // chart used to do as uid 10001 on a read-only root file system.
            Assert.True(File.Exists(Path.Combine(record, "tailscaled")), "control run did not reach the legacy bring-up");
        }
    }

    /// <summary>The literal VPN variables of the app container in <c>deployment.yaml</c>.</summary>
    private static IEnumerable<(string Name, string Value)> ChartVpnEnvironment()
    {
        var app = Deployment[..LineIndex(Deployment, "        - name: tailscale")];
        var found = Regex.Matches(app, @"- name: (?<n>VPN_PROVIDER|Vpn__Provider)\n\s*value: ""(?<v>[^""]*)""\n")
            .Select(m => (m.Groups["n"].Value, m.Groups["v"].Value)).ToList();
        Assert.Equal(2, found.Count);
        return found;
    }

    // ---- helm template (real renders) -------------------------------------------------------------------------

    [SkippableFact]
    public void The_default_render_sets_the_vpn_mode_and_names_no_latest_image()
    {
        var docs = Render();
        var app = Container(docs, "whiskers");
        Assert.Equal("none", Env(app, "VPN_PROVIDER"));
        Assert.Equal("none", Env(app, "Vpn__Provider"));
        Assert.Equal("true", Env(app, "WHISKERS_DISABLE_LOCAL_DOCKER"));
        foreach (var image in Images(docs)) Assert.DoesNotContain(":latest", image);
        Assert.Null(TryContainer(docs, "tailscale"));
    }

    [SkippableFact]
    public void The_sidecar_render_is_pinned_and_keeps_its_identity_on_a_pvc()
    {
        var docs = Render("tailscaleSidecar.enabled=true", "tailscaleSidecar.authKeySecret=ts-auth");
        var sidecar = Container(docs, "tailscale");
        Assert.Equal(Scalar(Values(), "tailscaleSidecar", "image"), Scalar(sidecar, "image"));
        Assert.Equal("", Env(sidecar, "TS_KUBE_SECRET"));
        Assert.Equal("true", Env(sidecar, "TS_AUTH_ONCE"));
        Assert.Equal("/var/lib/tailscale", Env(sidecar, "TS_STATE_DIR"));

        var claim = Scalar(Volume(docs, "tailscale-state"), "persistentVolumeClaim", "claimName");
        Assert.Contains(docs, d => Kind(d) == "PersistentVolumeClaim" && Scalar(d, "metadata", "name") == claim);
        // The app container's VPN mode does not change with the sidecar.
        Assert.Equal("none", Env(Container(docs, "whiskers"), "VPN_PROVIDER"));
    }

    [SkippableFact]
    public void A_non_persistent_sidecar_is_explicit_and_logs_in_every_start()
    {
        var docs = Render("tailscaleSidecar.enabled=true", "tailscaleSidecar.authKeySecret=ts-auth", "tailscaleSidecar.state.persistent=false");
        Assert.True(Volume(docs, "tailscale-state").Children.ContainsKey(new YamlScalarNode("emptyDir")));
        Assert.Null(TryEnv(Container(docs, "tailscale"), "TS_AUTH_ONCE"));
        Assert.DoesNotContain(docs, d => Kind(d) == "PersistentVolumeClaim" && Scalar(d, "metadata", "name").EndsWith("-tailscale", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Postgres_reads_its_connection_string_from_the_named_secret()
    {
        var docs = Render("database.provider=postgres", "database.existingSecret=whiskers-db");
        var app = Container(docs, "whiskers");
        Assert.Equal("postgres", Env(app, "WHISKERS_DB_PROVIDER"));
        var env = EnvEntry(app, "WHISKERS_DB_CONNECTION")!;
        Assert.Equal("whiskers-db", Scalar(env, "valueFrom", "secretKeyRef", "name"));
        Assert.Equal("connectionString", Scalar(env, "valueFrom", "secretKeyRef", "key"));
    }

    [SkippableTheory]
    [InlineData("database.provider must be sqlite or postgres", "database.provider=mysql")]
    [InlineData("database.provider=postgres needs database.existingSecret", "database.provider=postgres")]
    [InlineData("is not pinned", "tailscaleSidecar.enabled=true", "tailscaleSidecar.authKeySecret=ts", "tailscaleSidecar.image=tailscale/tailscale:latest")]
    [InlineData("is not pinned", "tailscaleSidecar.enabled=true", "tailscaleSidecar.authKeySecret=ts", "tailscaleSidecar.image=tailscale/tailscale")]
    [InlineData("tailscaleSidecar.authKeySecret is required", "tailscaleSidecar.enabled=true")]
    [InlineData("the chart fixes the VPN mode", "extraEnv[0].name=VPN_PROVIDER", "extraEnv[0].value=tailscale")]
    [InlineData("the chart fixes the VPN mode", "extraEnv[0].name=DOTNET_Vpn__Provider", "extraEnv[0].value=tailscale")]
    [InlineData("the chart fixes the VPN mode", "extraEnv[0].name=TAILSCALE_AUTHKEY", "extraEnv[0].value=x")]
    [InlineData("is refused", "image.tag=latest")]
    public void A_render_that_cannot_work_is_refused(string message, params string[] set)
    {
        var (exit, output) = HelmTemplate(set);
        Assert.NotEqual(0, exit);
        Assert.Contains(message, output);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>The sidecar container's block in <c>deployment.yaml</c> (up to the volumes).</summary>
    private static string SidecarBlock()
    {
        var start = LineIndex(Deployment, "        - name: tailscale");
        return Deployment[start..Deployment.IndexOf("      volumes:\n", start, StringComparison.Ordinal)];
    }

    /// <summary>Where the line that is exactly <paramref name="line"/> starts (indentation counts).</summary>
    private static int LineIndex(string text, string line)
    {
        var match = Regex.Match(text, "(?m)^" + Regex.Escape(line) + "$");
        Assert.True(match.Success, $"no line '{line}'");
        return match.Index;
    }

    private static readonly Regex TemplateAction = new(@"\{\{-?(?<body>.*?)-?\}\}", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>How many <c>if</c>/<c>with</c>/<c>range</c>/<c>define</c> blocks are open at <paramref name="index"/>.</summary>
    internal static int TemplateDepthAt(string template, int index)
    {
        var depth = 0;
        foreach (Match m in TemplateAction.Matches(template))
        {
            if (m.Index >= index) break;
            var body = m.Groups["body"].Value.Trim();
            if (body.StartsWith("/*", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(body, @"^(if|with|range|define|block)\b")) depth++;
            else if (Regex.IsMatch(body, @"^end\b")) depth--;
        }
        return depth;
    }

    [Fact]
    public void The_depth_check_sees_a_conditional()
    {
        const string text = "a\n{{- if .Values.x }}\nb\n{{- end }}\nc\n";
        Assert.Equal(0, TemplateDepthAt(text, text.IndexOf('a')));
        Assert.Equal(1, TemplateDepthAt(text, text.IndexOf('b')));
        Assert.Equal(0, TemplateDepthAt(text, text.IndexOf('c')));
        // A guarded VPN_PROVIDER, as a mutation of the real template, is caught.
        var mutated = Deployment.Replace("            - name: VPN_PROVIDER\n",
            "            {{- if .Values.vpnExplicit }}\n            - name: VPN_PROVIDER\n", StringComparison.Ordinal)
            .Replace("            - name: Vpn__Provider\n              value: \"none\"\n",
                "            - name: Vpn__Provider\n              value: \"none\"\n            {{- end }}\n", StringComparison.Ordinal);
        Assert.Equal(1, TemplateDepthAt(mutated, mutated.IndexOf("- name: VPN_PROVIDER", StringComparison.Ordinal)));
    }

    private static string? Helm()
    {
        var helm = DeploymentScripts.FindOnPath("helm");
        if (helm is null && DeploymentScripts.InCi) Assert.Fail("helm is required on CI to render the chart");
        Skip.If(helm is null, "helm is not installed; the structural checks above still ran");
        return helm;
    }

    private static (int Exit, string Output) HelmTemplate(params string[] set)
    {
        var helm = Helm();
        var args = new List<string> { "template", "test", ChartDir };
        foreach (var s in set) { args.Add("--set"); args.Add(s); }
        var (exit, stdout, stderr) = DeploymentScripts.Run(helm!, args);
        return (exit, stdout + stderr);
    }

    private static List<YamlMappingNode> Render(params string[] set)
    {
        var helm = Helm();
        var args = new List<string> { "template", "test", ChartDir };
        foreach (var s in set) { args.Add("--set"); args.Add(s); }
        var (exit, stdout, stderr) = DeploymentScripts.Run(helm!, args);
        Assert.True(exit == 0, stderr);
        var stream = new YamlStream();
        stream.Load(new StringReader(stdout));
        return stream.Documents.Select(d => d.RootNode).OfType<YamlMappingNode>().ToList();
    }

    private static string Kind(YamlMappingNode doc)
        => doc.Children.TryGetValue(new YamlScalarNode("kind"), out var k) ? ((YamlScalarNode)k).Value ?? "" : "";

    private static YamlMappingNode PodSpec(List<YamlMappingNode> docs)
    {
        var deployment = Assert.Single(docs, d => Kind(d) == "Deployment");
        return (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)deployment["spec"])["template"])["spec"];
    }

    private static IEnumerable<string> Images(List<YamlMappingNode> docs)
        => ((YamlSequenceNode)PodSpec(docs)["containers"]).Cast<YamlMappingNode>().Select(c => Scalar(c, "image"));

    private static YamlMappingNode? TryContainer(List<YamlMappingNode> docs, string name)
        => ((YamlSequenceNode)PodSpec(docs)["containers"]).Cast<YamlMappingNode>().SingleOrDefault(c => Scalar(c, "name") == name);

    private static YamlMappingNode Container(List<YamlMappingNode> docs, string name)
        => TryContainer(docs, name) ?? throw new Xunit.Sdk.XunitException($"no container {name}");

    private static YamlMappingNode Volume(List<YamlMappingNode> docs, string name)
        => ((YamlSequenceNode)PodSpec(docs)["volumes"]).Cast<YamlMappingNode>().Single(v => Scalar(v, "name") == name);

    private static YamlMappingNode? EnvEntry(YamlMappingNode container, string name)
        => container.Children.TryGetValue(new YamlScalarNode("env"), out var env)
            ? ((YamlSequenceNode)env).Cast<YamlMappingNode>().SingleOrDefault(e => Scalar(e, "name") == name)
            : null;

    private static string? TryEnv(YamlMappingNode container, string name)
        => EnvEntry(container, name) is { } e && e.Children.TryGetValue(new YamlScalarNode("value"), out var v) ? ((YamlScalarNode)v).Value ?? "" : null;

    private static string Env(YamlMappingNode container, string name)
        => TryEnv(container, name) ?? throw new Xunit.Sdk.XunitException($"env {name} is not set");
}

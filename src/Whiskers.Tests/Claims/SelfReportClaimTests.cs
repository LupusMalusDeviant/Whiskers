using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;
using Whiskers.Tests.ReleaseGate;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Tests.Claims;

/// <summary>
/// Contract 0011, Part B (INV-56): the claims of the governed documents that no other test proves, each against the code
/// or a repository file (D12). The claim ids are in <c>docs/v1/CLAIMS.md</c>; a quantifier in a claim is checked over
/// every member, never a sample.
/// </summary>
public class SelfReportClaimTests
{
    private static string Read(string relative) => GateFiles.Require(relative);

    private static YamlMappingNode Service(string composeFile, string service)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(Read(composeFile)));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        return (YamlMappingNode)((YamlMappingNode)root.Children[new YamlScalarNode("services")]).Children[new YamlScalarNode(service)];
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode s ? s.Value : null;

    private static IReadOnlyList<string> Sequence(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlSequenceNode seq
            ? seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "").ToList()
            : [];

    // ---- README -------------------------------------------------------------------------------------------------

    /// <summary>RM-02, RM-11, RM-17, RM-20, RM-21, RM-22, PC-01, PC-09: every row of the README's configuration table
    /// maps its <c>.env</c> name to the named container variable in the release <c>docker-compose.yml</c>, and the one
    /// row that says "not mapped" is not.</summary>
    [Fact]
    public void The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable()
    {
        var app = Service("docker-compose.yml", "serverwatch");
        var environment = Sequence(app, "environment");
        var mappings = environment.Select(e => Regex.Match(e, @"^(?<var>[A-Za-z0-9_]+)=\$\{(?<env>[A-Z0-9_]+):-(?<default>[^}]*)\}$"))
            .Where(m => m.Success).ToDictionary(m => m.Groups["var"].Value, m => (Env: m.Groups["env"].Value, Default: m.Groups["default"].Value));

        foreach (var (variable, env) in new[]
                 {
                     ("Auth__Disabled", "AUTH_DISABLED"), ("VAULT_KEY", "VAULT_KEY"), ("VAULT_KEY_FILE", "VAULT_KEY_FILE"),
                     ("Oidc__Enabled", "OIDC_ENABLED"), ("Oidc__Authority", "OIDC_AUTHORITY"), ("Oidc__ClientId", "OIDC_CLIENT_ID"),
                     ("Oidc__ClientSecret", "OIDC_CLIENT_SECRET"), ("Oidc__RequireHttpsMetadata", "OIDC_REQUIRE_HTTPS"),
                     ("PathBase", "PATH_BASE"),
                 })
        {
            Assert.True(mappings.TryGetValue(variable, out var mapped), $"{variable} is not mapped from the .env");
            Assert.Equal(env, mapped.Env);
        }
        Assert.Equal("false", mappings["Auth__Disabled"].Default);
        Assert.Equal("true", mappings["Oidc__RequireHttpsMetadata"].Default);
        Assert.Equal(["${HOST_BIND:-127.0.0.1}:${HOST_PORT:-5100}:8080"], Sequence(app, "ports"));
        Assert.DoesNotContain(environment, e => e.StartsWith("ForwardedHeaders", StringComparison.Ordinal));
    }

    /// <summary>RM-06, RM-19: the data directory is <c>/app/data</c>, moved by <c>WHISKERS_DATA_DIR</c>; SQLite is the
    /// default provider and PostgreSQL the alternative; the PostgreSQL overlay needs <c>WHISKERS_DB_PASSWORD</c>.</summary>
    [Fact]
    public void Data_lives_in_app_data_sqlite_is_the_default_and_the_overlay_needs_a_password()
    {
        Assert.Equal("/app/data", new DataPathOptions().RootDir);
        Assert.Equal("WHISKERS_DATA_DIR", DataPathOptions.EnvVarName);
        Assert.Equal("sqlite", new DatabaseOptions().Provider);
        Assert.Equal("sqlite", Whiskers.Services.Persistence.DatabaseRegistration.Resolve(new ConfigurationBuilder().Build()).Provider);
        Assert.Equal("postgres", Whiskers.Services.Backup.SelfBackupFormat.ProviderPostgres);
        var overlay = Read("deploy/docker-compose.postgres.yml");
        Assert.Contains("WHISKERS_DB_PROVIDER=postgres", overlay);
        Assert.Contains("${WHISKERS_DB_PASSWORD:?", overlay);   // compose refuses to start without it
    }

    /// <summary>RM-07, SEC-07, SA-01: a vault entry is AES-256-GCM (12-byte nonce, 16-byte tag) under a key derived with
    /// PBKDF2-HMAC-SHA256 (600,000 iterations) from <c>VAULT_KEY</c> and the vault's salt — opened here independently
    /// of the vault's own code.</summary>
    [Fact]
    public async Task The_vault_encrypts_with_aes_256_gcm_under_a_pbkdf2_key()
    {
        var root = Path.Combine(Path.GetTempPath(), "sw-tests", "claims-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPathOptions(root);
            const string passphrase = "claims-test-passphrase";
            var vault = new VaultService(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["VAULT_KEY"] = passphrase }).Build(), NullLogger<VaultService>.Instance, dataPaths: paths);
            await vault.InitializeAsync();
            await vault.SetSecretAsync("claim", "the secret value");

            var file = JsonNode.Parse(await File.ReadAllTextAsync(paths.VaultJson))!.AsObject();
            JsonNode? Get(JsonObject o, string name) => o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            var salt = Convert.FromBase64String((string)Get(file, "KdfSalt")!);
            var entry = Get(file, "Entries")!.AsArray().Select(e => e!.AsObject()).Single(e => (string?)Get(e, "Key") == "claim");
            var stored = (string)Get(entry, "EncryptedValue")!;
            Assert.StartsWith("g1:", stored);
            Assert.DoesNotContain("the secret value", await File.ReadAllTextAsync(paths.VaultJson));

            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, 600_000, HashAlgorithmName.SHA256, 32);
            Assert.Equal(32, key.Length);                              // AES-256
            var blob = Convert.FromBase64String(stored[3..]);
            var plain = new byte[blob.Length - 28];
            using (var gcm = new AesGcm(key, 16))
                gcm.Decrypt(blob[..12], blob[28..], blob[12..28], plain);
            Assert.Equal("the secret value", Encoding.UTF8.GetString(plain));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>RM-15: the cloud APIs Whiskers drives are exactly Hetzner and Hostinger.</summary>
    [Fact]
    public void The_cloud_targets_are_hetzner_and_hostinger()
        => Assert.Equal([CloudProvider.Hetzner, CloudProvider.Hostinger], Enum.GetValues<CloudProvider>().Where(p => p != CloudProvider.None));

    /// <summary>RM-16, RM-23: .NET 10 and the Apache License 2.0.</summary>
    [Fact]
    public void The_stack_is_dotnet_10_under_the_apache_license_2()
    {
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", Read("src/Whiskers/Whiskers.csproj"));
        var license = Read("LICENSE");
        Assert.Matches(@"^\s*Apache License\s+Version 2\.0,", license);
    }

    /// <summary>RM-13: each command of a compose run has 5 minutes (<c>ComposeImpact.RunBudget</c>, the one value its
    /// <c>ComposeRunOptions</c> gets); the Git deploy keeps 300 s pull, 600 s build, 300 s up
    /// (<c>GitDeployService</c>, the values of its <c>ComposeRunOptions</c>).</summary>
    [Fact]
    public void The_compose_run_budget_is_five_minutes_and_git_deploy_keeps_300_600_300_seconds()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Whiskers.Services.Mutations.ComposeImpact.RunBudget);
        Assert.Equal(TimeSpan.FromSeconds(300), Whiskers.Services.GitDeploy.GitDeployService.DeployPullBudget);
        Assert.Equal(TimeSpan.FromSeconds(600), Whiskers.Services.GitDeploy.GitDeployService.DeployBuildBudget);
        Assert.Equal(TimeSpan.FromSeconds(300), Whiskers.Services.GitDeploy.GitDeployService.DeployUpBudget);
    }

    /// <summary>RM-08, SA-12: both images are built with SLSA provenance and an SBOM attestation and signed keyless with
    /// cosign (the workflow's OIDC token, no key).</summary>
    [Fact]
    public void The_release_attests_provenance_and_an_sbom_and_signs_keyless()
    {
        var release = Read(".github/workflows/release.yml");
        var builds = Regex.Matches(release, @"uses: docker/build-push-action@").Count;
        Assert.True(builds >= 2, "both architectures are built");
        Assert.Equal(builds, Regex.Matches(release, @"(?m)^\s+provenance: true\s*$").Count);
        Assert.Equal(builds, Regex.Matches(release, @"(?m)^\s+sbom: true\s*$").Count);
        Assert.Matches(@"(?m)^\s+id-token: write", release);
        var signs = Regex.Matches(release, @"cosign sign [^\n]*").Select(m => m.Value).ToList();
        Assert.NotEmpty(signs);
        Assert.All(signs, s => Assert.DoesNotContain("--key", s));
    }

    // ---- SECURITY.md --------------------------------------------------------------------------------------------

    /// <summary>SEC-01: the supported line named in SECURITY.md is the version of this source tree.</summary>
    [Fact]
    public void The_supported_line_is_the_version_of_this_source_tree()
    {
        var version = ReleaseGate.ReleaseGate.ReadVersion(Read("src/Whiskers/Whiskers.csproj"));
        var line = Regex.Match(Read("SECURITY.md"), @"\*\*(?<stage>beta|stable)\*\* \(`(?<line>\d+\.\d+)\.x`\)");
        Assert.True(line.Success, "SECURITY.md names no supported line");
        Assert.Equal($"{version.Major}.{version.Minor}", line.Groups["line"].Value);
        // From 1.0 on the line is stable; a 0.x line is beta.
        Assert.Equal(version.Major >= 1 ? "stable" : "beta", line.Groups["stage"].Value);
    }

    /// <summary>SEC-03: every role maps to its scope — Viewer → Read, Operator → Write, Admin → Admin — through
    /// <c>CapabilityCatalog.LevelFor</c>, the one map <c>McpPermissionCheck</c>, the agent and the approvals use.</summary>
    [Fact]
    public void Every_role_maps_to_its_scope()
    {
        var expected = new Dictionary<AppRole, string>
        {
            [AppRole.Viewer] = McpPermissionLevels.Read, [AppRole.Operator] = McpPermissionLevels.Write, [AppRole.Admin] = McpPermissionLevels.Admin,
        };
        Assert.Equal(Enum.GetValues<AppRole>().Order(), expected.Keys.Order());
        foreach (var role in Enum.GetValues<AppRole>())
            Assert.Equal(expected[role], Whiskers.Services.Mutations.CapabilityCatalog.LevelFor(role));
    }

    /// <summary>SEC-04: an approved call may run within 10 minutes (<c>ApprovalStore.GrantLifetime</c>, the one value the
    /// grant and the gate's message use).</summary>
    [Fact]
    public void An_approval_grant_lasts_ten_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), Whiskers.Services.Approvals.ApprovalStore.GrantLifetime);
        Assert.Equal(Whiskers.Services.Approvals.ApprovalStore.GrantLifetime, Whiskers.Services.Approvals.ApprovalGate.GrantLifetime);
    }

    /// <summary>SEC-09, SEC-28, PC-04: the default compose runs privileged in the host PID namespace as root; the
    /// hardened one is non-root, unprivileged, without the host PID namespace, with every capability dropped and a
    /// read-only root file system.</summary>
    [Fact]
    public void The_default_compose_is_privileged_and_the_hardened_one_is_not()
    {
        var full = Service("docker-compose.yml", "serverwatch");
        Assert.Equal("true", Scalar(full, "privileged"));
        Assert.Equal("host", Scalar(full, "pid"));
        Assert.Null(Scalar(full, "user"));

        var hardened = Service("docker-compose.hardened.yml", "serverwatch");
        Assert.Null(Scalar(hardened, "privileged"));
        Assert.Null(Scalar(hardened, "pid"));
        Assert.Equal("10001:10001", Scalar(hardened, "user"));
        Assert.Equal(["ALL"], Sequence(hardened, "cap_drop"));
        Assert.Equal("true", Scalar(hardened, "read_only"));
        Assert.DoesNotContain(Sequence(hardened, "volumes"), v => v.StartsWith("/var/run/docker.sock", StringComparison.Ordinal));
    }

    /// <summary>SEC-21: <c>.env</c> files and the data directory are ignored by git and kept out of the image's build
    /// context, and the image declares <c>/app/data</c> as the data volume instead of copying data in.</summary>
    [Fact]
    public void Secrets_are_neither_committed_nor_in_the_image_build_context()
    {
        var git = Read(".gitignore").Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(".env", git);
        Assert.Contains(".env.*", git);
        Assert.Contains("data/", git);
        var docker = Read(".dockerignore").Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(".env", docker);
        Assert.Contains(".env.*", docker);
        var copies = Regex.Matches(Read("Dockerfile"), @"(?m)^COPY\s+(?<from>\S+)").Select(m => m.Groups["from"].Value).ToList();
        Assert.All(copies, c => Assert.True(c.StartsWith("src/", StringComparison.Ordinal) || c.StartsWith("deploy/keys/", StringComparison.Ordinal)
                                            || c.StartsWith("--from=", StringComparison.Ordinal) || c is "Directory.Build.props" or "entrypoint.sh" or "global.json",
            $"the image copies {c}"));
    }

    /// <summary>SEC-11: a restore refuses more than 100,000 entries or 32 GiB of content (the default limits every
    /// restore extracts with).</summary>
    [Fact]
    public void The_restore_limits_are_100000_entries_and_32_gib()
    {
        var limits = Whiskers.Services.Backup.BackupExtractionLimits.Default;
        Assert.Equal(100_000, limits.MaxEntries);
        Assert.Equal(32L * 1024 * 1024 * 1024, limits.MaxTotalBytes);
    }

    /// <summary>SEC-02, PC-06: the Helm chart pins one replica with the Recreate strategy (no value can scale it).</summary>
    [Fact]
    public void The_helm_chart_runs_one_replica_with_recreate()
    {
        var deployment = Read("deploy/helm/whiskers/templates/deployment.yaml");
        Assert.Matches(@"(?m)^  replicas: 1\s*$", deployment);
        Assert.Matches(@"(?m)^    type: Recreate\s*$", deployment);
        Assert.DoesNotMatch(@"replicas:\s*\{\{", deployment);
    }

    /// <summary>SEC-08: an uploaded SSH key is written owner-only (mode 0600; Unix, where CI runs).</summary>
    [Fact]
    public async Task An_uploaded_ssh_key_is_written_owner_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "sw-tests", "claims-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataPathOptions(root);
            var servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "npipe://test" }),
                NullLogger<ServerConfigService>.Instance, dataPaths: paths, sealedKeys: new SealedKeyFiles(TestVault.Usable, paths));
            await servers.InitializeAsync();
            // A key belongs to an existing server; an upload for a missing one is refused (INV-59, #187).
            await servers.AddServerAsync(new ServerConfig { Id = "edge", Name = "edge", ConnectionType = ConnectionType.SSH,
                SshHost = "127.0.0.1", SshPort = 22, SshUser = "probe" });
            await servers.SaveSshKeyAsync("edge", "id_ed25519", Encoding.UTF8.GetBytes(SealedKeyFilesTests.NewPem()));

            var key = Directory.GetFiles(paths.SshKeysDir, "id_ed25519", SearchOption.AllDirectories).Single();
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---- production checklist -----------------------------------------------------------------------------------

    /// <summary>PC-01, RM-18: the installer binds to 127.0.0.1 unless <c>--bind</c> or <c>WHISKERS_BIND</c> says
    /// otherwise, and its compose file sets <c>TZ</c> and <c>VAULT_KEY_FILE</c>.</summary>
    [Fact]
    public void The_installer_binds_loopback_by_default_and_sets_tz_and_the_vault_key_file()
    {
        var installer = Read("deploy/install.sh");
        Assert.Contains("BIND=\"${WHISKERS_BIND:-127.0.0.1}\"", installer);
        Assert.Contains("--bind) BIND=", installer);
        Assert.Matches(@"(?m)^      - TZ=", installer);
        Assert.Matches(@"(?m)^      - VAULT_KEY_FILE=/run/secrets/", installer);
    }

    /// <summary>PC-02: a <c>…_FILE</c> variant exists for exactly the named settings. Every read of a secret file in the
    /// application — <c>SecretSource.Read</c> (NAME → NAME_FILE), <c>SecretSource.ReadFile</c> and the VPN key-file
    /// settings — is enumerated over all its source files.</summary>
    [Fact]
    public void File_variants_exist_only_for_the_named_settings()
    {
        var source = Path.Combine(GateFiles.Root, "src", "Whiskers");
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            var reads = Regex.Matches(text, @"SecretSource\.Read\([^,()]+,\s*""(?<name>[A-Z0-9_]+)""\)");
            var fileReads = Regex.Matches(text, @"SecretSource\.ReadFile\(\s*""(?<name>[A-Z0-9_]+)""");
            foreach (Match m in reads) found.Add(m.Groups["name"].Value + "_FILE");
            foreach (Match m in fileReads) found.Add(m.Groups["name"].Value);
            foreach (Match m in Regex.Matches(text, @"=>\s*Read\(configuration,\s*""(?<name>[A-Z0-9_]+)""\)")) found.Add(m.Groups["name"].Value + "_FILE");
            // Every call names its setting literally; a call with a computed name would escape the list.
            Assert.Equal(Regex.Matches(text, @"SecretSource\.Read(File)?\(").Count, reads.Count + fileReads.Count);
        }
        var vpn = typeof(Whiskers.Services.Vpn.VpnSettings).GetNestedTypes()
            .SelectMany(t => t.GetProperties().Where(p => p.Name.EndsWith("File", StringComparison.Ordinal)).Select(p => $"Vpn:{t.Name.Replace("Options", "")}:{p.Name}"));
        found.UnionWith(vpn);

        Assert.Equal(new SortedSet<string>(StringComparer.Ordinal)
        {
            "VAULT_KEY_FILE", "VAULT_KEY_NEW_FILE", "WHISKERS_DB_CONNECTION_FILE", "WHISKERS_ADMIN_PASSWORD_FILE",
            "Vpn:Tailscale:AuthKeyFile", "Vpn:Netbird:SetupKeyFile",
        }, found);
    }
}

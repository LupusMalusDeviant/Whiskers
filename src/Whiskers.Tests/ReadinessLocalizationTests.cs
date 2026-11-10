using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Certificates;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Setup;
using Whiskers.Services.Vault;
using Ids = Whiskers.Services.Setup.ProductionReadinessService.Ids;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0011, Part A (INV-56): the production-readiness lines come from the <c>Readiness_*</c> resources in English
/// and German. D5: both languages hold every key, translated, with the same placeholders and the same facts (numbers,
/// file, setting and flag names). D6: run with a key-echo localizer, every scenario emits nothing but resource keys and
/// separators, and every key is emitted by some scenario; run in English and German, every scenario gives the same lines
/// with the same states and the same data.
/// </summary>
public class ReadinessLocalizationTests
{
    // ---- D5: the two resource files ------------------------------------------------------------------------------

    internal static string ResourcePath(string file)
        => Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), "src", "Whiskers", "Resources", file);

    /// <summary>Key → value of a resx file, read as XML from the repository (not the compiled satellite).</summary>
    internal static IReadOnlyDictionary<string, string> Resx(string file)
        => XDocument.Load(ResourcePath(file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "", StringComparer.Ordinal);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(() => Resx("SharedResource.resx"));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> German = new(() => Resx("SharedResource.de.resx"));

    private static IEnumerable<string> ReadinessKeys(IReadOnlyDictionary<string, string> resx)
        => resx.Keys.Where(k => k.StartsWith("Readiness_", StringComparison.Ordinal));

    internal static SortedSet<string> Placeholders(string text)
        => new(Regex.Matches(text, @"\{(\d+)(?:[,:][^}]*)?\}").Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    /// <summary>The fact tokens of a text (D5): backticked spans, path and file names, setting and variable names, CLI
    /// flags and digit sequences. Placeholders are not facts.</summary>
    internal static SortedSet<string> Facts(string text)
    {
        var facts = new SortedSet<string>(StringComparer.Ordinal);
        var rest = Regex.Replace(text, @"\{\d+(?:[,:][^}]*)?\}", " ");
        foreach (Match m in Regex.Matches(rest, "`[^`]+`")) facts.Add(m.Value);
        rest = Regex.Replace(rest, "`[^`]+`", " ");
        foreach (Match m in Regex.Matches(rest, @"[\w.\-]*/[\w./\-:]*")) facts.Add("path " + m.Value);
        foreach (Match m in Regex.Matches(rest, @"\b[\w\-]+\.(?:json|md|yml|yaml|txt|xml|key|crt|sh)\b")) facts.Add("file " + m.Value);
        foreach (Match m in Regex.Matches(rest, @"\b[A-Za-z][A-Za-z0-9]*(?:__|:)[A-Za-z0-9_:]*[A-Za-z0-9]")) facts.Add("setting " + m.Value);
        foreach (Match m in Regex.Matches(rest, @"\b[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]*[A-Za-z0-9]\b")) facts.Add("name " + m.Value);
        foreach (Match m in Regex.Matches(rest, @"(?<![\w-])--[a-z][a-z-]*")) facts.Add("flag " + m.Value.TrimEnd('-'));
        foreach (Match m in Regex.Matches(rest, @"\d+")) facts.Add("digits " + m.Value);
        return facts;
    }

    [Fact]
    public void Every_readiness_key_is_in_both_languages_translated_with_the_same_placeholders_and_facts()
    {
        var en = English.Value;
        var de = German.Value;
        var problems = new List<string>();
        var keys = ReadinessKeys(en).Union(ReadinessKeys(de)).Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            if (!en.TryGetValue(key, out var e) || string.IsNullOrWhiteSpace(e)) { problems.Add($"{key}: no English value"); continue; }
            if (!de.TryGetValue(key, out var d) || string.IsNullOrWhiteSpace(d)) { problems.Add($"{key}: no German value"); continue; }
            if (e == d) problems.Add($"{key}: the German value is the English one (untranslated)");
            if (!Placeholders(e).SetEquals(Placeholders(d)))
                problems.Add($"{key}: placeholders differ (en {string.Join(",", Placeholders(e))}, de {string.Join(",", Placeholders(d))})");
            var (fe, fd) = (Facts(e), Facts(d));
            if (!fe.SetEquals(fd))
                problems.Add($"{key}: facts only in English [{string.Join("; ", fe.Except(fd))}], only in German [{string.Join("; ", fd.Except(fe))}]");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void The_fact_tokens_catch_a_number_or_a_name_in_one_language_only()
    {
        Assert.False(Facts("Not 0600: {0}.").SetEquals(Facts("Nicht 0644: {0}.")));
        Assert.False(Facts("set WHISKERS_PG_BIN_DIR").SetEquals(Facts("WHISKERS_PG_DIR setzen")));
        Assert.False(Facts("(docs/operations/a.md)").SetEquals(Facts("(docs/operations/b.md)")));
        Assert.False(Facts("narrow --allow-cn").SetEquals(Facts("einengen")));
        Assert.False(Facts("in Oidc:RequireHttpsMetadata").SetEquals(Facts("in Oidc:Authority")));
        Assert.True(Facts("Running --allow-cn list").SetEquals(Facts("Laufende --allow-cn-Liste")));
        Assert.True(Facts("{0} — too old: 17.6 (or 16.10)").SetEquals(Facts("{0} — zu alt: 17.6 (bzw. 16.10)")));
    }

    // ---- D6: scenarios over every branch --------------------------------------------------------------------------

    /// <summary>One state of the world and the readiness lines it gives with a localizer. <see cref="Data"/> are values
    /// that must appear, untranslated, in both languages.</summary>
    public sealed record Scenario(string Name, Func<IStringLocalizer<SharedResource>, Task<IReadOnlyList<ReadinessCheck>>> Run, string[] Data)
    {
        public override string ToString() => Name;
    }

    /// <summary>Keys that only the other operating system can emit (the key-file check reads modes on Unix and ACLs on
    /// Windows).</summary>
    private static readonly string[] OtherOsKeys = OperatingSystem.IsWindows()
        ? ["Readiness_mtlsKeysProtected_OkUnix", "Readiness_mtlsKeysProtected_FailUnix"]
        : ["Readiness_mtlsKeysProtected_OkWindows", "Readiness_mtlsKeysProtected_FailWindows"];

    public static IEnumerable<Scenario> Scenarios()
    {
        yield return new("defaults over HTTP, no vault", text => General(text, isHttps: false), []);
        yield return new("privileged over HTTPS", text => General(text, privileged: true), []);
        yield return new("sign-in off, no Docker", text => General(text, config: new() { ["Auth:Disabled"] = "true" }), []);
        yield return new("sign-in off, own container published", text => General(text, config: new() { ["Auth:Disabled"] = "true" },
            ownLabel: "docktail.funnel.enable"), ["docktail.funnel.enable"]);
        yield return new("plaintext everywhere, vault on", text => General(text, vaultKey: "readiness-l10n-passphrase", cloudKey: true,
            dnsToken: true, plaintextSetting: true, webhook: true), ["1", "Email:Password"]);
        yield return new("plaintext cloud key, no vault", text => General(text, cloudKey: true, plaintextSetting: true, webhook: true),
            ["1", "Email:Password"]);
        yield return new("wrong vault key", text => General(text, vaultKey: "readiness-l10n-passphrase", wrongKey: true), []);
        yield return new("backup, update policy, recovered and kept stores", text => General(text, scheduled: true,
            recovered: "roles.json", kept: true), ["roles.json", "servers.json.corrupt-20261101000000"]);
        yield return new("PostgreSQL tools new enough", text => General(text, pgVersion: "pg_dump (PostgreSQL) 17.6"),
            ["pg_dump (PostgreSQL) 17.6"]);
        yield return new("PostgreSQL tools too old", text => General(text, pgVersion: "pg_dump (PostgreSQL) 16.2"),
            ["pg_dump (PostgreSQL) 16.2"]);
        yield return new("PostgreSQL tools missing", text => General(text, pgVersion: "!pg_dump was not found"), ["pg_dump was not found"]);
        yield return new("OIDC over HTTP and an untrusted peer", text => General(text,
            config: new() { ["Oidc:Enabled"] = "true", ["Oidc:RequireHttpsMetadata"] = "false" }, untrustedPeer: "172.18.0.2"),
            ["172.18.0.2"]);
        yield return new("mTLS: one host migrated, one shared, keys exposed", MtlsMigrated,
            ["Rabenhof", "Zirkuswagen", "whiskers-client-rabenhof-g1", "1"]);
        yield return new("mTLS: live arguments stopped, unreadable, mismatched", MtlsLive,
            ["Rabenhof", "Zirkuswagen", "Burgcloud", MtlsCertificateService.SharedClientCommonName]);
        yield return new("mTLS: everything narrowed, operator host, invalid id", MtlsNarrowed,
            ["Rabenhof", "Handmade", "bad id", "whiskers-client-rabenhof-g1", MtlsCertificateService.SharedClientCommonName]);
        yield return new("mTLS: unfinished switch, unreadable hosts", MtlsUnfinished,
            ["Rabenhof", "Zirkuswagen", "Burgcloud", "whiskers-client-zirkuswagen-g1", "whiskers-client-burgcloud-g1"]);
        yield return new("guardrails: fail-closed, record missing, unconfirmed, recorded", Guardrails,
            ["guardrails.json: unexpected token", "table missing"]);
    }

    /// <summary>Contract 0010's line in each of its four states (fail-closed, record not written, unconfirmed, recorded).</summary>
    private static Task<IReadOnlyList<ReadinessCheck>> Guardrails(IStringLocalizer<SharedResource> text)
        => Task.FromResult<IReadOnlyList<ReadinessCheck>>(
        [
            ProductionReadinessService.GuardrailPolicyCheck(new GuardrailState(loadError: "guardrails.json: unexpected token"), text),
            ProductionReadinessService.GuardrailPolicyCheck(new GuardrailState(markerProblem: "table missing"), text),
            ProductionReadinessService.GuardrailPolicyCheck(new GuardrailState(), text),
            ProductionReadinessService.GuardrailPolicyCheck(new GuardrailState(markerRecorded: true), text),
        ]);

    private sealed class GuardrailState(string? loadError = null, string? markerProblem = null, bool markerRecorded = false)
        : Whiskers.Services.Guardrails.IGuardrailStore
    {
        public GuardrailPolicy Current { get; } = new();
        public GuardrailConfig Config { get; } = new();
        string? Whiskers.Services.Guardrails.IGuardrailStore.LoadError => loadError;
        string? Whiskers.Services.Guardrails.IGuardrailStore.MarkerProblem => markerProblem;
        bool Whiskers.Services.Guardrails.IGuardrailStore.MarkerRecorded => markerRecorded;
        public Task SaveConfigAsync(GuardrailConfig config, Whiskers.Models.Agent.AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(GuardrailPolicy p, Whiskers.Models.Agent.AgentPrincipal editor, CancellationToken ct = default) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var s in Scenarios()) data.Add(s.Name);
        return data;
    }

    private static Scenario Named(string name) => Scenarios().Single(s => s.Name == name);

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task A_scenario_emits_only_resource_keys_and_separators(string name)
    {
        var echo = new ReadinessTexts.KeyEcho();
        var checks = await Named(name).Run(echo);

        Assert.NotEmpty(checks);
        foreach (var check in checks)
        foreach (var (field, text) in new[] { ("Title", check.Title), ("Detail", check.Detail), ("LinkText", check.LinkText ?? "") })
        {
            var rest = Regex.Replace(text, @"⟦Readiness_[A-Za-z]+_[A-Za-z]+⟧", "");
            Assert.True(Regex.IsMatch(rest, "^[ ,]*$"), $"{check.Id}.{field} holds text that is no Readiness_* key: '{text}'");
        }
        Assert.All(echo.Asked, k => Assert.StartsWith("Readiness_", k));
    }

    [Fact]
    public async Task Every_readiness_key_is_emitted_by_some_scenario()
    {
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scenario in Scenarios())
        {
            var echo = new ReadinessTexts.KeyEcho();
            await scenario.Run(echo);
            asked.UnionWith(echo.Asked);
        }
        var keys = ReadinessKeys(English.Value).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(asked.Except(keys).Order());                            // asked for a key the resx lacks
        Assert.Empty(keys.Except(asked).Except(OtherOsKeys).Order());        // an unused key or an untested branch
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task A_scenario_says_the_same_in_english_and_german(string name)
    {
        var scenario = Named(name);
        var en = await scenario.Run(ReadinessTexts.English);
        var de = await scenario.Run(ReadinessTexts.German);

        Assert.Equal(en.Select(c => c.Id), de.Select(c => c.Id));
        Assert.Equal(en.Select(c => c.Ok), de.Select(c => c.Ok));
        Assert.Equal(en.Select(c => c.LinkHref), de.Select(c => c.LinkHref));
        foreach (var value in scenario.Data)
        {
            Assert.Contains(en, c => (c.Title + " " + c.Detail).Contains(value, StringComparison.Ordinal));
            Assert.Contains(de, c => (c.Title + " " + c.Detail).Contains(value, StringComparison.Ordinal));
        }
        // No line came out as a key name (a key missing in one language falls back to its name).
        Assert.DoesNotContain(en.Concat(de), c => (c.Title + c.Detail + c.LinkText).Contains("Readiness_", StringComparison.Ordinal));
        Assert.NotEqual(en.Select(c => c.Title), de.Select(c => c.Title));
    }

    [Fact]
    public async Task An_unsupported_culture_gets_english()
    {
        var scenario = Named("plaintext everywhere, vault on");
        var en = await scenario.Run(ReadinessTexts.English);
        var fr = await scenario.Run(ReadinessTexts.In("fr"));

        Assert.Equal(en.Select(c => (c.Id, c.Title, c.Ok, c.LinkText)), fr.Select(c => (c.Id, c.Title, c.Ok, c.LinkText)));
        Assert.Equal(English.Value["Readiness_vault_Title"], fr.Single(c => c.Id == Ids.Vault).Title);
    }

    [Fact]
    public async Task Without_a_localizer_the_service_speaks_the_resources_not_key_names()
    {
        using var w = new ReadinessWorld();
        var checks = await new ProductionReadinessService(w.Config([]), await w.Vault(null), w.Scopes, await w.Servers())
            { RecoveredStores = () => [] }.GetChecksAsync(isHttps: true);
        Assert.DoesNotContain(checks, c => c.Title.StartsWith("Readiness_", StringComparison.Ordinal));
    }

    // ---- the worlds ---------------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<ReadinessCheck>> General(IStringLocalizer<SharedResource> text,
        Dictionary<string, string?>? config = null, bool isHttps = true, bool privileged = false, string? ownLabel = null,
        string? vaultKey = null, bool wrongKey = false, bool cloudKey = false, bool dnsToken = false, bool plaintextSetting = false,
        bool webhook = false, bool scheduled = false, string? recovered = null, bool kept = false, string? pgVersion = null,
        string? untrustedPeer = null)
    {
        using var w = new ReadinessWorld();
        config ??= [];
        if (pgVersion is not null) config["WHISKERS_DB_PROVIDER"] = "postgres";
        if (vaultKey is not null) config["VAULT_KEY"] = vaultKey;
        if (cloudKey)
            File.WriteAllText(w.Paths.ServersJson, new JsonObject
            {
                ["Servers"] = new JsonArray(new JsonObject
                {
                    ["Id"] = "hetz", ["Name"] = "Hetzner box", ["ConnectionType"] = 0, ["Enabled"] = true, ["IsDefault"] = true,
                    ["CloudProvider"] = (int)CloudProvider.Hetzner, ["CloudApiKey"] = "CLOUDKEY-" + Guid.NewGuid().ToString("N"),
                }),
            }.ToJsonString());
        var settings = new JsonObject();
        if (dnsToken) settings["Dns"] = new JsonObject { ["Provider"] = "infomaniak", ["ApiToken"] = "DNSTOKEN-x" };
        if (plaintextSetting) settings["Email"] = new JsonObject { ["Password"] = "SMTP-x" };
        if (settings.Count > 0) File.WriteAllText(w.Paths.AppSettingsJson, settings.ToJsonString());
        if (wrongKey)
        {
            var first = await w.Vault(vaultKey);
            await first.SetSecretAsync("x", "y");
            config["VAULT_KEY"] = vaultKey + "-but-another";
        }
        using (var scope = w.Scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            if (webhook) db.Webhooks.Add(new WebhookEntity { Name = "legacy", Secret = "WH-x", TargetId = "/x" });
            if (scheduled)
            {
                db.ScheduledTasks.Add(new ScheduledTaskEntity { Name = "self", TaskType = ScheduledTaskType.SelfBackup, CronExpression = "0 2 * * *" });
                db.UpdatePolicies.Add(new UpdatePolicyEntity { ContainerId = "c1", ContainerName = "web", ServerId = "local", AutoUpdate = true });
            }
            await db.SaveChangesAsync();
        }
        if (kept) await File.WriteAllTextAsync(w.Paths.ServersJson + ".corrupt-20261101000000", "{torn");

        var vault = await w.Vault(config.GetValueOrDefault("VAULT_KEY"));
        var servers = await w.Servers();
        var observer = new Whiskers.Utils.ForwardedHeadersObserver();
        if (untrustedPeer is not null) observer.Untrusted(System.Net.IPAddress.Parse(untrustedPeer));
        FakeDocker? docker = null;
        if (ownLabel is not null)
        {
            var self = Whiskers.Services.LogMonitor.LogMonitorService.ResolveSelfServerIds(servers).Single();
            docker = new FakeDocker(new ContainerInfo
            {
                Id = "whiskers-id", Name = "whiskers", ServerId = self, Labels = new() { [ownLabel] = "true" },
            });
        }
        var service = new ProductionReadinessService(w.Config(config), vault, w.Scopes, servers,
            new AppSettingsStore(dataPaths: w.Paths), dataPaths: w.Paths, forwarded: observer, docker: docker,
            selfContainerNames: new HashSet<string>(["whiskers"], StringComparer.OrdinalIgnoreCase), text: text)
        {
            RecoveredStores = () => recovered is null ? [] : [new JsonStoreRecoveryEvent(Path.Combine(w.Paths.RootDir, recovered), "x", DateTime.UtcNow)],
            IsPrivileged = () => privileged,
            PgDumpVersion = pgVersion is null ? null
                : pgVersion.StartsWith('!') ? () => throw new Whiskers.Services.Backup.PostgresToolException(pgVersion[1..])
                : () => Task.FromResult(pgVersion),
        };
        return await service.GetChecksAsync(isHttps);
    }

    private static ProductionReadinessService Mtls(CredentialWorld w, IStringLocalizer<SharedResource> text)
        => new(null!, null!, null!, w.Servers, clientCredentials: w.Store, text: text);

    /// <summary>One host migrated (own identity with an expiry), one on the shared identity, the shared key left behind,
    /// and one key file readable by everybody.</summary>
    private static async Task<IReadOnlyList<ReadinessCheck>> MtlsMigrated(IStringLocalizer<SharedResource> text)
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        ProtectKeys(w.Root);
        var checks = Mtls(w, text).MtlsClientCredentialChecks().ToList();
        Expose(w.Store.SharedPaths.Key);
        checks.AddRange(Mtls(w, text).MtlsClientCredentialChecks().Where(c => c.Id == Ids.MtlsKeysProtected));
        return checks;
    }

    /// <summary>The live lines: all fine first, then one host stopped, one unreadable and a narrowed host running a
    /// wider list.</summary>
    private static async Task<IReadOnlyList<ReadinessCheck>> MtlsLive(IStringLocalizer<SharedResource> text)
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"), ("burgcloud", "100.64.0.11"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var checks = (await Mtls(w, text).LiveAllowListChecksAsync(w.Service.ReadLiveAllowListAsync)).ToList();
        w.Host("zirkuswagen").GhostunnelStopped = true;
        w.Host("burgcloud").Unreachable = true;
        w.Host("rabenhof").RunningAllow = [MtlsCertificateService.SharedClientCommonName, "whiskers-client-rabenhof-g1"];
        checks.AddRange(await Mtls(w, text).LiveAllowListChecksAsync(w.Service.ReadLiveAllowListAsync));
        return checks;
    }

    /// <summary>Every managed host narrowed (no host admits the shared name), one operator-supplied host, one record
    /// whose id cannot be a directory name, and a narrowed host whose certificate cannot be read (no expiry).</summary>
    private static async Task<IReadOnlyList<ReadinessCheck>> MtlsNarrowed(IStringLocalizer<SharedResource> text)
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("handmade", "100.64.0.12"));
        Assert.True((await w.Service.MigrateClientAsync("rabenhof", null, default)).Ok);
        var own = Path.Combine(w.Root, "operator");
        Directory.CreateDirectory(own);
        File.Copy(w.Store.SharedPaths.Cert, Path.Combine(own, "client.crt"));
        File.Copy(w.Store.SharedPaths.Key, Path.Combine(own, "client.key"));
        var record = w.Server("handmade").Clone();
        record.TcpClientCertPath = Path.Combine(own, "client.crt");
        record.TcpClientKeyPath = Path.Combine(own, "client.key");
        await w.Servers.UpdateServerAsync(record);
        w.Host("handmade").RunningAllow = ["operator-client"];
        var checks = (await Mtls(w, text).MtlsChecksAsync(w.Service.ReadLiveAllowListAsync)).ToList();

        await w.Servers.AddServerAsync(new ServerConfig
        {
            Id = "bad id", Name = "bad id", ConnectionType = ConnectionType.TCP, TcpUseTls = true,
            TcpClientCertPath = w.Store.SharedPaths.Cert, TcpClientKeyPath = w.Store.SharedPaths.Key,
        });
        File.WriteAllText(w.Server("rabenhof").TcpClientCertPath!, "not a certificate");
        checks.AddRange(Mtls(w, text).MtlsClientCredentialChecks().Where(c => c.Id == Ids.MtlsIdentity));
        return checks;
    }

    /// <summary>A switch that stopped at phase 3, and the records of hosts whose live arguments cannot be read — one on
    /// the shared identity, one on its own.</summary>
    private static async Task<IReadOnlyList<ReadinessCheck>> MtlsUnfinished(IStringLocalizer<SharedResource> text)
    {
        using var w = new CredentialWorld(true, ("rabenhof", "100.64.0.9"), ("zirkuswagen", "100.64.0.10"), ("burgcloud", "100.64.0.11"));
        Assert.True((await w.Service.MigrateClientAsync("zirkuswagen", null, default)).Ok);
        Assert.True((await w.Service.MigrateClientAsync("burgcloud", null, default)).Ok);
        await w.Store.WriteJournalAsync("zirkuswagen", w.Store.ReadJournal("zirkuswagen")! with { Phase = 3 }, default);
        w.Host("rabenhof").Unreachable = true;
        w.Host("burgcloud").Unreachable = true;
        return await Mtls(w, text).MtlsChecksAsync(w.Service.ReadLiveAllowListAsync);
    }

    /// <summary>Gives every key file under <paramref name="root"/> mode 0600 on Unix, as the store writes them. The
    /// fixture writes the shared key with the process umask (0644 on a CI runner), which reads as exposed. On Windows
    /// the files inherit the profile's private ACL already.</summary>
    private static void ProtectKeys(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var key in Directory.EnumerateFiles(root, "*.key", SearchOption.AllDirectories))
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Makes a key file readable by everybody: mode 0644 on Unix, a read grant to Everyone on Windows.</summary>
    private static void Expose(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            return;
        }
        var info = new FileInfo(path);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    /// <summary>A data directory with an SQLite database, the vault and the server store.</summary>
    internal sealed class ReadinessWorld : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "ready-l10n-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _sp;
        public DataPathOptions Paths { get; }
        public IServiceScopeFactory Scopes => _sp.GetRequiredService<IServiceScopeFactory>();

        public ReadinessWorld()
        {
            Directory.CreateDirectory(_root);
            Paths = new DataPathOptions(_root);
            var services = new ServiceCollection();
            services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={Paths.DbPath}"));
            _sp = services.BuildServiceProvider();
            using var scope = _sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        }

        public IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        public async Task<VaultService> Vault(string? key)
        {
            var vault = new VaultService(Config(key is null ? [] : new() { ["VAULT_KEY"] = key }), NullLogger<VaultService>.Instance, dataPaths: Paths);
            await vault.InitializeAsync();
            return vault;
        }

        public async Task<ServerConfigService> Servers()
        {
            var servers = new ServerConfigService(Options.Create(new DockerSettings { SocketPath = "npipe://test" }),
                NullLogger<ServerConfigService>.Instance, dataPaths: Paths);
            await servers.InitializeAsync();
            return servers;
        }

        public void Dispose()
        {
            _sp.Dispose();
            TestSqlite.ClearPools(_root);
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Services.Cloud;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Setup;

/// <summary>One checklist item on the "production ready?" card (W3): a pass/fail state, a short
/// explanation and where to fix it. Purely informational — nothing here gates any behaviour.</summary>
/// <param name="Id">Stable, untranslated line id (contract 0011, D1), e.g. <c>vault</c> or <c>mtls-identity</c>. Tests
/// and the claim register refer to a line by its id, never by its title.</param>
public sealed record ReadinessCheck(string Id, string Title, bool Ok, string Detail, string? LinkHref, string? LinkText);

/// <summary>Computes the static "production ready?" checklist shown in Settings (outOfTheBox W3.4).
/// Cheap, read-only checks against config + stores; deliberately NOT a framework.</summary>
public interface IProductionReadinessService
{
    /// <param name="isHttps">Whether the caller's page was delivered over HTTPS — passed in by the
    /// UI (from NavigationManager) because IHttpContextAccessor is unreliable inside a Blazor
    /// Server circuit.</param>
    Task<IReadOnlyList<ReadinessCheck>> GetChecksAsync(bool isHttps);
}

/// <remarks>
/// Contract 0011 (INV-56): every title, detail and link text comes from the <c>Readiness_*</c> keys of
/// <c>SharedResource.resx</c> (English) and <c>SharedResource.de.resx</c> (German), resolved at call time against the
/// caller's UI culture; nothing localized is cached in this singleton. Values inserted into the texts are data and stay
/// untranslated (counts, names, setting names, versions); dates use the invariant <c>yyyy-MM-dd</c> format.
/// </remarks>
public sealed class ProductionReadinessService : IProductionReadinessService
{
    /// <summary>The line ids (contract 0011, D1).</summary>
    public static class Ids
    {
        public const string Auth = "auth";
        public const string SelfExposure = "self-exposure";
        public const string Vault = "vault";
        public const string GuardrailPolicy = "guardrail-policy";
        public const string CloudDns = "cloud-dns";
        public const string NotifyAi = "notify-ai";
        public const string StoredEncrypted = "stored-encrypted";
        public const string SelfBackup = "self-backup";
        public const string PgTools = "pg-tools";
        public const string UpdatePolicy = "update-policy";
        public const string StoresReadable = "stores-readable";
        public const string NonRoot = "non-root";
        public const string Https = "https";
        public const string OidcHttps = "oidc-https";
        public const string ProxyTrusted = "proxy-trusted";
        public const string MtlsProxy = "mtls-proxy";
        public const string MtlsSharedLive = "mtls-shared-live";
        public const string MtlsAllowCn = "mtls-allow-cn";
        public const string MtlsIdentity = "mtls-identity";
        public const string MtlsSharedKey = "mtls-shared-key";
        public const string MtlsKeysProtected = "mtls-keys-protected";
    }

    private static string? _pgDumpVersion;

    private static readonly Lazy<IStringLocalizer<SharedResource>> ResourceText = new(() =>
        new StringLocalizer<SharedResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions { ResourcesPath = "Resources" }), NullLoggerFactory.Instance)));

    /// <summary>A localizer over the real resources, for callers without one (they get English, not key names).</summary>
    internal static IStringLocalizer<SharedResource> DefaultText => ResourceText.Value;

    private readonly IConfiguration _config;
    private readonly IVaultService _vault;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerConfigService? _servers;
    private readonly IAppSettingsStore? _appSettings;
    private readonly Whiskers.Services.Agent.IAgentSettingsStore? _agentSettings;
    private readonly VaultKeyRingRepository? _keyRing;
    private readonly Whiskers.Configuration.DataPathOptions? _dataPaths;
    private readonly Whiskers.Utils.ForwardedHeadersObserver? _forwarded;
    private readonly Whiskers.Services.Certificates.IMtlsClientCredentialStore? _clientCredentials;
    private readonly Whiskers.Services.Docker.IDockerService? _docker;
    private readonly Whiskers.Services.Guardrails.IGuardrailStore? _guardrails;
    private readonly IReadOnlySet<string> _selfContainerNames;
    private readonly IStringLocalizer<SharedResource> _text;

    public ProductionReadinessService(IConfiguration config, IVaultService vault, IServiceScopeFactory scopeFactory,
        IServerConfigService? servers = null, IAppSettingsStore? appSettings = null,
        Whiskers.Services.Agent.IAgentSettingsStore? agentSettings = null, Whiskers.Configuration.DataPathOptions? dataPaths = null,
        VaultKeyRingRepository? keyRing = null, Whiskers.Utils.ForwardedHeadersObserver? forwarded = null,
        Whiskers.Services.Certificates.IMtlsClientCredentialStore? clientCredentials = null,
        Whiskers.Services.Docker.IDockerService? docker = null, Whiskers.Services.Guardrails.IGuardrailStore? guardrails = null,
        IReadOnlySet<string>? selfContainerNames = null, IStringLocalizer<SharedResource>? text = null)
    {
        _clientCredentials = clientCredentials;
        _docker = docker;
        _guardrails = guardrails;
        _selfContainerNames = selfContainerNames ?? Whiskers.Services.LogMonitor.Hygiene.LogScanExclusions.ManualNamesFromEnvironment();
        _forwarded = forwarded;
        _keyRing = keyRing;
        _dataPaths = dataPaths;
        _config = config;
        _vault = vault;
        _scopeFactory = scopeFactory;
        _servers = servers;
        _appSettings = appSettings;
        _text = text ?? DefaultText;
        // The agent's file is read also while the agent module is off (its store is then not registered).
        _agentSettings = agentSettings ?? (dataPaths is null ? null : new Whiskers.Services.Agent.AgentSettingsStore(dataPaths: dataPaths));
    }

    // ---- seams for tests (contract 0011, D6: every branch is reachable from a scenario) ----

    /// <summary>The installed <c>pg_dump</c>'s version; by default the real tool, found once per process.</summary>
    internal Func<Task<string>>? PgDumpVersion { get; init; }

    /// <summary>The JSON stores recovered since the start (R1-35); by default the process-wide record.</summary>
    internal Func<IReadOnlyList<JsonStoreRecoveryEvent>> RecoveredStores { get; init; } = () => JsonStoreRecovery.Recovered;

    /// <summary>Whether the process runs privileged (root); by default the real process state.</summary>
    internal Func<bool> IsPrivileged { get; init; } = () => Environment.IsPrivilegedProcess;

    /// <summary>One whole translated sentence (or title) for the caller's UI culture.</summary>
    private string T(string key, params object[] args) => _text[key, args].Value;

    /// <summary>Whole translated sentences joined with a space (contract 0011, D2: no sentence fragments).</summary>
    private static string Sentences(params string?[] sentences) => string.Join(" ", sentences.Where(s => !string.IsNullOrEmpty(s)));

    private static string List(IEnumerable<string> items) => string.Join(", ", items);

    public async Task<IReadOnlyList<ReadinessCheck>> GetChecksAsync(bool isHttps)
    {
        var checks = new List<ReadinessCheck>();

        // 1) Authentication — the single most important switch.
        var authDisabled = _config.GetValue<bool>("Auth:Disabled");
        checks.Add(new ReadinessCheck(Ids.Auth,
            T("Readiness_auth_Title"),
            !authDisabled,
            authDisabled ? T("Readiness_auth_Fail") : T("Readiness_auth_Ok"),
            null, null));
        // 1b) Contract 0006: with sign-in off, Whiskers' own container must not be published through a route label.
        if (authDisabled && await SelfExposureCheckAsync() is { } selfExposure)
            checks.Add(selfExposure);

        // 2) Vault — without VAULT_KEY stored secrets are unavailable/plaintext-blocked.
        checks.Add(new ReadinessCheck(Ids.Vault,
            T("Readiness_vault_Title"),
            _vault.IsEnabled,
            _vault.KeyState switch
            {
                // INV-56: says which credentials the vault holds — and which it does not hold yet.
                VaultKeyState.Verified => T("Readiness_vault_Ok"),
                VaultKeyState.Wrong => T("Readiness_vault_Wrong"),
                _ => T("Readiness_vault_Missing"),
            },
            null, null));

        // 2a) Contract 0010 (INV-25): the guardrail policy is enforced as stored, and the database records that it exists.
        if (_guardrails is not null)
            checks.Add(GuardrailPolicyCheck(_guardrails, _text));

        // 2b) R1-29: the cloud API keys and the DNS token as they are actually stored, not as the vault switch suggests.
        var plaintextCloud = _servers?.GetServers().Count(s => s.HasPlaintextCloudKey()) ?? 0;
        var plaintextDns = await HasPlaintextDnsTokenAsync();
        var credentialsInVault = plaintextCloud == 0 && !plaintextDns;
        checks.Add(new ReadinessCheck(Ids.CloudDns,
            T("Readiness_cloudDns_Title"),
            credentialsInVault,
            credentialsInVault
                ? T("Readiness_cloudDns_Ok")
                : Sentences(
                    plaintextDns ? T("Readiness_cloudDns_FailCloudDns", plaintextCloud) : T("Readiness_cloudDns_FailCloud", plaintextCloud),
                    _vault.IsEnabled ? T("Readiness_cloudDns_PendingVault") : T("Readiness_cloudDns_PendingNoVault")),
            null, null));

        // 2c) R1-30: the notification channels' and the AI clients' credentials, as they are actually stored.
        var plaintextSettings = await PlaintextSettingsAsync();
        checks.Add(new ReadinessCheck(Ids.NotifyAi,
            T("Readiness_notifyAi_Title"),
            plaintextSettings.Count == 0,
            plaintextSettings.Count == 0
                ? T("Readiness_notifyAi_Ok")
                : Sentences(
                    T("Readiness_notifyAi_Fail", List(plaintextSettings)),
                    _vault.IsEnabled ? T("Readiness_notifyAi_PendingVault") : T("Readiness_notifyAi_PendingNoVault")),
            null, null));

        // 2d) R1-31: the webhook secrets and rollback snapshots in the database and the sign-in keys in their files.
        int plaintextSecrets, plaintextSnapshots;
        using (var scope = _scopeFactory.CreateScope())
            (plaintextSecrets, plaintextSnapshots) = await ProtectedFieldMigration.CountPlaintextAsync(
                scope.ServiceProvider.GetRequiredService<MetricsDbContext>());
        var plaintextKeys = _keyRing?.CountPlaintextKeys() ?? 0;
        var storedEncrypted = plaintextSecrets + plaintextSnapshots + plaintextKeys == 0;
        checks.Add(new ReadinessCheck(Ids.StoredEncrypted,
            T("Readiness_storedEncrypted_Title"),
            storedEncrypted,
            storedEncrypted
                ? T("Readiness_storedEncrypted_Ok")
                : Sentences(
                    T("Readiness_storedEncrypted_Fail", plaintextSecrets, plaintextSnapshots, plaintextKeys),
                    _vault.IsEnabled ? T("Readiness_storedEncrypted_PendingVault") : T("Readiness_storedEncrypted_PendingNoVault")),
            null, null));

        // 3) Self-backup schedule (F3) + 4) update policy — one scope, two cheap queries.
        bool hasBackupTask, hasUpdatePolicy;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            hasBackupTask = await db.ScheduledTasks
                .AnyAsync(t => t.TaskType == ScheduledTaskType.SelfBackup && t.Enabled);
            hasUpdatePolicy = await db.UpdatePolicies.AnyAsync(p => p.AutoUpdate);
        }
        checks.Add(new ReadinessCheck(Ids.SelfBackup,
            T("Readiness_selfBackup_Title"),
            hasBackupTask,
            hasBackupTask ? T("Readiness_selfBackup_Ok") : T("Readiness_selfBackup_Fail"),
            "tasks", T("Readiness_selfBackup_Link")));
        // R1-33: on PostgreSQL a self-backup needs the client tools; without them every backup fails.
        if (Whiskers.Services.Persistence.DatabaseRegistration.Resolve(_config).Provider == Whiskers.Services.Backup.SelfBackupFormat.ProviderPostgres)
        {
            // The tools do not change while the process runs: a found version is kept instead of starting pg_dump at
            // every evaluation (a missing tool is looked for again, so installing it shows without a restart).
            string? version = PgDumpVersion is null ? _pgDumpVersion : null, problem = null;
            if (version is null)
            {
                try
                {
                    version = PgDumpVersion is { } probe
                        ? await probe()
                        : _pgDumpVersion = await Whiskers.Services.Backup.PostgresDumpTool.FromEnvironment().VersionAsync();
                }
                catch (Whiskers.Services.Backup.PostgresToolException ex) { problem = ex.Message; }
            }
            // Tools without the \restrict protection write backups that no restore accepts.
            var canRestore = version is not null && Whiskers.Services.Backup.PostgresDumpTool.CanRestore(version);
            checks.Add(new ReadinessCheck(Ids.PgTools,
                T("Readiness_pgTools_Title"),
                canRestore,
                canRestore
                    ? T("Readiness_pgTools_Ok", version!)
                    : version is not null
                        ? T("Readiness_pgTools_TooOld", version)
                        : T("Readiness_pgTools_Unusable", problem ?? ""),
                null, null));
        }

        checks.Add(new ReadinessCheck(Ids.UpdatePolicy,
            T("Readiness_updatePolicy_Title"),
            hasUpdatePolicy,
            hasUpdatePolicy ? T("Readiness_updatePolicy_Ok") : T("Readiness_updatePolicy_Fail"),
            "image-updates", T("Readiness_updatePolicy_Link")));

        // R1-35: a JSON store that could not be read since the start (recovered from its previous generation), and the
        // unreadable copies kept in the data directory — they outlast the restart that usually follows a power loss.
        var recovered = RecoveredStores();
        var kept = CorruptCopies();
        checks.Add(new ReadinessCheck(Ids.StoresReadable,
            T("Readiness_storesReadable_Title"),
            recovered.Count == 0 && kept.Count == 0,
            recovered.Count == 0 && kept.Count == 0
                ? T("Readiness_storesReadable_Ok")
                : Sentences(
                    recovered.Count == 0 ? null : T("Readiness_storesReadable_Recovered", List(recovered.Select(r => Path.GetFileName(r.Path)))),
                    kept.Count == 0 ? null : T("Readiness_storesReadable_Kept", List(kept))),
            null, null));

        // 5) Non-root / hardened profile. The hardened compose runs the container as uid 10001;
        // a privileged (root) process signals that the default profile is in use.
        var nonRoot = !IsPrivileged();
        checks.Add(new ReadinessCheck(Ids.NonRoot,
            T("Readiness_nonRoot_Title"),
            nonRoot,
            nonRoot ? T("Readiness_nonRoot_Ok") : T("Readiness_nonRoot_Fail"),
            null, null));

        // 6) HTTPS at the proxy — the flag comes from the caller's page URI (NavigationManager),
        // which reflects X-Forwarded-Proto through UseForwardedHeaders.
        checks.Add(new ReadinessCheck(Ids.Https,
            T("Readiness_https_Title"),
            isHttps,
            isHttps ? T("Readiness_https_Ok") : T("Readiness_https_Fail"),
            null, null));

        // R1-50 (A-14): OIDC discovery over plain HTTP lets a man in the middle swap the IdP's keys and endpoints.
        if (_config.GetValue<bool>("Oidc:Enabled") && !_config.GetValue("Oidc:RequireHttpsMetadata", true))
            checks.Add(new ReadinessCheck(Ids.OidcHttps, T("Readiness_oidcHttps_Title"), false, T("Readiness_oidcHttps_Fail"), null, null));

        // 7) R1-50: forwarded headers from a peer that is not a trusted proxy were ignored — either a reverse proxy that
        // still has to be named in ForwardedHeaders:TrustedNetworks (until then the client address, the login rate limit
        // and the audit see the proxy) or a client claiming another address.
        if (_forwarded?.RecentUntrustedPeers is { Count: > 0 } untrusted)
        {
            // Data, not text: the peer and the invariant UTC time (D3).
            var peers = List(untrusted.Select(u => string.Create(CultureInfo.InvariantCulture, $"{u.Peer} ({u.At:yyyy-MM-dd HH:mm} UTC)")));
            checks.Add(new ReadinessCheck(Ids.ProxyTrusted, T("Readiness_proxyTrusted_Title"), false,
                T("Readiness_proxyTrusted_Fail", peers), null, null));
        }

        // 8) R1-44 (INV-58): per-host mTLS client identities.
        using (var scope = _scopeFactory.CreateScope())
        {
            var certificates = scope.ServiceProvider.GetService<Whiskers.Services.Certificates.IMtlsCertificateService>();
            checks.AddRange(await MtlsChecksAsync(certificates is null ? null : certificates.ReadLiveAllowListAsync));
        }

        return checks;
    }

    /// <summary>
    /// Contract 0010 D3/D7: fail-closed with its reason, a record that could not be written, or both fine. The store's
    /// reason and problem are data and stay untranslated (contract 0011, D3).
    /// </summary>
    internal static ReadinessCheck GuardrailPolicyCheck(Whiskers.Services.Guardrails.IGuardrailStore guardrails,
        IStringLocalizer<SharedResource>? text = null)
    {
        text ??= DefaultText;
        string T(string key, params object[] args) => text[key, args].Value;
        var title = T("Readiness_guardrailPolicy_Title");
        if (guardrails.LoadError is { } reason)
            return new ReadinessCheck(Ids.GuardrailPolicy, title, false, T("Readiness_guardrailPolicy_FailClosed", reason),
                "/guardrails", T("Readiness_guardrailPolicy_Link"));
        if (guardrails.MarkerProblem is { } problem)
            return new ReadinessCheck(Ids.GuardrailPolicy, title, false, T("Readiness_guardrailPolicy_MarkerProblem", problem),
                "/guardrails", T("Readiness_guardrailPolicy_Link"));
        return guardrails.MarkerRecorded
            ? new ReadinessCheck(Ids.GuardrailPolicy, title, true, T("Readiness_guardrailPolicy_Ok"), null, null)
            : new ReadinessCheck(Ids.GuardrailPolicy, title, false, T("Readiness_guardrailPolicy_Unconfirmed"), null, null);
    }

    private static readonly TimeSpan SelfExposureReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Contract 0006 (synergy with H2), called only with <c>Auth:Disabled</c>: reads Whiskers' own container on its own
    /// host (<see cref="Whiskers.Services.LogMonitor.LogMonitorService.ResolveSelfServerIds"/>), found by the names in
    /// <c>WHISKERS_SELF_CONTAINERS</c> as <see cref="Whiskers.Services.LogMonitor.Hygiene.LogScanExclusions"/> does. A label
    /// with an exposure prefix is not OK and the line quotes only its key; a container that cannot be found or read is
    /// "unknown", not OK. No label: no line.
    /// </summary>
    internal async Task<ReadinessCheck?> SelfExposureCheckAsync()
    {
        ReadinessCheck Unknown() => new(Ids.SelfExposure, T("Readiness_selfExposure_Title"), false, T("Readiness_selfExposure_Unknown"), null, null);
        if (_docker is null || _servers is null) return Unknown();
        var prefixes = _guardrails?.Current.EffectiveExposureLabelPrefixes
                       ?? Whiskers.Configuration.GuardrailPolicy.DefaultExposureLabelPrefixes;
        var found = false;
        try
        {
            foreach (var serverId in Whiskers.Services.LogMonitor.LogMonitorService.ResolveSelfServerIds(_servers))
            {
                using var cts = new CancellationTokenSource(SelfExposureReadTimeout);
                var containers = await _docker.ListContainersAsync(all: true, serverId, cts.Token).WaitAsync(cts.Token);
                foreach (var container in containers.Where(c => _selfContainerNames.Contains(c.Name.TrimStart('/'))))
                {
                    found = true;
                    var label = container.Labels.Keys
                        .Where(k => prefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                        .Order(StringComparer.Ordinal).FirstOrDefault();
                    if (label is not null)
                        return new ReadinessCheck(Ids.SelfExposure, T("Readiness_selfExposure_Title"), false,
                            T("Readiness_selfExposure_Fail", label), null, null);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Unknown();
        }
        return found ? null : Unknown();
    }

    /// <summary>
    /// R1-44 (contract 0007): the per-host credential lines and the live lines together. The live arguments are read
    /// first so the per-host line of a host whose arguments cannot be read points to the break-glass re-onboarding
    /// instead of "Migrate" (review r2 A: a restored older self-backup brings back the record and the journal
    /// together, so only the live read shows that the host no longer admits what the record names).
    /// </summary>
    internal async Task<IReadOnlyList<ReadinessCheck>> MtlsChecksAsync(
        Func<string, CancellationToken, Task<Whiskers.Services.Certificates.MtlsLiveAllowList>>? read)
    {
        if (read is null) return MtlsClientCredentialChecks();
        var results = await ReadLiveAllowListsAsync(read);
        var unreadable = results.Where(r => !r.Live.Readable).Select(r => r.Server.Id).ToHashSet(StringComparer.Ordinal);
        var checks = new List<ReadinessCheck>(MtlsClientCredentialChecks(unreadable));
        checks.AddRange(LiveAllowListChecks(results));
        return checks;
    }

    /// <summary>
    /// R1-44 (contract 0007, review m-9 and amendment 8): what each mTLS host's ghostunnel actually runs with, read live
    /// (<c>docker inspect</c>, one bounded read per host, in parallel). One line names every host whose ghostunnel is not
    /// running or whose arguments cannot be read — the mTLS channel is then the broken management path and the way back is
    /// the break-glass re-onboarding over SSH; another names every narrowed host whose running <c>--allow-cn</c> list is
    /// not exactly its own name (a hand edit, a restored compose file).
    /// </summary>
    internal async Task<IReadOnlyList<ReadinessCheck>> LiveAllowListChecksAsync(
        Func<string, CancellationToken, Task<Whiskers.Services.Certificates.MtlsLiveAllowList>> read)
        => LiveAllowListChecks(await ReadLiveAllowListsAsync(read));

    private async Task<(Whiskers.Models.ServerConfig Server, Whiskers.Services.Certificates.MtlsLiveAllowList Live)[]> ReadLiveAllowListsAsync(
        Func<string, CancellationToken, Task<Whiskers.Services.Certificates.MtlsLiveAllowList>> read)
    {
        if (_clientCredentials is null || _servers is null) return [];
        var hosts = _servers.GetServers()
            .Where(s => s.TcpUseTls && !string.IsNullOrWhiteSpace(s.TcpHost) && !string.IsNullOrWhiteSpace(s.TcpClientCertPath))
            .ToList();
        if (hosts.Count == 0) return [];

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await Task.WhenAll(hosts.Select(async s =>
        {
            try { return (Server: s, Live: await read(s.Id, cts.Token)); }
            catch (Exception ex) { return (Server: s, Live: new Whiskers.Services.Certificates.MtlsLiveAllowList(false, false, [], ex.Message)); }
        }));
    }

    private List<ReadinessCheck> LiveAllowListChecks(
        IReadOnlyList<(Whiskers.Models.ServerConfig Server, Whiskers.Services.Certificates.MtlsLiveAllowList Live)> results)
    {
        var checks = new List<ReadinessCheck>();
        if (_clientCredentials is not { } store || results.Count == 0) return checks;

        var down = results.Where(r => !r.Live.Readable || !r.Live.Running)
            .Select(r => r.Live.Readable ? T("Readiness_mtlsProxy_HostStopped", r.Server.Name) : T("Readiness_mtlsProxy_HostUnreadable", r.Server.Name))
            .ToList();
        checks.Add(new ReadinessCheck(Ids.MtlsProxy, T("Readiness_mtlsProxy_Title"), down.Count == 0,
            down.Count == 0 ? T("Readiness_mtlsProxy_Ok", results.Count) : T("Readiness_mtlsProxy_Fail", List(down)),
            down.Count == 0 ? null : "/servers", down.Count == 0 ? null : T("Readiness_mtlsProxy_Link")));

        // Every readable host whose running ghostunnel still admits the shared name, whatever its record uses —
        // operator-supplied credentials included (contract 0007, amendment 3 m-9; review r1 M1): a stolen shared key opens it.
        var sharedCn = Whiskers.Services.Certificates.MtlsCertificateService.CommonNameOf(store.SharedPaths.Cert)
                       ?? Whiskers.Services.Certificates.MtlsCertificateService.SharedClientCommonName;
        var admitShared = results.Where(r => r.Live is { Readable: true, Running: true } && r.Live.AllowedCommonNames.Contains(sharedCn))
            .Select(r => r.Server.Name).ToList();
        checks.Add(new ReadinessCheck(Ids.MtlsSharedLive, T("Readiness_mtlsSharedLive_Title"), admitShared.Count == 0,
            admitShared.Count == 0 ? T("Readiness_mtlsSharedLive_Ok", sharedCn) : T("Readiness_mtlsSharedLive_Fail", sharedCn, List(admitShared)),
            admitShared.Count == 0 ? null : "/servers", admitShared.Count == 0 ? null : T("Readiness_mtlsSharedLive_Link")));

        var mismatched = new List<string>();
        foreach (var (server, live) in results.Where(r => r.Live is { Readable: true, Running: true }))
        {
            if (!Whiskers.Services.Certificates.MtlsClientCredentialStore.IsValidServerId(server.Id)) continue;
            var status = store.StatusOf(server);
            if (status.Kind != Whiskers.Services.Certificates.MtlsClientCredentialKind.Own || store.ReadJournal(server.Id) is not { IsNarrowed: true })
                continue;
            if (live.AllowedCommonNames.Count != 1 || live.AllowedCommonNames[0] != status.CommonName)
                mismatched.Add(T("Readiness_mtlsAllowCn_Host", server.Name, List(live.AllowedCommonNames)));
        }
        checks.Add(new ReadinessCheck(Ids.MtlsAllowCn, T("Readiness_mtlsAllowCn_Title"), mismatched.Count == 0,
            mismatched.Count == 0 ? T("Readiness_mtlsAllowCn_Ok") : T("Readiness_mtlsAllowCn_Fail", List(mismatched)),
            mismatched.Count == 0 ? null : "/servers", mismatched.Count == 0 ? null : T("Readiness_mtlsAllowCn_Link")));
        return checks;
    }

    /// <summary>
    /// R1-44 (contract 0007): one line per mTLS host about its client credential — a host on the shared credential and a
    /// migration that is not finished are not OK, and so is a host whose live ghostunnel arguments could not be read
    /// (<paramref name="unreadable"/>): after an older self-backup was restored, the record and the journal come back
    /// together and name a credential the host no longer admits, so the way back is the break-glass re-onboarding, not
    /// "Migrate" — plus the shared key still lying around while hosts no longer accept it, and the modes (Unix) or
    /// ACLs (Windows) of every client key file.
    /// </summary>
    internal IReadOnlyList<ReadinessCheck> MtlsClientCredentialChecks(IReadOnlySet<string>? unreadable = null)
    {
        var checks = new List<ReadinessCheck>();
        if (_clientCredentials is not { } store || _servers is null) return checks;

        var narrowedHosts = 0;
        foreach (var server in _servers.GetServers().Where(s => s.TcpUseTls && !string.IsNullOrWhiteSpace(s.TcpClientCertPath)))
        {
            var title = T("Readiness_mtlsIdentity_Title", server.Name);
            if (!Whiskers.Services.Certificates.MtlsClientCredentialStore.IsValidServerId(server.Id))
            {
                checks.Add(new ReadinessCheck(Ids.MtlsIdentity, title, false, T("Readiness_mtlsIdentity_InvalidId", server.Id), null, null));
                continue;
            }
            var status = store.StatusOf(server);
            var journal = store.ReadJournal(server.Id);
            switch (status.Kind)
            {
                case Whiskers.Services.Certificates.MtlsClientCredentialKind.Shared or Whiskers.Services.Certificates.MtlsClientCredentialKind.Own
                    when unreadable?.Contains(server.Id) == true:
                    // E.g. an older self-backup restored after a migration or rotation (review r2 A): the record and the
                    // journal name a credential the host no longer admits, so "Migrate" cannot reach it either.
                    if (journal is { IsNarrowed: true }) narrowedHosts++;
                    checks.Add(new ReadinessCheck(Ids.MtlsIdentity, title, false,
                        status.Kind == Whiskers.Services.Certificates.MtlsClientCredentialKind.Shared
                            ? T("Readiness_mtlsIdentity_UnreadableShared")
                            : T("Readiness_mtlsIdentity_UnreadableOwn", status.CommonName ?? "", status.Generation),
                        "/servers", T("Readiness_mtlsIdentity_Link")));
                    break;
                case Whiskers.Services.Certificates.MtlsClientCredentialKind.Shared:
                    checks.Add(new ReadinessCheck(Ids.MtlsIdentity, title, false, T("Readiness_mtlsIdentity_Shared"),
                        "/servers", T("Readiness_mtlsIdentity_Link")));
                    break;
                case Whiskers.Services.Certificates.MtlsClientCredentialKind.Own when journal is { IsNarrowed: true }:
                    narrowedHosts++;
                    checks.Add(new ReadinessCheck(Ids.MtlsIdentity, title, true,
                        status.NotAfterUtc is { } until
                            ? T("Readiness_mtlsIdentity_OwnUntil", status.CommonName ?? "", status.Generation,
                                until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                            : T("Readiness_mtlsIdentity_Own", status.CommonName ?? "", status.Generation),
                        null, null));
                    break;
                case Whiskers.Services.Certificates.MtlsClientCredentialKind.Own:
                    checks.Add(new ReadinessCheck(Ids.MtlsIdentity, title, false,
                        T("Readiness_mtlsIdentity_Unfinished", status.CommonName ?? "", journal?.Phase ?? 0),
                        "/servers", T("Readiness_mtlsIdentity_Link")));
                    break;
                default:
                    checks.Add(new ReadinessCheck(Ids.MtlsIdentity, title, true, T("Readiness_mtlsIdentity_Operator"), null, null));
                    break;
            }
        }

        var shared = store.SharedPaths;
        if (File.Exists(shared.Key) && narrowedHosts > 0)
            checks.Add(new ReadinessCheck(Ids.MtlsSharedKey, T("Readiness_mtlsSharedKey_Title"), false,
                T("Readiness_mtlsSharedKey_Fail", narrowedHosts), "/servers", T("Readiness_mtlsSharedKey_Link")));

        var keys = store.KeyFiles();
        if (keys.Count > 0)
        {
            var exposed = keys.Where(KeyIsExposed).Select(k => Path.GetRelativePath(Path.GetDirectoryName(shared.Key)!, k).Replace('\\', '/')).ToList();
            checks.Add(new ReadinessCheck(Ids.MtlsKeysProtected, T("Readiness_mtlsKeysProtected_Title"), exposed.Count == 0,
                exposed.Count == 0
                    ? (OperatingSystem.IsWindows() ? T("Readiness_mtlsKeysProtected_OkWindows") : T("Readiness_mtlsKeysProtected_OkUnix"))
                    : (OperatingSystem.IsWindows()
                        ? T("Readiness_mtlsKeysProtected_FailWindows", List(exposed))
                        : T("Readiness_mtlsKeysProtected_FailUnix", List(exposed))),
                null, null));
        }
        return checks;
    }

    /// <summary>Unix: anything but 0600. Windows: a read grant to Everyone, Users or Authenticated Users.</summary>
    internal static bool KeyIsExposed(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                return File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return WindowsKeyIsExposed(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool WindowsKeyIsExposed(string path)
    {
        var broad = new[]
        {
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
        };
        var rules = new FileInfo(path).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in rules)
            if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                && (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.ReadData) != 0
                && broad.Contains(rule.IdentityReference))
                return true;
        return false;
    }

    /// <summary>The unreadable store copies (<c>*.corrupt-*</c>, R1-35; the guardrails' <c>*.unreadable-*</c>) in the
    /// data directory, relative to it.</summary>
    private List<string> CorruptCopies()
    {
        var root = _dataPaths?.RootDir;
        if (root is null || !Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).Contains(".corrupt-", StringComparison.Ordinal)
                            || Path.GetFileName(f).Contains(".unreadable-", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith("backups/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The vault-backed settings (R1-30) still stored in plaintext in app-settings.json or agent-settings.json, by
    /// name (<c>Section:Property</c>) — the files, not the merged configuration.</summary>
    private async Task<List<string>> PlaintextSettingsAsync()
    {
        var found = new List<string>();
        foreach (var (section, properties) in VaultBackedSettings.Fields)
        {
            var stored = VaultBackedSettings.IsAgent(section)
                ? _agentSettings is null ? null : await _agentSettings.ReadSectionAsync()
                : _appSettings is null ? null : await _appSettings.ReadSectionAsync(section);
            // Also a value next to a vault reference (hand-edited): it is at rest in plaintext all the same.
            found.AddRange(properties.Where(p => VaultBackedSettings.HasPlaintext(stored, p)).Select(p => $"{section}:{p}"));
        }
        return found;
    }

    /// <summary>Whether app-settings.json holds a DNS token in plaintext (the file, not the merged configuration: a
    /// token set by the environment is not stored at rest by Whiskers).</summary>
    private async Task<bool> HasPlaintextDnsTokenAsync()
    {
        if (_appSettings is null) return false;
        var section = await _appSettings.ReadSectionAsync(Whiskers.Configuration.DnsSettings.SectionName);
        var token = section?.FirstOrDefault(p => string.Equals(p.Key, nameof(Whiskers.Configuration.DnsSettings.ApiToken), StringComparison.OrdinalIgnoreCase)).Value;
        return token is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text);
    }
}

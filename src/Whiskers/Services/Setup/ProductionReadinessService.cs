using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;
using Whiskers.Services.Cloud;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;

namespace Whiskers.Services.Setup;

/// <summary>One checklist item on the "production ready?" card (W3): a pass/fail state, a short
/// explanation and where to fix it. Purely informational — nothing here gates any behaviour.</summary>
public sealed record ReadinessCheck(string Title, bool Ok, string Detail, string? LinkHref, string? LinkText);

/// <summary>Computes the static "Produktionsreif?" checklist shown in Settings (outOfTheBox W3.4).
/// Cheap, read-only checks against config + stores; deliberately NOT a framework.</summary>
public interface IProductionReadinessService
{
    /// <param name="isHttps">Whether the caller's page was delivered over HTTPS — passed in by the
    /// UI (from NavigationManager) because IHttpContextAccessor is unreliable inside a Blazor
    /// Server circuit.</param>
    Task<IReadOnlyList<ReadinessCheck>> GetChecksAsync(bool isHttps);
}

public sealed class ProductionReadinessService : IProductionReadinessService
{
    private static string? _pgDumpVersion;

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

    public ProductionReadinessService(IConfiguration config, IVaultService vault, IServiceScopeFactory scopeFactory,
        IServerConfigService? servers = null, IAppSettingsStore? appSettings = null,
        Whiskers.Services.Agent.IAgentSettingsStore? agentSettings = null, Whiskers.Configuration.DataPathOptions? dataPaths = null,
        VaultKeyRingRepository? keyRing = null, Whiskers.Utils.ForwardedHeadersObserver? forwarded = null,
        Whiskers.Services.Certificates.IMtlsClientCredentialStore? clientCredentials = null)
    {
        _clientCredentials = clientCredentials;
        _forwarded = forwarded;
        _keyRing = keyRing;
        _dataPaths = dataPaths;
        _config = config;
        _vault = vault;
        _scopeFactory = scopeFactory;
        _servers = servers;
        _appSettings = appSettings;
        // The agent's file is read also while the agent module is off (its store is then not registered).
        _agentSettings = agentSettings ?? (dataPaths is null ? null : new Whiskers.Services.Agent.AgentSettingsStore(dataPaths: dataPaths));
    }

    public async Task<IReadOnlyList<ReadinessCheck>> GetChecksAsync(bool isHttps)
    {
        var checks = new List<ReadinessCheck>();

        // 1) Authentication — the single most important switch.
        var authDisabled = _config.GetValue<bool>("Auth:Disabled");
        checks.Add(new ReadinessCheck(
            "Authentifizierung aktiv",
            !authDisabled,
            authDisabled
                ? "AUTH_DISABLED=true — jeder mit Netzwerkzugriff ist Admin. Nur für isolierte LAN-Setups vertretbar."
                : "Login ist erforderlich (lokal, Google oder OIDC).",
            null, null));

        // 2) Vault — without VAULT_KEY stored secrets are unavailable/plaintext-blocked.
        checks.Add(new ReadinessCheck(
            "Secret-Vault aktiv (VAULT_KEY)",
            _vault.IsEnabled,
            _vault.KeyState switch
            {
                // INV-56: says which credentials the vault holds — and which it does not hold yet.
                VaultKeyState.Verified => "Der Vault ist aktiv; Backups sind verschlüsselbar. In den Vault gehören Cloud-API-Keys, der "
                                          + "DNS-Token, die Zugangsdaten der Benachrichtigungskanäle und der KI (wo sie stehen, sagen die "
                                          + "nächsten Punkte), kubeconfigs sowie Registry- und Git-Zugangsdaten; mit einem daraus "
                                          + "abgeleiteten Schlüssel verschlüsselt sind die Secrets eingehender Webhooks, die Rollback-Snapshots "
                                          + "und die Sitzungsschlüssel. Noch unverschlüsselt gespeichert: SSH-Schlüssel und der "
                                          + "mTLS-Client-Schlüssel im Datenverzeichnis.",
                VaultKeyState.Wrong => "VAULT_KEY passt nicht zu diesem Vault — Secrets sind gesperrt, vault.json bleibt unverändert. Den richtigen Schlüssel setzen und neu starten.",
                _ => "Kein VAULT_KEY gesetzt — Vault und verschlüsselte Backups sind deaktiviert. Key setzen und sicher ablegen.",
            },
            null, null));

        // 2b) R1-29: the cloud API keys and the DNS token as they are actually stored, not as the vault switch suggests.
        var plaintextCloud = _servers?.GetServers().Count(s => s.HasPlaintextCloudKey()) ?? 0;
        var plaintextDns = await HasPlaintextDnsTokenAsync();
        var credentialsInVault = plaintextCloud == 0 && !plaintextDns;
        checks.Add(new ReadinessCheck(
            "Cloud- und DNS-Zugangsdaten im Vault",
            credentialsInVault,
            credentialsInVault
                ? "Keine Cloud-API-Keys und kein DNS-Token im Klartext: sie liegen verschlüsselt im Vault (oder sind nicht konfiguriert)."
                : $"Im Klartext gespeichert: {plaintextCloud} Cloud-API-Key(s) in servers.json"
                  + (plaintextDns ? " und der DNS-Token in app-settings.json" : "") + ". "
                  + (_vault.IsEnabled
                      ? "Sie sind noch nicht in den Vault übernommen (die Übernahme läuft beim Start; scheitert sie, nennt das Log den Grund) — "
                        + "neu starten, oder den Server bzw. die DNS-Einstellungen einmal speichern: das legt den Wert unter einem neuen Vault-Schlüssel ab."
                      : "Sie werden beim ersten Start mit gültigem VAULT_KEY in den Vault übernommen."),
            null, null));

        // 2c) R1-30: the notification channels' and the AI clients' credentials, as they are actually stored.
        var plaintextSettings = await PlaintextSettingsAsync();
        checks.Add(new ReadinessCheck(
            "Benachrichtigungs- und KI-Zugangsdaten im Vault",
            plaintextSettings.Count == 0,
            plaintextSettings.Count == 0
                ? "Keine Zugangsdaten der Benachrichtigungskanäle, des KI-Chats oder des Agenten im Klartext: sie liegen verschlüsselt im Vault (oder sind nicht konfiguriert)."
                : $"Im Klartext gespeichert: {string.Join(", ", plaintextSettings)}. "
                  + (_vault.IsEnabled
                      ? "Sie sind noch nicht in den Vault übernommen (die Übernahme läuft beim Start; scheitert sie, nennt das Log den Grund) — "
                        + "neu starten, oder die Einstellungen einmal speichern: das legt die Werte unter neuen Vault-Schlüsseln ab. "
                        + "Ein von Hand neben einen Vault-Verweis geschriebener Wert wird beim Start nicht übernommen; nur ein Speichern entfernt ihn."
                      : "Sie werden beim ersten Start mit gültigem VAULT_KEY in den Vault übernommen."),
            null, null));

        // 2d) R1-31: the webhook secrets and rollback snapshots in the database and the sign-in keys in their files.
        int plaintextSecrets, plaintextSnapshots;
        using (var scope = _scopeFactory.CreateScope())
            (plaintextSecrets, plaintextSnapshots) = await ProtectedFieldMigration.CountPlaintextAsync(
                scope.ServiceProvider.GetRequiredService<MetricsDbContext>());
        var plaintextKeys = _keyRing?.CountPlaintextKeys() ?? 0;
        var storedEncrypted = plaintextSecrets + plaintextSnapshots + plaintextKeys == 0;
        checks.Add(new ReadinessCheck(
            "Webhook-Secrets, Rollback-Snapshots und Sitzungsschlüssel verschlüsselt",
            storedEncrypted,
            storedEncrypted
                ? "Keine Webhook-Secrets, Rollback-Snapshots (mit den Umgebungsvariablen der Container) oder Sitzungsschlüssel im Klartext."
                : $"Im Klartext gespeichert: {plaintextSecrets} Webhook-Secret(s) und {plaintextSnapshots} Rollback-Snapshot(s) in der "
                  + $"Datenbank, {plaintextKeys} Sitzungsschlüssel-Datei(en) im Datenverzeichnis. "
                  + (_vault.IsEnabled
                      ? "Sie werden beim Start verschlüsselt (scheitert das, nennt das Log den Grund) — neu starten."
                      : "Sie werden beim ersten Start mit gültigem VAULT_KEY verschlüsselt; bis dahin legt Whiskers keine neuen "
                        + "Webhook-Secrets und keine Rollback-Snapshots an."),
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
        checks.Add(new ReadinessCheck(
            "Self-Backup eingeplant",
            hasBackupTask,
            hasBackupTask
                ? "Ein geplanter Whiskers-Self-Backup-Task ist aktiv."
                : "Kein geplanter Self-Backup-Task — unter Aufgaben einen SelfBackup-Zeitplan anlegen (manuelle Backups: Settings → Backup & Restore).",
            "tasks", "Aufgaben öffnen"));
        // R1-33: on PostgreSQL a self-backup needs the client tools; without them every backup fails.
        if (Whiskers.Services.Persistence.DatabaseRegistration.Resolve(_config).Provider == Whiskers.Services.Backup.SelfBackupFormat.ProviderPostgres)
        {
            // The tools do not change while the process runs: a found version is kept instead of starting pg_dump at
            // every evaluation (a missing tool is looked for again, so installing it shows without a restart).
            string? version = _pgDumpVersion, problem = null;
            if (version is null)
            {
                try { version = _pgDumpVersion = await Whiskers.Services.Backup.PostgresDumpTool.FromEnvironment().VersionAsync(); }
                catch (Whiskers.Services.Backup.PostgresToolException ex) { problem = ex.Message; }
            }
            // Tools without the \restrict protection write backups that no restore accepts.
            var canRestore = version is not null && Whiskers.Services.Backup.PostgresDumpTool.CanRestore(version);
            checks.Add(new ReadinessCheck(
                "PostgreSQL-Client-Tools für das Self-Backup",
                canRestore,
                canRestore
                    ? $"{version} — Backups enthalten die Datenbank (pg_dump)."
                    : version is not null
                        ? $"{version} — zu alt für die Wiederherstellung: Client-Tools 17.6 oder neuer (bzw. 16.10, 15.14, 14.19) und mindestens so neu wie der Server nötig."
                        : $"pg_dump nicht nutzbar — Self-Backups schlagen fehl: {problem} (WHISKERS_PG_BIN_DIR auf das bin-Verzeichnis der Client-Tools setzen).",
                null, null));
        }

        checks.Add(new ReadinessCheck(
            "Update-Policy gesetzt",
            hasUpdatePolicy,
            hasUpdatePolicy
                ? "Mindestens ein Container hat eine Auto-Update-Policy (Rollback-Snapshot inklusive)."
                : "Keine Auto-Update-Policy aktiv — Updates bleiben vollständig manuell (bewusste Entscheidung ist auch okay).",
            "image-updates", "Image-Updates öffnen"));

        // R1-35: a JSON store that could not be read since the start (recovered from its previous generation), and the
        // unreadable copies kept in the data directory — they outlast the restart that usually follows a power loss.
        var recovered = JsonStoreRecovery.Recovered;
        var kept = CorruptCopies();
        checks.Add(new ReadinessCheck(
            "Datenspeicher lesbar",
            recovered.Count == 0 && kept.Count == 0,
            recovered.Count == 0 && kept.Count == 0
                ? "Jede Speicherdatei (Rollen, Whitelist, Vault, Server, Berechtigungen …) ist lesbar; keine beschädigte Datei liegt im Datenverzeichnis."
                : string.Join(" ", new[]
                  {
                      recovered.Count == 0 ? null
                          : "Seit dem Start aus der vorigen Fassung wiederhergestellt — die letzte Änderung kann fehlen; "
                            + "Rollen, Whitelist, MCP-Schlüssel und Vault auf einen wieder geltenden, entzogenen Zugang prüfen: "
                            + string.Join(", ", recovered.Select(r => Path.GetFileName(r.Path))) + ".",
                      kept.Count == 0 ? null
                          : "Unlesbare Dateien aufbewahrt (prüfen, dann löschen): " + string.Join(", ", kept) + ".",
                  }.Where(s => s is not null)),
            null, null));

        // 5) Non-root / hardened profile. The hardened compose runs the container as uid 10001;
        // a privileged (root) process signals that the default profile is in use.
        var nonRoot = !Environment.IsPrivilegedProcess;
        checks.Add(new ReadinessCheck(
            "Hardened-Profil (non-root)",
            nonRoot,
            nonRoot
                ? "Der Prozess läuft ohne Root-Rechte (hardened Profil)."
                : "Der Prozess läuft privilegiert — für den Dauerbetrieb docker-compose.hardened.yml verwenden (non-root, read-only rootfs, socket-proxy).",
            null, null));

        // 6) HTTPS at the proxy — the flag comes from the caller's page URI (NavigationManager),
        // which reflects X-Forwarded-Proto through UseForwardedHeaders.
        checks.Add(new ReadinessCheck(
            "HTTPS am Reverse Proxy",
            isHttps,
            isHttps
                ? "Diese Seite wurde über HTTPS ausgeliefert."
                : "Diese Verbindung läuft über HTTP — im Dauerbetrieb einen TLS-terminierenden Reverse Proxy (Caddy/Traefik/nginx) davorschalten.",
            null, null));

        // R1-50 (A-14): OIDC discovery over plain HTTP lets a man in the middle swap the IdP's keys and endpoints.
        if (_config.GetValue<bool>("Oidc:Enabled") && !_config.GetValue("Oidc:RequireHttpsMetadata", true))
        {
            checks.Add(new ReadinessCheck(
                "OIDC-Discovery über HTTPS",
                false,
                "Oidc:RequireHttpsMetadata ist aus: die Schlüssel und Endpunkte des Identity Providers werden über HTTP geholt " +
                "und können unterwegs ausgetauscht werden. Nur im abgeschotteten Testnetz so betreiben.",
                null, null));
        }

        // 7) R1-50: forwarded headers from a peer that is not a trusted proxy were ignored — either a reverse proxy that
        // still has to be named in ForwardedHeaders:TrustedNetworks (until then the client address, the login rate limit
        // and the audit see the proxy) or a client claiming another address.
        if (_forwarded?.RecentUntrustedPeers is { Count: > 0 } untrusted)
        {
            var peers = string.Join(", ", untrusted.Select(u => $"{u.Peer} ({u.At:yyyy-MM-dd HH:mm} UTC)"));
            checks.Add(new ReadinessCheck(
                "Reverse-Proxy vertraut",
                false,
                $"X-Forwarded-Header von Peers, die keine vertrauten Proxys sind, wurden ignoriert (letzte 24 h): {peers}. " +
                "Ist einer davon Ihr Reverse Proxy, tragen Sie seine Adresse möglichst eng (z. B. /32 bzw. /128) in " +
                "ForwardedHeaders__TrustedNetworks__0 ein (Standard: nur Loopback) — sonst sehen Login-Limit und Audit nur " +
                "seine Adresse, HTTPS wird als HTTP erkannt (Session-Cookie ohne Secure, Google-/OIDC-Rückruf mit http://) " +
                "und Auth:Disabled weist die Anfragen ab. Eine öffentliche Adresse ist eher ein Client, der eine fremde " +
                "Adresse vortäuschen wollte — nicht eintragen.",
                null, null));
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
    /// R1-44 (contract 0007): the per-host credential lines and the live lines together. The live arguments are read
    /// first so the per-host line of a host whose arguments cannot be read points to the break-glass re-onboarding
    /// instead of „Migrieren“ (review r2 A: a restored older self-backup brings back the record and the journal
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
        var hosts = results;

        var down = results.Where(r => !r.Live.Readable || !r.Live.Running)
            .Select(r => r.Server.Name + (r.Live.Readable ? " (ghostunnel läuft nicht)" : " (Argumente nicht lesbar)"))
            .ToList();
        checks.Add(new ReadinessCheck("mTLS-Proxy (ghostunnel) läuft", down.Count == 0,
            down.Count == 0
                ? $"Auf allen {hosts.Count} mTLS-Hosts läuft ghostunnel; die laufenden Argumente sind lesbar."
                : "Nicht erreichbar über mTLS: " + string.Join(", ", down) + ". Der mTLS-Kanal ist der einzige Verwaltungsweg; " +
                  "zurück geht es über das Notfall-Re-Onboarding per SSH (docs/operations/per-host-mtls-migration.md).",
            down.Count == 0 ? null : "/servers", down.Count == 0 ? null : "Server"));

        // Every readable host whose running ghostunnel still admits the shared name, whatever its record uses —
        // operator-supplied credentials included (contract 0007, amendment 3 m-9; review r1 M1): a stolen shared key opens it.
        var sharedCn = Whiskers.Services.Certificates.MtlsCertificateService.CommonNameOf(store.SharedPaths.Cert)
                       ?? Whiskers.Services.Certificates.MtlsCertificateService.SharedClientCommonName;
        var admitShared = results.Where(r => r.Live is { Readable: true, Running: true } && r.Live.AllowedCommonNames.Contains(sharedCn))
            .Select(r => r.Server.Name).ToList();
        checks.Add(new ReadinessCheck("Gemeinsame Client-Identität im laufenden ghostunnel", admitShared.Count == 0,
            admitShared.Count == 0
                ? $"Kein lesbarer mTLS-Host nimmt laut docker inspect noch {sharedCn} an."
                : $"Nimmt laut docker inspect noch {sharedCn} an: " + string.Join(", ", admitShared) +
                  ". Ein gestohlener gemeinsamer Schlüssel öffnet diese Hosts. Von Whiskers verwaltete Hosts: „Migrieren“; " +
                  "Hosts mit selbst gesetzten Zertifikaten: --allow-cn dort von Hand einengen (docs/operations/per-host-mtls-migration.md).",
            admitShared.Count == 0 ? null : "/servers", admitShared.Count == 0 ? null : "Server"));

        var mismatched = new List<string>();
        foreach (var (server, live) in results.Where(r => r.Live is { Readable: true, Running: true }))
        {
            if (!Whiskers.Services.Certificates.MtlsClientCredentialStore.IsValidServerId(server.Id)) continue;
            var status = store.StatusOf(server);
            if (status.Kind != Whiskers.Services.Certificates.MtlsClientCredentialKind.Own || store.ReadJournal(server.Id) is not { IsNarrowed: true })
                continue;
            if (live.AllowedCommonNames.Count != 1 || live.AllowedCommonNames[0] != status.CommonName)
                mismatched.Add($"{server.Name} (läuft mit {string.Join(", ", live.AllowedCommonNames)})");
        }
        checks.Add(new ReadinessCheck("Laufende --allow-cn-Liste", mismatched.Count == 0,
            mismatched.Count == 0
                ? "Jeder eingeengte Host nimmt laut docker inspect genau seine eigene Client-Identität an."
                : "Eingeengt, aber der laufende ghostunnel nimmt anderes an: " + string.Join(", ", mismatched) + ". „Migrieren“ erneut starten.",
            mismatched.Count == 0 ? null : "/servers", mismatched.Count == 0 ? null : "Server"));
        return checks;
    }

    /// <summary>
    /// R1-44 (contract 0007): one line per mTLS host about its client credential — a host on the shared credential and a
    /// migration that is not finished are not OK, and so is a host whose live ghostunnel arguments could not be read
    /// (<paramref name="unreadable"/>): after an older self-backup was restored, the record and the journal come back
    /// together and name a credential the host no longer admits, so the way back is the break-glass re-onboarding, not
    /// „Migrieren“ — plus the shared key still lying around while hosts no longer accept it, and the modes (Unix) or
    /// ACLs (Windows) of every client key file.
    /// </summary>
    internal IReadOnlyList<ReadinessCheck> MtlsClientCredentialChecks(IReadOnlySet<string>? unreadable = null)
    {
        var checks = new List<ReadinessCheck>();
        if (_clientCredentials is not { } store || _servers is null) return checks;

        var narrowedHosts = 0;
        foreach (var server in _servers.GetServers().Where(s => s.TcpUseTls && !string.IsNullOrWhiteSpace(s.TcpClientCertPath)))
        {
            var title = $"mTLS-Client-Identität: {server.Name}";
            if (!Whiskers.Services.Certificates.MtlsClientCredentialStore.IsValidServerId(server.Id))
            {
                checks.Add(new ReadinessCheck(title, false, $"Die Server-ID '{server.Id}' taugt nicht als Verzeichnisname; dieser Host kann keine eigene Identität bekommen.", null, null));
                continue;
            }
            var status = store.StatusOf(server);
            var journal = store.ReadJournal(server.Id);
            switch (status.Kind)
            {
                case Whiskers.Services.Certificates.MtlsClientCredentialKind.Shared or Whiskers.Services.Certificates.MtlsClientCredentialKind.Own
                    when unreadable?.Contains(server.Id) == true:
                    // E.g. an older self-backup restored after a migration or rotation (review r2 A): the record and the
                    // journal name a credential the host no longer admits, so „Migrieren“ cannot reach it either.
                    if (journal is { IsNarrowed: true }) narrowedHosts++;
                    checks.Add(new ReadinessCheck(title, false,
                        (status.Kind == Whiskers.Services.Certificates.MtlsClientCredentialKind.Shared
                            ? "Der Eintrag nennt die gemeinsame Client-Identität"
                            : $"Der Eintrag nennt {status.CommonName} (Generation {status.Generation})") +
                        ", aber die laufenden ghostunnel-Argumente des Hosts sind nicht lesbar — vermutlich nimmt er diese Identität " +
                        "nicht mehr an (älteres Backup zurückgespielt?). „Migrieren“ erreicht ihn so nicht; zurück über das " +
                        "Notfall-Re-Onboarding per SSH (docs/operations/per-host-mtls-migration.md, „Älteres Backup“).", "/servers", "Server"));
                    break;
                case Whiskers.Services.Certificates.MtlsClientCredentialKind.Shared:
                    checks.Add(new ReadinessCheck(title, false,
                        "Nutzt noch die gemeinsame Client-Identität: ein gestohlener Schlüssel öffnet jeden Host, der sie annimmt. " +
                        "Auf der Server-Seite „Migrieren“ (Anleitung: docs/operations/per-host-mtls-migration.md).", "/servers", "Server"));
                    break;
                case Whiskers.Services.Certificates.MtlsClientCredentialKind.Own when journal is { IsNarrowed: true }:
                    narrowedHosts++;
                    checks.Add(new ReadinessCheck(title, true,
                        $"Eigene Identität {status.CommonName} (Generation {status.Generation})" +
                        (status.NotAfterUtc is { } until ? $", gültig bis {until:yyyy-MM-dd}" : "") +
                        "; der Host nimmt nur sie an.", null, null));
                    break;
                case Whiskers.Services.Certificates.MtlsClientCredentialKind.Own:
                    checks.Add(new ReadinessCheck(title, false,
                        $"Eigene Identität {status.CommonName}, aber die Umstellung ist nicht abgeschlossen (Phase {journal?.Phase ?? 0} von 5): " +
                        "der Host nimmt womöglich noch die gemeinsame Identität an. „Migrieren“ erneut starten — es setzt an der Stelle fort.", "/servers", "Server"));
                    break;
                default:
                    checks.Add(new ReadinessCheck(title, true,
                        "Vom Betreiber gesetzte Client-Zertifikatspfade; Whiskers verwaltet sie nicht. Ob der ghostunnel des Hosts " +
                        "die gemeinsame Identität noch annimmt, zeigt die Zeile „Gemeinsame Client-Identität im laufenden ghostunnel“.", null, null));
                    break;
            }
        }

        var shared = store.SharedPaths;
        if (File.Exists(shared.Key) && narrowedHosts > 0)
            checks.Add(new ReadinessCheck("Gemeinsamer mTLS-Client-Schlüssel", false,
                $"Der gemeinsame Client-Schlüssel liegt noch im Datenverzeichnis, obwohl {narrowedHosts} Host(s) ihn nicht mehr annehmen — " +
                "vor dem Stilllegen, oder nach dem Zurückspielen eines älteren Backups (ältere Self-Backups enthalten ihn noch). " +
                "Sind alle Hosts migriert: auf der Server-Seite stilllegen. Ist ein Host so nicht mehr erreichbar: Notfall-Re-Onboarding per SSH.",
                "/servers", "Server"));

        var keys = store.KeyFiles();
        if (keys.Count > 0)
        {
            var exposed = keys.Where(KeyIsExposed).Select(k => Path.GetRelativePath(Path.GetDirectoryName(shared.Key)!, k).Replace('\\', '/')).ToList();
            checks.Add(new ReadinessCheck("mTLS-Client-Schlüssel geschützt", exposed.Count == 0,
                exposed.Count == 0
                    ? (OperatingSystem.IsWindows()
                        ? "Keiner der Client-Schlüssel ist für Jeder, Benutzer oder authentifizierte Benutzer lesbar."
                        : "Alle Client-Schlüssel haben den Modus 0600.")
                    : (OperatingSystem.IsWindows()
                        ? "Für alle Benutzer lesbar: "
                        : "Nicht 0600: ") + string.Join(", ", exposed) + ".",
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

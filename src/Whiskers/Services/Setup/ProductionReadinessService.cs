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
    private readonly IConfiguration _config;
    private readonly IVaultService _vault;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerConfigService? _servers;
    private readonly IAppSettingsStore? _appSettings;
    private readonly Whiskers.Services.Agent.IAgentSettingsStore? _agentSettings;
    private readonly VaultKeyRingRepository? _keyRing;
    private readonly Whiskers.Configuration.DataPathOptions? _dataPaths;

    public ProductionReadinessService(IConfiguration config, IVaultService vault, IServiceScopeFactory scopeFactory,
        IServerConfigService? servers = null, IAppSettingsStore? appSettings = null,
        Whiskers.Services.Agent.IAgentSettingsStore? agentSettings = null, Whiskers.Configuration.DataPathOptions? dataPaths = null,
        VaultKeyRingRepository? keyRing = null)
    {
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
                          : "Seit dem Start aus der vorigen Fassung wiederhergestellt (die letzte Änderung kann fehlen): "
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

        return checks;
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

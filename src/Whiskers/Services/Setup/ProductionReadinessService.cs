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

    public ProductionReadinessService(IConfiguration config, IVaultService vault, IServiceScopeFactory scopeFactory,
        IServerConfigService? servers = null, IAppSettingsStore? appSettings = null)
    {
        _config = config;
        _vault = vault;
        _scopeFactory = scopeFactory;
        _servers = servers;
        _appSettings = appSettings;
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
                VaultKeyState.Verified => "Der Vault ist aktiv; Backups sind verschlüsselbar. Im Vault liegen Cloud-API-Keys, der DNS-Token, "
                                          + "kubeconfigs sowie Registry- und Git-Zugangsdaten. Benachrichtigungs- und KI-Zugangsdaten und "
                                          + "Webhook-Secrets liegen noch unverschlüsselt in den Einstellungsdateien.",
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
                      ? "Die Übernahme in den Vault ist beim Start fehlgeschlagen — das Log nennt den Grund; nach dem Beheben neu starten."
                      : "Sie werden beim ersten Start mit gültigem VAULT_KEY in den Vault übernommen."),
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

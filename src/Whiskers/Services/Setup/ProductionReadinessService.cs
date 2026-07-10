using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;
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

    public ProductionReadinessService(IConfiguration config, IVaultService vault, IServiceScopeFactory scopeFactory)
    {
        _config = config;
        _vault = vault;
        _scopeFactory = scopeFactory;
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
            _vault.IsEnabled
                ? "Secrets werden verschlüsselt gespeichert; Backups sind verschlüsselbar."
                : "Kein VAULT_KEY gesetzt — Vault und verschlüsselte Backups sind deaktiviert. Key setzen und sicher ablegen.",
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
}

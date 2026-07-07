# Changelog — Security-Review-Fixes (2026-07-07)

Umsetzung der kritischen und hohen Findings aus dem [Full-Repo-Review 2026-07-06](2026-07-06-full-repo-review.md).
Build grün, 123/123 Unit-Tests bestehen. Kein Deploy (erfolgt manuell durch den Benutzer).

Konvention: keine Claude-Attribution in Commits (Projektregel). In-Code-Kommentare auf Englisch.

## Kritisch (KRIT)

- **KRIT-1 — RBAC-Bypass Scheduler.** `SchedulerTools.CreateScheduledTask`/`RunScheduledTask` verlangen jetzt die `execute_command`-(Admin-)Berechtigung, wenn der Task-Typ `CustomCommand` ist. Ein Write-Level-Key kann keine beliebigen Root-Kommandos mehr über den Scheduler ausführen.
  _Dateien: `Mcp/Tools/SchedulerTools.cs`._
- **KRIT-2 — Agent Auto-Confirm / synthetischer Admin (AI-Trigger).** `AiTriggerDispatcher` baut den Principal nicht mehr als Admin, sondern mit dem neuen konfigurierbaren `AiTrigger.MaxLevel` (Default `write`); Guardrail-Confirmations werden abgelehnt statt automatisch bestätigt. Neue Helper `McpPermissionLevels.Normalize`.
  _Dateien: `Services/Agent/Triggers/AiTriggerDispatcher.cs`, `Models/AiTrigger.cs`, `Models/McpPermission.cs`._
- **KRIT-3 — `/cves` ohne Auth.** `@attribute [Authorize]` ergänzt. (Fallback-Policy in Program.cs bewusst zurückgestellt — Auth-Middleware Off-Limits.)
  _Dateien: `Components/Pages/Cves.razor`._
- **KRIT-4 — Volume-Restore Datenverlust.** `RestoreVolumeAsync` validiert das Archiv (`tar tzf`, read-only) und zieht ein Safety-Backup **vor** dem Wipe; löscht auch Dotfiles (`find /data -mindepth 1 -delete`).
  _Dateien: `Services/Backup/VolumeBackupService.cs`._
- **KRIT-5 — DB-Backups nicht durabel.** `BackupDatabaseAsync` kopiert den Dump per `docker cp` auf den Host (`/app/data/backups`) und entfernt die Container-Kopie; gibt den Host-Pfad zurück. `TaskExecutor.ApplyRetention` erhielt einen `DbBackup`-Zweig und scoped Volume-Retention auf `ServerId ?? "local"` (MIT-30).
  _Dateien: `Services/Database/DatabaseService.cs`, `Services/Scheduler/TaskExecutor.cs`._

## Hoch (HOCH)

- **HOCH-1 — `instruct_agent` Auto-Confirm.** Confirmations werden abgelehnt statt automatisch bestätigt.
  _Dateien: `Mcp/Tools/AgentTools.cs`._
- **HOCH-2 — Toter TerminalHub.** `Hubs/TerminalHub.cs` gelöscht (unauthentifizierte Root-Shell, von keinem Client genutzt), MapHub-Zeile entfernt, Docs synchronisiert. `ContainerHub` bleibt (aktiv genutzt).
  _Dateien: `Hubs/TerminalHub.cs` (gelöscht), `Program.cs`, `Hubs/README.md`, `Services/Terminal/README.md`, `wwwroot/js/README.md`._
- **HOCH-3 — Approvals-Autorisierung.** `IApprovalStore.ResolveAsync` erhielt einen optionalen `resolverLevel`; Genehmigen nur, wenn das Resolver-Level ≥ dem Approval-Level ist. Die UI übergibt das gemappte Rollen-Level und zeigt bei Ablehnung einen Fehler.
  _Dateien: `Services/Agent/Approvals/ApprovalStore.cs`, `Components/Pages/Approvals.razor`._
- **HOCH-4 — Vault-Krypto.** AES-256-CBC (kein MAC) + unsalted SHA256 → **AES-256-GCM + PBKDF2 (600k)** mit persistiertem Salt und transparenter Migration alter Einträge; `GetSecret` loggt Decrypt-Fehler. Siehe [ADR 0001](../adr/0001-vault-aead-gcm-pbkdf2.md).
  _Dateien: `Services/Vault/VaultService.cs`, `Models/VaultEntry.cs`._
- **HOCH-5 — Whitelist-Aliasing / enabled-empty.** `SaveWhitelistAsync` deep-copyt vor dem Cachen; `IsEmailAllowed`: enabled+leer = deny all; Settings-UI verweigert das Speichern von enabled+leer (kein Selbst-Aussperren). (inkl. MIT-5)
  _Dateien: `Services/Auth/WhitelistService.cs`, `Components/Pages/Settings.razor`._
- **HOCH-6 — Synthetischer AuthDisabled-Admin.** Neuer `AgentSyntheticScheme` + `McpLevelClaim` (zentral in `AuthConstants`); `AgentToolInvoker.BuildSyntheticContext` trägt das echte Level als Claim, `McpPermissionCheck` erzwingt es statt Admin.
  _Dateien: `Services/Auth/AuthConstants.cs` (neu), `Services/Agent/AgentToolInvoker.cs`, `Mcp/McpPermissionCheck.cs`._
- **HOCH-7 — CVE-Scan überschreibt gute Ergebnisse.** OS- und Container-Zweig überschreiben den Store nur bei `Error is null`; sonst bleiben die vorherigen Ergebnisse erhalten.
  _Dateien: `Services/Cve/CveMonitorService.cs`._
- **HOCH-8 — CVE-Identity.** `CveFinding.IdentityKey` verwendet den Container**namen** statt der -ID (Alter/Notification stabil über Recreates).
  _Dateien: `Models/Cve/CveFinding.cs`._
- **HOCH-9 — `PullImageAsync`-Parsing.** Neuer `ParseImageReference`-Helper: Registry-Port (`host:5000/app`) und Digest (`repo@sha256:…`) werden korrekt behandelt.
  _Dateien: `Services/Docker/DockerService.cs`._
- **HOCH-10 — Container-Recreate ohne Rollback.** `RecreateContainerAsync` benennt den alten Container um statt ihn zu löschen, erstellt/startet den neuen (nur erstes Netzwerk beim Create, Rest per Connect), entfernt den alten erst bei Erfolg; bei Fehler Rollback (zurück-umbenennen + starten).
  _Dateien: `Services/Docker/DockerService.cs`._
- **HOCH-12 (Kern) — Webhook-Deploy-Injection.** `DeployCompose` verlangt einen Absolut-Pfad und quotet `TargetId` (`ShellUtils.Quote`). (Secret-Pflicht Teil 2 zurückgestellt — braucht Secret-UI.)
  _Dateien: `Services/Webhooks/WebhookService.cs`._
- **HOCH-13 — Hostinger-Metrics.** `GetMetricsRawAsync` hängt die Pflichtparameter `date_from`/`date_to` (24h-Fenster) an.
  _Dateien: `Services/Hostinger/HostingerApiService.cs`._

## Zusätzlich vorgezogen

- **NIED-20.6** — `Webhooks.razor.TestWebhook` verlangt jetzt die Operator-Rolle.

## Mittel & Niedrig — Bean-Abarbeitung (ab 2026-07-07)

Umsetzung der verbleibenden Findings, ein Bean pro Cluster (`feat/ServerWatch-<id>-…`-Branches, ein Finding = ein Commit). Build grün, Tests grün, DI per Development-Boot validiert.

### ServerWatch-sdn3 — Concurrency & Cache-Aliasing (Auth/Config/Vault)

- **MIT-1 — RoleService Aliasing/Lock.** `SaveRoleDataAsync` klont vor Cache/Persist; `SetRoleAsync`/`RemoveRoleAsync` bauen den Snapshot im Write-Lock und persistieren ihn danach — nie mehr die Live-Liste serialisieren, die ein paralleler Writer mutiert.
- **MIT-2 — VaultService Lesepfade.** `ListSecrets`/`GetSecret`/`GetExpiringSecrets` laufen jetzt unter demselben Lock wie die Writer (kein transientes `null`/`InvalidOperationException` bei paralleler Mutation).
- **NIED-14 — ServerConfigService.** `SaveSshKeyAsync`/`DeleteSshKeyAsync` arbeiten auf `GetServer(id)?.Clone()` statt das gecachte Live-Objekt zu mutieren.
- _Zusätzlich:_ DI-safe optionaler `storePath`-Ctor-Seam + neue `ConcurrencyCacheAliasingTests` (Aliasing, Concurrency, Fehlerpfad, Secret-Safety); 131/131 Tests.
  _Dateien: `Services/Auth/RoleService.cs`, `Services/Vault/VaultService.cs`, `Services/ServerConfig/ServerConfigService.cs`, per-Ordner-READMEs (Auth/Vault/ServerConfig)._
### ServerWatch-wjhf — Audit-Log: fail-safe Fallback + Coverage

- **MIT-4 — Audit fail-open.** `AuditLogService.LogAsync` loggt bei Schreibfehler (DB locked/Disk voll) den kompletten Eintrag strukturiert auf Error, statt nur "Failed to write audit log entry" — die Fakten überleben.
- **MIT-3 — Vault-Audit.** `Settings.razor` loggt `vault.set`/`vault.delete` (Key-Namen only); das Inline-Delete wurde in `DeleteVaultSecret` extrahiert. Neuer `AuditVault`-Helper.
- **MIT-7 — Scheduler-Audit.** `SchedulerTools` Create/Run/Delete bekommen einen DI-injizierten `IAuditLogService`-Tool-Param und schreiben `scheduler.create`/`run`/`delete`; CustomCommand via `SecretRedactor.Redact` redigiert.
- **NIED-4 — Rescue-Audit.** `HetznerEnableRescue` loggt `hetzner.rescue_enable` ("root credential issued") — das temporäre Passwort geht nur an den Aufrufer, nie in den Audit-Log.
- _Tests:_ Fail-safe-Fallback (throwing ScopeFactory + capturing Logger) + Redaction-Test; MCP-Tool-Wiring durch Build + DI-Boot + ServerTools-Muster gedeckt.
  _Dateien: `Services/AuditLog/AuditLogService.cs`, `Components/Pages/Settings.razor`, `Mcp/Tools/SchedulerTools.cs`, `Mcp/Tools/HetznerTools.cs`, `Services/AuditLog/README.md`._
Umsetzung der verbleibenden Findings, ein Bean pro Cluster (`feat/ServerWatch-<id>-…`-Branches, ein Finding = ein Commit). Build grün, Tests grün.

### ServerWatch-izcu — MCP-Tool Input-Validation & Injection-Härtung

- **MIT-6 — deploy_compose Path-Traversal.** Neuer `McpInputValidation.IsSafeProjectName` (Leading-Alnum, Safe-Charset, `..`-Verbot) ersetzt die schwache Regex; das Ziel-Verzeichnis wird zusätzlich per `ShellUtils.Quote` in mkdir/cd gequotet.
- **NIED-2 — Container-ID-Ambiguität.** Neuer `McpInputValidation.Resolve`: exakter Id/Name-Match, sonst eindeutiger Id-Präfix; Mehrdeutigkeit/No-Match → klarer Fehler statt Aktion am falschen Container. Alle 10 Inline-Sites (ContainerTools 8, DatabaseTools 2) umgestellt (kein `?? containerId`-Fallback mehr).
- **NIED-12 — Options-Injection.** `SafeServiceNameRegex`/`SafeCertNameRegex` verlangen ein führendes alphanumerisches Zeichen (kein `-` am Anfang → kein systemctl/certbot-Flag).
- **NIED-3 — ReDoS.** Die Match-Pfade (LogSearchService 2s, Monitor-Loop 1s) waren bereits time-bounded; der Validierungs-Compile in `CreateRuleAsync` bekommt defense-in-depth ebenfalls ein matchTimeout.
- _Tests:_ `McpInputValidationTests` (Projektname-Validierung, Resolver: exakt/Präfix/mehrdeutig/No-Match). Kein DI-/Ctor-Change → kein Boot nötig.
- ⚠️ **NIED-1 (Bearer-Scheme case) NICHT umgesetzt** — auth-middleware-nah, zurückgestellt bis zur ausdrücklichen Freigabe.
  _Dateien: `Mcp/Tools/McpInputValidation.cs` (neu), `Mcp/Tools/ContainerTools.cs`, `Mcp/Tools/DatabaseTools.cs`, `Services/Server/SystemdService.cs`, `Services/Server/SslCertService.cs`, `Services/LogMonitor/LogMonitorService.cs`, `Mcp/Tools/README.md`._
### ServerWatch-gny5 — Destruktiv-Op-Zielauflösung (Cloud/AutoUpdate/Hetzner)

- **MIT-22 — Hetzner-Snapshot-Löschung.** Neuer `IHetznerService.GetImageAsync` (GET `/images/{id}`, 404→null) + `HetznerImageResponse`; `hetzner_delete_snapshot` lädt das Image und verweigert null oder `Type != "snapshot"` (Backups/System-Images geschützt). Helper `IsDeletableSnapshot`.
- **MIT-20 — AutoUpdate falscher Host.** Neuer `AutoUpdateService.MatchesPolicy` (ServerId-scoped, Id-Match vor Name-Match); der Cross-Server-Match und der `SetPolicy`-Lookup sind jetzt auf `policy.ServerId` begrenzt — kein Recreate eines gleichnamigen Containers auf einem anderen Host.
- **MIT-21 — Cloud Namens-Fallback.** `ResolveAsync` setzt bei Namens-Fallback (IP-Match fehlgeschlagen) ein `Note`; `DispatchAsync`/`HardReset` geben es mit ⚠️ aus.
- **MIT-26 — Hetzner-Pagination.** Neuer `ListAllPagesAsync`-Helper (page-Loop, `per_page=50`) für Servers/Snapshots/ServerTypes; `per_page=100` (>API-Max) entfernt.
- **NIED-17 — Hostinger Hard-Reset.** `HardResetAsync` sagt für Hostinger explizit „kein Hard-Reset — Neustart ausgelöst (evtl. wirkungslos)"; Hetzner unverändert.
- _Tests:_ `DestructiveOpTargetingTests` (IsDeletableSnapshot snapshot/backup/system/null; MatchesPolicy same-server/empty/id-vs-name). MIT-21/26/NIED-17 durch Build + DI-Boot + Inspektion (HTTP-Stubs fehlen). Interface-Change (MIT-22) → App gebootet.
  _Dateien: `Services/Hetzner/{IHetznerService,HetznerApiService}.cs`, `Models/Hetzner/HetznerModels.cs`, `Mcp/Tools/HetznerTools.cs`, `Services/AutoUpdate/AutoUpdateService.cs`, `Services/Cloud/CloudControlService.cs`, per-Ordner-READMEs (Hetzner/Cloud/AutoUpdate)._
Umsetzung der verbleibenden Findings, ein Bean pro Cluster (`feat/ServerWatch-<id>-…`-Branches, ein Finding = ein Commit). Build grün, Tests grün.

### ServerWatch-b9qw — Secret-Hygiene (argv & Logs)

- **MIT-39 — DB-Passwörter in argv + Debug-Logs.** `BuildMysqlCmd` (alle MySQL-Pfade) und der Neo4j-Zweig übergeben das Passwort via `MYSQL_PWD`/`NEO4J_PASSWORD` unter `sh -c` (Sq-gequotet, wie `BackupDatabaseAsync`) statt `-p<pw>` in argv → nicht mehr in der Container-Prozessliste. Die drei rohen `LogDebug`-Kommandos in `HostCommandExecutor` laufen durch `SecretRedactor.Redact`. (ContainerDetail-DB-Kommandos waren bereits env-var-sicher.)
- **MIT-14 — Notification-Tokens in Logs.** Sechs capability-tragende Notification-HttpClients (Telegram/Discord/Slack/Mattermost/ntfy/Webhook) werden auf `System.Net.Http.HttpClient.<Name>` = Warning gefiltert, sodass die volle Request-URI (mit Token/Secret-URL) nicht mehr auf Information geloggt wird.
- **NIED-18 — VPN-Enrollment-Secrets in argv.** `VpnProcessRunner.RunAsync` bekommt einen optionalen env-Dict-Parameter; Tailscale übergibt den Auth-Key via `TS_AUTHKEY`, NetBird via `NB_SETUP_KEY` (statt `--authkey`/`--setup-key` in argv).
- _Tests:_ `SecretHygieneTests` (Redaction hides `-p<pw>` / `MYSQL_PWD=` / `PGPASSWORD=` / keyed secrets). Kein Ctor-/Interface-Change → kein Boot.
  _Dateien: `Services/Database/DatabaseService.cs`, `Services/Server/HostCommandExecutor.cs`, `Program.cs`, `Services/Vpn/VpnProcessRunner.cs`, `Services/Vpn/Providers/{Tailscale,Netbird}VpnProvider.cs`, per-Ordner-READMEs (Database/Vpn/Notifications)._

## Bewusst zurückgestellt (Begründung im Review-Doc / ADR)

- **HOCH-11** — SSH `StrictHostKeyChecking=no` → `accept-new`: Aussperr-Risiko bei Host-Key-Wechsel + Off-Limits-Netzwerk-Layer. Siehe [ADR 0002](../adr/0002-ssh-host-key-verification-deferred.md). Braucht ausdrückliche Freigabe.
- **KRIT-3 Schritt 2** — Fallback-Authorization-Policy: könnte `/mcp` brechen; Auth-Middleware Off-Limits.
- **HOCH-12 Teil 2** — secretlose Webhooks ablehnen: braucht Secret-Management-UI, sonst Feature unbrauchbar.

## Verifikation

- `dotnet build src/ServerWatch/ServerWatch.csproj` → 0 Fehler.
- `dotnet test src/ServerWatch.Tests` → 123/123 bestanden.
- Kein App-Boot durchgeführt; es wurden keine DI-Registrierungen oder Konstruktor-Abhängigkeiten geändert (DI-Graph unverändert). Vor dem Deploy dennoch einmal booten (Development/ValidateOnBuild).

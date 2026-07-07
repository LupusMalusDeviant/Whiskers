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

## Mittel & Niedrig — Bean-Abarbeitung

### ServerWatch-07q1 — Cleanup: Dead-Code, Templates, SSL/Terminal-Bugs (NIED-23, NIED-19, NIED-10, NIED-11)

- **NIED-23 — Toter Code entfernt.** `DockerConnectionFactory` (kein Aufrufer, keine Registrierung), die `IDockerConnectionManager.Client`-Property (kein Aufrufer — aus Interface + Impl) und der unbenutzte `ConfigExport`-Service (nur in Program.cs registriert, kein UI/MCP-Aufrufer) inkl. Registrierung + Ordner gelöscht; leeres `Auth/`-Verzeichnis entfernt; Docker-README bereinigt. **ConfigExport = gelöscht** (das Bean erlaubt löschen-oder-verdrahten; Cleanup-Thema, vermeidet eine neue Admin-Export-Fläche; der Export war bereits secret-frei und ist trivial aus Git wiederherstellbar).
  _Dateien: `Services/Docker/DockerConnectionFactory.cs` (gelöscht), `Services/Docker/IDockerConnectionManager.cs`, `Services/Docker/DockerConnectionManager.cs`, `Services/ConfigExport/*` (gelöscht), `Program.cs`, `Services/Docker/README.md`._
- **NIED-19 — Kaputte Templates repariert.** Plausible: `DATABASE_URL` + `CLICKHOUSE_DATABASE_URL` ergänzt, PG-Passwort jetzt Pflicht-Var `{DB_PASSWORD}` (kein hartkodiertes `postgres`), CE-Init-`command`, überschreibbare `BASE_URL`. n8n: das in n8n 1.x entfernte `N8N_BASIC_AUTH_*` raus (Owner-Setup via UI, im Compose kommentiert), `N8N_ENCRYPTION_KEY` als Pflicht. Rocket.Chat: MongoDB als Replica-Set (`bitnami/mongodb` mit `MONGODB_REPLICA_SET_MODE=primary`/`rs0`) — ohne Replica-Set startet Rocket.Chat gar nicht.
  _Dateien: `Services/Templates/TemplateService.cs`._
- **NIED-10 — SSL-Ablauf-Fehlalarm.** `SslCertificate.ExpiresAt` ist jetzt `DateTime?`; ein unparsebares certbot-Datum lässt den Ablauf „unbekannt" statt `DateTime.MinValue` (das jedes solche Zertifikat als „läuft bald ab" markierte). `DaysUntilExpiry`/`IsExpiringSoon` sind null-sicher; UI + MCP-Tool zeigen „unbekannt".
  _Dateien: `Services/Server/SslCertService.cs`, `Components/Pages/SslCerts.razor`, `Mcp/Tools/ServerTools.cs`._
- **NIED-11 — Terminal-Edge-Cases.** `LastActivityAt` wird jetzt auch bei Output aktualisiert (`TerminalSession.Touch()` im server-seitigen Read-Loop) — ein Terminal mit laufendem Output wird nicht mehr idle-gekillt. Das Max-Sessions-Limit wird atomar geprüft (`RegisterSession` unter Lock) statt Count-dann-Add (TOCTOU).
  _Dateien: `Services/Terminal/TerminalSession.cs`, `Services/Terminal/TerminalSessionManager.cs`, `Components/Pages/Terminal.razor`._

**Verifikation (Branch `feat/ServerWatch-07q1-cleanup-dead-code-templates`):** Build 0 Fehler, `dotnet test` 133/133 (10 neu: `TemplateServiceTests`, `SslCertificateTests` inkl. Parse-Fehler-Regression, `TerminalSessionTests`). App in Development gebootet — „Application started" (DI-Graph nach Entfernen der ConfigExport-Registrierung + des `Client`-Interface-Members validiert). Der Terminal-Cap-Race-Fix ist ein Lock (prozess-startende Sessions sind auf der Dev-Box nicht unit-testbar) → durch Build + Boot + Review abgedeckt.

## Bewusst zurückgestellt (Begründung im Review-Doc / ADR)

- **HOCH-11** — SSH `StrictHostKeyChecking=no` → `accept-new`: Aussperr-Risiko bei Host-Key-Wechsel + Off-Limits-Netzwerk-Layer. Siehe [ADR 0002](../adr/0002-ssh-host-key-verification-deferred.md). Braucht ausdrückliche Freigabe.
- **KRIT-3 Schritt 2** — Fallback-Authorization-Policy: könnte `/mcp` brechen; Auth-Middleware Off-Limits.
- **HOCH-12 Teil 2** — secretlose Webhooks ablehnen: braucht Secret-Management-UI, sonst Feature unbrauchbar.

## Verifikation

- `dotnet build src/ServerWatch/ServerWatch.csproj` → 0 Fehler.
- `dotnet test src/ServerWatch.Tests` → 123/123 bestanden.
- Kein App-Boot durchgeführt; es wurden keine DI-Registrierungen oder Konstruktor-Abhängigkeiten geändert (DI-Graph unverändert). Vor dem Deploy dennoch einmal booten (Development/ValidateOnBuild).

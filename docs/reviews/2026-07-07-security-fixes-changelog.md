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

## Bewusst zurückgestellt (Begründung im Review-Doc / ADR)

- **HOCH-11** — SSH `StrictHostKeyChecking=no` → `accept-new`: Aussperr-Risiko bei Host-Key-Wechsel + Off-Limits-Netzwerk-Layer. Siehe [ADR 0002](../adr/0002-ssh-host-key-verification-deferred.md). Braucht ausdrückliche Freigabe.
- **KRIT-3 Schritt 2** — Fallback-Authorization-Policy: könnte `/mcp` brechen; Auth-Middleware Off-Limits.
- **HOCH-12 Teil 2** — secretlose Webhooks ablehnen: braucht Secret-Management-UI, sonst Feature unbrauchbar.

## Mittel & Niedrig — Bean-Abarbeitung

### ServerWatch-ekuc — Docker/SSH-Lifecycle (Client-Invalidation, Tunnel, Cancel, Compose-Ports, mTLS)

- **MIT-16** — instanz-bewusste Client-Invalidierung (`InvalidateClient(serverId, ifCurrent)` via atomarem `TryRemove(KeyValuePair)`), sodass ein Retry keinen frisch aufgebauten Client killt; `ObjectDisposedException` zählt jetzt als Connection-Failure (Retry). _Dateien: `Services/Docker/DockerConnectionManager.cs`, `IDockerConnectionManager.cs`._
- **MIT-17** — SSH-Tunnel-stderr wird für die Lebensdauer im Hintergrund gedraint (redigiert), damit ein voller Pipe-Buffer den „lebendigen" Tunnel nicht einfriert. _Dateien: `Services/Docker/SshTunnelManager.cs`._
- **MIT-18** — externer Cancel (nicht nur Timeout) killt jetzt den Prozessbaum + observed die Read-Tasks; „cancelled"-Result; Timeout-Log redigiert. _Dateien: `Services/Server/HostCommandExecutor.cs`._
- **MIT-19** — Compose-Parser behandelt `ip:host:container` (Bind-IP → `PortBinding.HostIP`, neues `DeploymentRequest.PortBindIps`) und Einzelport; malformte Syntax schlägt laut fehl statt stillem Drop. _Dateien: `Services/Deployment/ComposeFileParser.cs`, `Models/DeploymentRequest.cs`, `Services/Docker/DockerService.cs`._
- **NIED-9** — Port-Wahl + Spawn + Readiness in einer 3-Versuch-Retry-Schleife bei Bind-Race (TOCTOU). _Dateien: `Services/Docker/SshTunnelManager.cs`._
- **NIED-13** (⚠️ off-limits, per ausdrücklicher Freigabe 2026-07-07) — mTLS-Callback prüft zusätzlich den Hostnamen (`cert.MatchesHostname(server.TcpHost)`, `chainOk && hostnameOk`, fail-closed). Test beweist die Ablehnung eines gültig-signierten Certs mit falschem Host. **Deploy-Hinweis:** ein Server-Cert ohne zu `TcpHost` passenden SAN kann sich danach nicht mehr verbinden. _Dateien: `Services/Docker/DockerConnectionManager.cs`._

**Verifikation Bean 8:** Build 0 Fehler; `dotnet test` 136/136 bestanden (neue Tests: `DockerConnectionFailureTests`, `ComposeFileParserPortTests`, `DockerMtlsHostnameTests`); App in Development gebootet — DI-Graph sauber (Interface-Signatur MIT-16 geändert). Prozess-/Tunnel-Pfade (MIT-17/18, NIED-9) durch Build + Boot + Review verifiziert.

## Verifikation

- `dotnet build src/ServerWatch/ServerWatch.csproj` → 0 Fehler.
- `dotnet test src/ServerWatch.Tests` → 123/123 bestanden.
- Kein App-Boot durchgeführt; es wurden keine DI-Registrierungen oder Konstruktor-Abhängigkeiten geändert (DI-Graph unverändert). Vor dem Deploy dennoch einmal booten (Development/ValidateOnBuild).

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

### ServerWatch-vcvv — Optimierungen (OPT-2/4/5/6/7/8/10/11.{1,2,3,4,6,7})

Alle verhaltenserhaltend (regression-safe), je Datei ein Commit.
- **OPT-4/5 (DockerService).** `GetContainerStatsAsync` nutzt statische `JsonSerializerOptions` statt eine pro Aufruf; `RunHostShellAsync` cacht die Host-Shell-Image-Präsenz pro Server (TTL 1h) und drosselt den Leftover-Sweep (≤1×/5 min).
- **OPT-2/11.2 (MetricsCollectorService).** Prune läuft nur noch stündlich (`_lastPrune`-Gate) statt in jedem 30s-Zyklus; der Container-Stats-Fan-out ist mit `SemaphoreSlim(8)` begrenzt.
- **OPT-6 (Nginx/Systemd).** Die je zwei unabhängigen Read-Kommandos in `ListSitesAsync`/`ListServicesAsync` laufen jetzt nebenläufig (der Executor startet pro Call einen eigenen Prozess/Container, SSH multiplext → nebenläufigkeitssicher). Write-/Enable-/Disable-Pfade bleiben sequenziell.
- **OPT-7 (Program.cs).** Registry/Hetzner/Hostinger-Typed-Clients bekommen einen `SocketsHttpHandler` mit `PooledConnectionLifetime = 5min` (rotiert Verbindungen → kein Stale-DNS, auch bei einem captured Client); die doppelte `RegistryClient`-Singleton-Registrierung entfernt (IRegistryClient bleibt Singleton mit geteiltem Cache).
- **OPT-8 (CloudControlService).** `ListAllAsync` gruppiert nach `(Provider, Token)` und listet jeden Account **einmal** statt einmal pro Server (schont das Hetzner-Ratelimit). Match/Map in verbatim ausgelagerte Helfer — gleiches beobachtbares Ergebnis.
- **OPT-10 (LogMonitorService).** Aktive Regex-Regeln werden einmal pro Zyklus kompiliert (Dict nach Pattern) statt pro Logzeile jedes Containers; ungültige Patterns werden hier verworfen statt pro Zeile zu werfen.
- **OPT-11.1/.3/.4/.6/.7.** Container-Memory-History wird wie CPU downsampled; `Cves.FilteredGroups()` einmal pro Render; `InAppNotificationStore` trimmt die persistierte Historie periodisch (nicht bei jedem Insert); der AiChat-Modellkontext kappt das Inventar auf 50 (unhealthy/gestoppt priorisiert); Provider-Fehler zeigen `error.message` statt bloßem Status (neuer, unit-getesteter Helfer `Agent/Providers/ProviderError`).

**Verifikation (Branch `feat/ServerWatch-vcvv-optimizations`):** Build 0 Fehler, `dotnet test` 130/130 (7 neu: `ProviderErrorTests` inkl. Fehlerpfad + No-Secret-Leak). App in Development gebootet — „Application started" (DI-Graph nach dem OPT-7-Registrierungsumbau validiert: Registry/Hetzner/Hostinger lösen sauber auf). Beans-übergreifend: **OPT-11.5** (MaxTokens) → Bean 6, **OPT-1** → Bean 7, **OPT-3/OPT-9** → Bean 12.

## Bewusst zurückgestellt (Begründung im Review-Doc / ADR)

- **HOCH-11** — SSH `StrictHostKeyChecking=no` → `accept-new`: Aussperr-Risiko bei Host-Key-Wechsel + Off-Limits-Netzwerk-Layer. Siehe [ADR 0002](../adr/0002-ssh-host-key-verification-deferred.md). Braucht ausdrückliche Freigabe.
- **KRIT-3 Schritt 2** — Fallback-Authorization-Policy: könnte `/mcp` brechen; Auth-Middleware Off-Limits.
- **HOCH-12 Teil 2** — secretlose Webhooks ablehnen: braucht Secret-Management-UI, sonst Feature unbrauchbar.
- **OPT-12** — `CancellationToken` durch Hetzner/Hostinger fädeln: reine Plumbing-Änderung (~70 Signaturen + 2 Interfaces) ohne aktuellen Aufrufer, der ein Token übergibt. Als fokussierter Follow-up zurückgestellt (2026-07-08); Einstiegspunkt `_http.SendAsync(req, ct)` in den privaten `SendAsync`-Helpern.

## Verifikation

- `dotnet build src/ServerWatch/ServerWatch.csproj` → 0 Fehler.
- `dotnet test src/ServerWatch.Tests` → 123/123 bestanden.
- Kein App-Boot durchgeführt; es wurden keine DI-Registrierungen oder Konstruktor-Abhängigkeiten geändert (DI-Graph unverändert). Vor dem Deploy dennoch einmal booten (Development/ValidateOnBuild).

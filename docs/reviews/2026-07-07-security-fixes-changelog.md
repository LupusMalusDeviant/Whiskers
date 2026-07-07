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

### ServerWatch-4wua — Blazor-UI (XSS, Env-Masking, RBAC, SRI, Timer, Deep-Link, Kleinbugs, Perf)

- **MIT-35** (Security/XSS) — `MarkdownSanitizer.NeutralizeUnsafeHrefs` filtert nicht-`http(s)`/`mailto`/`#`-Hrefs aus gerendertem LLM-Markdown → ein `[x](javascript:…)` ist inert. Nur `Agent.razor` (Markdig); `AiChat.razor` rendert keine Links (safe). _Dateien: `Utils/MarkdownSanitizer.cs`, `Components/Pages/Agent.razor`._
- **MIT-37** (Security) — `EnvMasking.ShouldMask` maskiert sensible Keys UND Werte mit Inline-Credentials (`://user:pass@`, inkl. leerer-User Redis-URIs); Env-Tab hinter `AppRole.Operator`. URL/URI werteseitig (nicht als Blanket-Keywords), damit harmlose Config-URLs sichtbar bleiben. _Dateien: `Utils/EnvMasking.cs`, `ContainerDetail.razor`, `ContainerDiff.razor`._
- **MIT-43(b)** (Security/RBAC) — Live-Admin-Recheck (`CurrentUser.HasRoleAsync`) als erste Zeile von `SaveWhitelist`/`SaveRoles`/`AddVaultSecret`/`AddApiKey` + neue gegatete `DeleteVaultSecret` (Rolle nicht mehr nur per-Circuit gesnapshottet). MIT-43(a) war Teil HOCH-4. _Dateien: `Settings.razor`._
- **MIT-36** (Security/Supply-Chain) — xterm/vis-network/html2canvas lokal nach `wwwroot/js/vendor/` gevendort (kein CDN-Load ohne SRI, air-gapped funktioniert). _Dateien: `App.razor`, `wwwroot/js/vendor/*`._
- **MIT-34** — beide `async void`-Refresh-Timer (Dashboard 5s, ContainerDetail) mit try/catch (Circuit-Teardown) + `Interlocked`-Reentrancy-Guard. _Dateien: `Dashboard.razor`, `ContainerDetail.razor`._
- **MIT-42** — Chart-Deep-Links nutzen `_resolvedServerId` (nicht den Routen-Param) + Reload nach Cross-Server-Discovery. _Dateien: `ContainerDetail.razor`._
- **NIED-20** (1/2/3/4/5/7; 6 war schon done) — Servers `@@@`→`@`; LogViewer toter Follow-Switch entfernt; ContainerDetail CSV-Placeholder aus Accept; AiChat Approval-Session-Fallback; theme-interop Drag-Listener einmalig (kein Leak); Settings MCP-Key → Clipboard statt Toast. _Dateien: `Servers.razor`, `LogViewer.razor`, `ContainerDetail.razor`, `Shared/AiChat.razor`, `theme-interop.js`, `Settings.razor`._
- **OPT-3** — `ChatWidget` Identity-Key-Guard in `OnParametersSetAsync` stoppt den Re-Query-Sturm bei Streaming. _Dateien: `Shared/ChatWidget.razor`._
- **OPT-9** — `IDockerService.GetContainerAsync` (Single-Inspect via id-Filter) für den Detail-Poll + Intervall 5s; Dashboard-Fan-out bereits durch MIT-34-Guard bounded. _Dateien: `IDockerService.cs`, `DockerService.cs`, `ContainerDetail.razor`._

**Verifikation Bean 12:** Build 0 Fehler; `dotnet test` 143/143 (neue Tests: `MarkdownSanitizerTests` inkl. Markdig-Round-Trip, `EnvMaskingTests`); App in Development gebootet — DI-Graph sauber (Interface + `IJSRuntime` + gevendorte Assets laden). UI-/Timer-/JS-Pfade via Build + Boot + Review. **Zurückgestellt:** breiter „leer=unverändert"-Secret-Feld-Retrofit (NIED-20.7 Teil 2) + `StateHasChanged`-only-on-change (OPT-9) — als fokussierter Follow-up.

## Verifikation

- `dotnet build src/ServerWatch/ServerWatch.csproj` → 0 Fehler.
- `dotnet test src/ServerWatch.Tests` → 123/123 bestanden.
- Kein App-Boot durchgeführt; es wurden keine DI-Registrierungen oder Konstruktor-Abhängigkeiten geändert (DI-Graph unverändert). Vor dem Deploy dennoch einmal booten (Development/ValidateOnBuild).

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
### ServerWatch-a3jo — Agent-Internals (Threadsafety, Live-Guardrails, Transcript, Runtime)

- **MIT-31 — Transcript-Sanitize.** `AgentTranscriptStore.SaveAsync` bereinigt das Fenster vor Persist/Re-Seed: führende Tool-Messages + verwaiste Assistant-ToolCalls entfernt (kein Provider-400), Tool-Outputs via `SecretRedactor` redigiert, base64-Screenshots verworfen.
- **MIT-40 — Store-Cache.** Ein `JsonFileStore` pro Pfad (ConcurrentDictionary) → die Semaphore serialisiert parallele Saves wirklich.
- **MIT-32 — Session-Threadsafety.** `_history`-Zugriffe unter Lock; `SendAsync` mit Interlocked-Reentrancy-Guard (zweiter paralleler Send → `Failed`).
- **MIT-33 — Live-Guardrails.** Die Session evaluiert gegen `IGuardrailStore.Current` (Kill-Switch/Limits greifen mid-run); Trigger-Runs behalten ihr gepinntes Preset (optionaler Store, Fallback auf `context.Policy`).
- **MIT-41 — ClaudeCodeRuntime-Härtung** (Stub, noch ohne Caller): `--permission-mode default` + `--disallowedTools Edit,Write,Bash,NotebookEdit`; kein `?? ApiKey`-Fallback (ohne McpKey kein Start); Principal-Level vs. Key-Level geprüft; temp MCP-Config chmod 600.
- **NIED-21 — Agent-Sammelfinding (8):** .1 pending-leak try/finally · .2 Eviction (id,session)-Tupel + ReferenceEquals · .3 ApprovalStore exactly-once (Interlocked) · .4 TargetInvocationException entpackt · .5 Guardrail-Legacy-Migration → SafeDefault statt Default-Policy · .6 AiChat-Save atomar · .7 Anthropic-Seed ohne führende Assistant-Turns · .8 AgentSettingsStore.SaveAsync admin-only.
- **OPT-11.5 — MaxTokens** aus `AgentSettings` (Default 4096) statt hartkodiert 1024.
- _Tests:_ TranscriptStore-Sanitize (Round-Trip), Session-Reentrancy + Live-Policy-Limit, AgentSettings-RequireAdmin; bestehende Agent-Tests bleiben grün. DI per Boot validiert (Ctor/Interface-Changes).

## Bewusst zurückgestellt (Begründung im Review-Doc / ADR)

- **HOCH-11** — SSH `StrictHostKeyChecking=no` → `accept-new`: Aussperr-Risiko bei Host-Key-Wechsel + Off-Limits-Netzwerk-Layer. Siehe [ADR 0002](../adr/0002-ssh-host-key-verification-deferred.md). Braucht ausdrückliche Freigabe.
- **KRIT-3 Schritt 2** — Fallback-Authorization-Policy: könnte `/mcp` brechen; Auth-Middleware Off-Limits.
- **HOCH-12 Teil 2** — secretlose Webhooks ablehnen: braucht Secret-Management-UI, sonst Feature unbrauchbar.

## Mittel & Niedrig — Bean-Abarbeitung

### ServerWatch-4x67 — DB-Persistenz (AlertHistory, Migrations, Metrics-Settings, WAL, Redis)

- **MIT-27** — `AlertHistory` in die Startup-DDL aufgenommen; die 30-s-Prune-Schleife wirft nicht mehr `no such table` und Audit-/MCP-Pruning läuft wieder. _Dateien: `Program.cs`._
- **MIT-29** — EF-Core-Migrations eingeführt (`MigrateAsync` statt `EnsureCreated` + Raw-DDL). `DatabaseInitializer` baselined Legacy-`EnsureCreated`-DBs nicht-destruktiv (heilen → `InitialCreate` stempeln → migrieren), Design-Time-Factory + `Migrations/InitialCreate`, belegt durch `DbMigrationBaselineTests`. Siehe [ADR 0003](../adr/0003-ef-core-migrations-baseline.md). _Dateien: `Program.cs`, `Services/Persistence/DatabaseInitializer.cs`, `MetricsDbContextFactory.cs`, `Migrations/`._
- **MIT-28** — `MetricsCollectorService` liest jetzt `IOptionsMonitor<MetricsSettings>` (Intervall/Retention/Enable, reload-on-change, mit Floors gegen 0/negativ); `Metrics__*`/`MetricAlert__*` in beiden Compose-Dateien gemappt + `METRICS_*`-Block in `.env.example`. _Dateien: `Services/Metrics/MetricsCollectorService.cs`, `docker-compose.yml`, `docker-compose.hardened.yml`, `.env.example`._
- **NIED-22** — Redis „list databases" liefert nummerierte Logik-DBs (`0..N-1`) statt der Anzahl als Name (pure `ParseRedisDatabaseList`). _Dateien: `Services/Database/DatabaseService.cs`._
- **OPT-1** — SQLite WAL (`journal_mode=WAL` + `synchronous=NORMAL`) im `DatabaseInitializer`. _Dateien: `Services/Persistence/DatabaseInitializer.cs`._

**Verifikation Bean 7:** Build 0 Fehler; `dotnet test` 133/133 bestanden; App in Development gebootet — DI-Graph sauber (trotz geänderter `MetricsCollectorService`-Ctor) und der Legacy-Baseline-Pfad lief korrekt gegen eine reale Bestands-Dev-DB. **Deploy-Hinweis:** vor dem ersten migrations-fähigen Deploy eine Kopie von `data/metrics.db` ziehen — nicht-destruktiv per Konstruktion, aber Gürtel-und-Hosenträger.
### ServerWatch-ekuc — Docker/SSH-Lifecycle (Client-Invalidation, Tunnel, Cancel, Compose-Ports, mTLS)

- **MIT-16** — instanz-bewusste Client-Invalidierung (`InvalidateClient(serverId, ifCurrent)` via atomarem `TryRemove(KeyValuePair)`), sodass ein Retry keinen frisch aufgebauten Client killt; `ObjectDisposedException` zählt jetzt als Connection-Failure (Retry). _Dateien: `Services/Docker/DockerConnectionManager.cs`, `IDockerConnectionManager.cs`._
- **MIT-17** — SSH-Tunnel-stderr wird für die Lebensdauer im Hintergrund gedraint (redigiert), damit ein voller Pipe-Buffer den „lebendigen" Tunnel nicht einfriert. _Dateien: `Services/Docker/SshTunnelManager.cs`._
- **MIT-18** — externer Cancel (nicht nur Timeout) killt jetzt den Prozessbaum + observed die Read-Tasks; „cancelled"-Result; Timeout-Log redigiert. _Dateien: `Services/Server/HostCommandExecutor.cs`._
- **MIT-19** — Compose-Parser behandelt `ip:host:container` (Bind-IP → `PortBinding.HostIP`, neues `DeploymentRequest.PortBindIps`) und Einzelport; malformte Syntax schlägt laut fehl statt stillem Drop. _Dateien: `Services/Deployment/ComposeFileParser.cs`, `Models/DeploymentRequest.cs`, `Services/Docker/DockerService.cs`._
- **NIED-9** — Port-Wahl + Spawn + Readiness in einer 3-Versuch-Retry-Schleife bei Bind-Race (TOCTOU). _Dateien: `Services/Docker/SshTunnelManager.cs`._
- **NIED-13** (⚠️ off-limits, per ausdrücklicher Freigabe 2026-07-07) — mTLS-Callback prüft zusätzlich den Hostnamen (`cert.MatchesHostname(server.TcpHost)`, `chainOk && hostnameOk`, fail-closed). Test beweist die Ablehnung eines gültig-signierten Certs mit falschem Host. **Deploy-Hinweis:** ein Server-Cert ohne zu `TcpHost` passenden SAN kann sich danach nicht mehr verbinden. _Dateien: `Services/Docker/DockerConnectionManager.cs`._

**Verifikation Bean 8:** Build 0 Fehler; `dotnet test` 136/136 bestanden (neue Tests: `DockerConnectionFailureTests`, `ComposeFileParserPortTests`, `DockerMtlsHostnameTests`); App in Development gebootet — DI-Graph sauber (Interface-Signatur MIT-16 geändert). Prozess-/Tunnel-Pfade (MIT-17/18, NIED-9) durch Build + Boot + Review verifiziert.
### ServerWatch-zcgp — CVE-Monitor (Scan-Race, Stale-Prune, Locale, Fail-Backoff)

- **MIT-8** — atomares Scan-Gate (`Interlocked.CompareExchange`) statt Bool-Check-then-set → manueller Trigger und Background-Loop können keine überlappenden Voll-Scans mehr fahren; `_store.IsScanning` bleibt nur UI-Indikator. _Dateien: `Services/Cve/CveMonitorService.cs`._
- **MIT-9** — Phantom-Prune: nach den Container-Scans je Server werden gespeicherte Container-Keys, die nicht mehr existieren, entfernt (`PruneServer`, OS-Key nie); löst das unbegrenzte Wachstum in Store/UI/`cve-findings.json` bei Recreates. Persist-Pfad jetzt injizierbar (Testbarkeit). _Dateien: `Services/Cve/CveFindingsStore.cs`, `ICveFindingsStore.cs`, `CveMonitorService.cs`._
- **MIT-13** — beide apt-Kommandos mit `LC_ALL=C.UTF-8` → auf deutschen Hosts nicht mehr stillschweigend 0 Findings. _Dateien: `Services/Cve/OsCveScanner.cs`._
- **NIED-5** — (a) 15-min-Backoff nach Fehlzyklus statt vollem Intervall; (b) `PruneStaleAsync` löscht `CveFirstSeen`-Zeilen, deren Key weg ist UND älter als 30 Tage (temp-SQLite-Test beweist: nur stale+alt). _Dateien: `Services/Cve/CveMonitorService.cs`, `CveAgeStore.cs`._

**Verifikation Bean 9:** Build 0 Fehler; `dotnet test` 128/128 (neue Tests: `CveFindingsStorePruneTests`, `OsCveScannerLocaleTests`, `CveAgePruneTests`); App in Development gebootet, DI-Graph sauber. Keep-Previous-on-Failure (HOCH-7) unverändert — kein falscher „clean"-Zustand.
### ServerWatch-0txk — Background-Loops (False-Restarts, Cron-Death-Loop, Log-Re-Alerts, Notification-Timeouts)

- **MIT-10** — Health-Monitor zählt einen Restart nur noch bei `running` aus einem echten Stop-Zustand (`IsRestart`), und überschreibt den gemerkten State nie mit `unknown` → keine Phantom-Restart-Loop-Alerts bei flappenden SSH-Tunneln (schützt auch die Stop-Erkennung). _Dateien: `Services/HealthMonitor/ContainerHealthMonitor.cs`._
- **MIT-11** — ungültiger Cron deaktiviert den Task (mit Log) statt alle 30s zu werfen (`TryParseCron`); Executor-Fehler deaktivieren den Task NICHT. _Dateien: `Services/Scheduler/SchedulerService.cs`._
- **MIT-12** — Log-Monitor holt nur Zeilen seit dem letzten Check (`since`-Overload auf `GetContainerLogsAsync`, Baseline = now bei Erstsicht) → eine alte ERROR-Zeile re-alarmiert nicht mehr; totes `_logOffsets` entfernt. _Dateien: `Services/Docker/IDockerService.cs`, `DockerService.cs`, `Services/LogMonitor/LogMonitorService.cs`._
- **MIT-15** — 15s-Timeout auf allen Notification-HttpClients + Ein-Mal-Retry in `SafeSend` (testbarer `NotificationRetry`) → ein langsamer Endpunkt blockiert keine Loop mehr ~100s; Log nur Provider-Name. _Dateien: `Program.cs`, `Services/Notifications/CompositeNotificationService.cs`, `NotificationRetry.cs`._
- **NIED-6** — per-Container-Dictionaries (Health/Metrics/Log) werden je Zyklus auf die Live-Menge geprunt; Throttler sweept alte Einträge; AI-Trigger-`_lastRun` cappt bei 1000. _Dateien: `ContainerHealthMonitor.cs`, `MetricsCollectorService.cs`, `LogMonitorService.cs`, `NotificationThrottler.cs`, `AiTriggerDispatcher.cs`._
- **NIED-7** — Scheduler feuert Tasks non-blocking (`Task.Run` + per-taskId In-Flight-Guard), persistiert `NextRun` vor Start (TryAdd nach dem Save → kein Guard-Leak); „Cron = UTC" in der UI. _Dateien: `Services/Scheduler/SchedulerService.cs`, `Components/Pages/ScheduledTasks.razor`._
- **NIED-8** — Throttle-Fenster wird pro Aufruf aus den Live-Settings gelesen statt beim Konstruieren eingefroren (`IsThrottled(…, minutes)`); alle 8 Provider übergeben `ThrottleMinutes`. _Dateien: `NotificationThrottler.cs` + 8 Provider._

**Verifikation Bean 10:** Build 0 Fehler; `dotnet test` 145/145 (neue Tests: `HealthRestartHeuristicTests`, `CronValidationTests`, `NotificationRetryTests`, `NotificationThrottlerTests`); App in Development gebootet — DI-Graph sauber, alle Background-Monitore gestartet. Prozess-/Loop-Pfade (MIT-12, NIED-6/7) via Build + Boot + Review.
### ServerWatch-z07v — Image-Update & Registry (Token-Flow, Digest-Pin, Race, Repeat-Notifications)

- **MIT-23** — registry-agnostischer Bearer-Token-Flow: bei 401 wird der `WWW-Authenticate: Bearer`-Challenge geparst (`ParseBearerChallenge`), ein Token vom Realm geholt und der Manifest-HEAD einmal wiederholt → GHCR/Quay/LSCR melden echten Update-Status statt „could not reach registry". Token wird nie geloggt. _Dateien: `Services/ImageUpdate/RegistryClient.cs`._
- **MIT-24** — Digest-gepinnte Images (`@sha256:`) werden nicht mehr fälschlich als Update gemeldet (`IsDigestPinned` ersetzt den nie feuernden Guard). _Dateien: `Services/ImageUpdate/ImageUpdateChecker.cs`._
- **MIT-25** — der Parallel-Check-Akkumulator ist `ConcurrentBag` statt `List<T>` (keine verlorenen Einträge / kein Growth-Crash). _Dateien: `Services/ImageUpdate/ImageUpdateChecker.cs`._
- **NIED-15** — ein Update benachrichtigt einmal statt jede Runde (Dedup gegen den vorherigen `_store.Get`-State); in AutoUpdate sind Start-/Fehler-Notifications in eigenes try/catch gewrappt, sodass `UpdateHistory.Add` + `SaveChangesAsync` immer laufen. _Dateien: `Services/ImageUpdate/ImageUpdateChecker.cs`, `Services/AutoUpdate/AutoUpdateService.cs`._
- **NIED-16** — alle `JsonDocument`-Instanzen mit `using var` (String vor Dispose extrahiert). _Dateien: `Services/ImageUpdate/RegistryClient.cs`, `Services/AiChat/AiChatService.cs`._

**Verifikation Bean 11:** Build 0 Fehler; `dotnet test` 134/134 (neue Tests: `RegistryChallengeTests`, `ImageUpdatePinTests`); App in Development gebootet, DI-Graph sauber. MIT-25/NIED-15/16 (Loop-/HTTP-/DB-Pfade) via Build + Boot + Review.

## Verifikation

- `dotnet build src/ServerWatch/ServerWatch.csproj` → 0 Fehler.
- `dotnet test src/ServerWatch.Tests` → 123/123 bestanden.
- Kein App-Boot durchgeführt; es wurden keine DI-Registrierungen oder Konstruktor-Abhängigkeiten geändert (DI-Graph unverändert). Vor dem Deploy dennoch einmal booten (Development/ValidateOnBuild).

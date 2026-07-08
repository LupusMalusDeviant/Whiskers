# CLAUDE.md — Whiskers

Kontext für Claude-Code-Sessions in diesem Projekt.

## Projektkontext

- **Stack:** C# / .NET 10 (net10.0), ASP.NET Core 10 (Blazor Server), EF Core 10, MudBlazor 9.2, Docker.DotNet; eingebetteter MCP-Server (ModelContextProtocol) mit ~11 Tool-Gruppen (Container/Server/Network/Database/Monitoring/Log/Scheduler/Cve/Cloud/Hetzner/Agent).
- **Einstiegspunkte:** `src/Whiskers/Program.cs` — Composition-Root: DI, Middleware, MCP-Server-Registrierung, OAuth/OIDC, SignalR-Hubs, Webhook-Endpunkte.
- **Struktur:** `src/Whiskers/` mit Components (Blazor UI), Mcp + Mcp/Tools (MCP-Auth + Tool-Defs), Services (Docker/Auth/Server/Vault/Agent/Mcp/Deployment/Metrics/Terminal/CVE/Scheduler), Hubs (SignalR), Configuration (typed settings), Models, Utils (SecretRedactor, ShellQuoting); `src/Whiskers.Tests/` (xUnit); `deploy/telemetry/` (mTLS-Templates); `docs/`.
- **Tests:** xUnit in `src/Whiskers.Tests/`, run: `dotnet test src/Whiskers.Tests/Whiskers.Tests.csproj`
- **Build:** `dotnet build src/Whiskers/Whiskers.csproj` → `bin/Release/net10.0` (Docker: `/app/publish`)
- **Style-Regeln:** In-Code-Kommentare & XML-Docs auf Englisch (README); Interface-first + DI-Registrierung (CONTRIBUTING); user-facing Strings teils Deutsch; Null-Checks/ArgumentException an Service-Grenzen.
- **Deploy:** Docker-Compose (`docker-compose.yml` / `docker-compose.hardened.yml`) auf Mesh-gebundenem Server (Tailscale/WireGuard), Bring-up via `entrypoint.sh`; kein automatisiertes CD (nur Dependabot). Push/Merge/Deploy bleibt manuell.
- **Kritische Bereiche:** (1) MCP-Command/Query-Ausführung auf Zielservern (HostCommandExecutor via nsenter/SSH/mTLS, execute_command/execute_query, DatabaseService); (2) Auth & Secrets/Vault (OAuth-Whitelist, MCP-Bearer + per-Key-RBAC, VaultService, CloudApiKey/SSH-Keys, SecretRedactor); (3) Cloud-/Hetzner-Destruktiv-Ops (cloud_hard_reset/shutdown/reboot, hetzner_delete_snapshot/disable_backups).
- **Off-Limits:** Auth-Middleware & OAuth-Whitelist (Middleware-Reihenfolge, Fail-Closed, Cookie-Validierung) und der mTLS/WireGuard-Netzwerk-Layer + docker-socket-proxy Verb-Whitelist — nie ohne ausdrückliche Erlaubnis ändern/aufweichen.
- **Qualitätsprioritäten:** Security (oberste Priorität), dann Wartbarkeit & Lesbarkeit.
- **Artifact-Backend:** beans (prefix: `Whiskers-`)

## Sicherheits-Leitplanken (aus dem Repo-Audit 2026-07-04)

Diese Lektionen sind in `.claude/lessons-learned.md` verankert und werden bei jedem Session-Start eingeblendet — vor dem Handeln lesen:
- **Agenten bestätigen niemals Write/Admin automatisch.** Keine Auto-Confirms, keine synthetischen Admin-Principals; RBAC auf JEDEM Pfad (auch MCP/extern) prüfen.
- **Keine selbstgebaute/unauth. Krypto** (kein AES-CBC ohne MAC, kein Key neben dem Ciphertext). AES-GCM oder die .NET Data Protection API.
- **Uploads** serverseitig über MIME + Magic-Bytes prüfen; `Html.Raw` nur nach serverseitiger Sanitization.

## Factory Skills

Factory generiert von `/factory-init` — 2026-07-04.

| Skill | Aufruf | Zweck |
|-------|--------|-------|
| grill | `/grill <problem>` | Diagnose & Konsultation |
| plan | `/plan <bean-id oder idee>` | Feature-Planung |
| refine | `/refine <bean-id>` | Plan vertiefen, Dateipfade, Signaturen (+ eval-bean-Gate) |
| implement | `/implement <bean-id>` | Branch + Commits + Implementierung (eval-bean-Preflight) |
| review | `/review [branch]` | Multi-Angle: parallele Subagents (security / correctness / over-engineering), OWASP-Pflicht-Pass |
| test | `/test <modul>` | Tests im Projekt-Stil generieren |
| doc | `/doc [adr\|arch\|changelog\|code]` | Alle Docs-Formate |
| learn | `/learn` | Lessons aus Transcripts extrahieren (Lernschleife) |
| evolve | `/evolve` | Wiederkehrende Lessons (≥3) zu CLAUDE.md-Regeln verdichten (REVIEW_REQUIRED) |
| status | `/status` | Read-only Pipeline-Übersicht + Empfehlung für den nächsten Schritt |

Artifact-Backend: beans (prefix: `Whiskers-`)

Pipeline: /grill → /plan → /refine → /implement · /review · /test · /doc · /learn · /evolve · /status

**Autonomie & Lernen:**
- **auto-drive** (Stop-Hook) nudged sichere Schritte selbst: offene Lessons → `/learn`, Plan ohne Refined Plan → `/refine`. **Stufe 1 (Default) stoppt vor `/implement`** — Code schreiben bleibt manuell. **Stufe 2 (opt-in):** `export FACTORY_AUTO_IMPLEMENT=1` + APPROVED `PLAN_SUMMARY.md` + refined Bean auf sauberem `main` → auto-drive nudged auch `/implement`. Push/Merge bleibt IMMER manuell. Off-Switch: `touch .claude/auto-drive.off`.
- **Lessons-Loop:** Fehler landen via `.claude/scripts/record-lesson.sh` in `.claude/lessons-learned.md`, werden bei SessionStart/PreCompact eingeblendet. `/learn` mined beendete Sessions (queue-transcript.sh sammelt sie).
- **eval-bean:** `.claude/scripts/eval-bean.sh` lintet Bean-Qualität (in refine/implement verdrahtet).
- **gate-guard** (PreToolUse) blockt destruktives git (push/reset --hard/branch -D/clean -f/force). Off-Switch: `touch .claude/gate-guard.off`.
- **Model-Policy:** `.claude/skills/rules/model-policy.md` — Subagents laufen auf günstigeren Tiers wo sinnvoll (Explore → haiku, Reviewer → sonnet); alles andere erbt das Session-Modell.

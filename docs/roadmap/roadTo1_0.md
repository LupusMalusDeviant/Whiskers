# roadTo1_0.md — Masterplan Whiskers 1.0

> **Zweck:** Einstiegspunkt für die Abarbeitung durch Opus 4.8 (oder andere Modelle). Verlinkt alle Roadmap-Dokumente, definiert Reihenfolge, Abhängigkeiten und die verbindlichen Arbeitsregeln. **Vor jedem Arbeitspaket: dieses Dokument + das jeweilige Detaildokument VOLLSTÄNDIG lesen.**
>
> Stand der Analyse: 2026-07-09, Whiskers v0.11.0 (Commit 70ec72b). Alle Aussagen in den Detaildokumenten sind gegen den Code verifiziert — bei Widersprüchen zwischen Dokument und Code gilt der Code; Abweichung kurz im PR notieren.

---

## 1. Die Dokumente

| Dokument | Inhalt | Charakter |
|---|---|---|
| [changeme.md](changeme.md) | Was ist intern schlecht gelöst (C1–C18) + offene Review-Altlasten (HOCH-11 etc.) | Aufräumliste, viele Voraussetzungen für den Rest |
| [stableDB.md](stableDB.md) | PostgreSQL als zweiter DB-Provider neben SQLite | In sich geschlossenes Paket, 8 Schritte |
| [RoadToSAP.md](RoadToSAP.md) | Modul-Framework: Core + an-/abschaltbare Module, Interface-first | Größte strukturelle Initiative, Phase 0–1 für 1.0 |
| [outOfTheBox.md](outOfTheBox.md) | Setup-Wizard, Release-Image, Install-Script, geführtes Onboarding (W1–W4) | KMU-Adoption, UX |
| [kubernetesImplement.md](kubernetesImplement.md) | Track A: Helm-Chart (Whiskers AUF K8s) · Track B: k3s-Workloads verwalten | Track A klein, Track B großes Seam-Refactoring |
| [missingFeatures.md](missingFeatures.md) | Feature-Lücken vs. Portainer/Coolify (F1–F12 + P2-Liste) | Backlog mit Skizzen |

## 2. Abhängigkeitsgraph (was blockiert was)

```
changeme C1 (DataPaths) ──┬─→ stableDB.md ──┬─→ changeme C7 (Stores→DB)
changeme C11 (healthz)  ──┤                 └─→ k8s Track A Postgres-Option
                          ├─→ k8s Track A (Helm) ←── outOfTheBox W2 (ghcr-Image)
                          └─→ outOfTheBox W2 (Installer wartet auf /healthz)

missingFeatures F1 (lokale Auth) ──→ outOfTheBox W1 (Wizard) ──→ W2 ──→ W3
changeme C5/C6 (Auth-Hotfix, MCP-Key) ─┘   [C5+F1+W1-Schritt2 = EIN Auth-Block, braucht User-Go]

RoadToSAP Phase 0 ──→ Phase 1 (Module; enthält changeme C2, C8, C9, C10, Settings-Split)
changeme C3 (DockerService-Split, empfohlen) ──→ k8s Track B Schritt 1 (Workload-Seam) ──→ B2 ──→ B3
missingFeatures F2 (i18n) — unabhängig, so früh wie möglich (Wizard-/NavItem-Strings brauchen es)
missingFeatures F11 (Webhook-Secrets) ──→ F5 (Git-Deploy)
```

## 3. Empfohlene Abarbeitungsreihenfolge (Wellen)

Jede Welle ist in sich deploybar. Innerhalb einer Welle sind die Pakete weitgehend parallelisierbar (getrennte Branches, kleine PRs).

**Welle 1 — Fundament (klein, sofort):**
1. `changeme` C1 (DataPaths) · C11 (healthz/readyz + graceful shutdown) · C6 (MCP-Key nicht ins Log)
2. `missingFeatures` F2 starten (i18n-Infrastruktur + erste Seiten; läuft ab hier als Daueraufgabe parallel)
3. `RoadToSAP` Phase 0 (Helper, IInitializable, NavItem-Gerüst)

**Welle 2 — Datenbank & Struktur:**
4. `stableDB.md` komplett (Schritte 1–8)
5. `RoadToSAP` Phase 1, Module 1–4 (Terminal → Notifications → mechanische Module → host-management)

**Welle 3 — Zugang & Auftritt (enthält den Auth-Block → User-Go einholen!):**
6. Auth-Block: F1 (lokale Auth + 2FA) + `changeme` C5 + `outOfTheBox` W1 (Wizard) — **ein zusammenhängender, vorab freizugebender Strang**
7. `outOfTheBox` W2 (Release-Pipeline, ghcr-Image, install.sh) + W3 (geführtes Onboarding)
8. `kubernetesImplement` Track A (Helm-Chart)

**Welle 4 — Module fertig & K8s-Verwaltung:**
9. `RoadToSAP` Phase 1, Module 5–8 (CVE, CloudControl inkl. OPT-12, ImageUpdate inkl. C12-Rollback, Agent)
10. `changeme` C3 (DockerService-Split) → `kubernetesImplement` Track B Schritte 1–2 (Seam + K8s-Provider: List/Logs/Exec)

**Welle 5 — Vergleichstest-Features (Reihenfolge nach Lust/Nachfrage):**
11. **F3 (Self-Backup) ✓** (`9b10bba`) · F11→F5 (Webhook-Secrets → Git-Deploy) · F6 (Events) · F7 (Resource-Limits) · F8 (Registries) · F9 (Tags) · F12 (Light Mode) · F10 (MCP-Katalog-Doku) · k8s Track B Schritt 3
12. Rest von `changeme` (C4, C13, C14a, C15, C16, C17-Ausbau) kontinuierlich einstreuen

**Jederzeit, sobald User-Go vorliegt:** `changeme` A-Block (HOCH-11, KRIT-3 Step 2, NIED-1) gebündelt.

**1.0-Schnitt (Definition):** Wellen 1–4 komplett + aus Welle 5 mindestens F3, F11, F12. Alles danach ist 1.x.

## 4. Verbindliche Arbeitsregeln (How to)

Diese Regeln stammen aus CLAUDE.md, den Projekt-Memories und den Review-Prozessen — sie sind NICHT optional:

1. **Interface-first:** Jeder neue Service bekommt ein Interface (`IFoo` → `Foo`), Registrierung dagegen. Konsumenten injizieren das Interface.
2. **Per-Folder-READMEs:** Jeder PR, der einen Ordner inhaltlich ändert, aktualisiert dessen README. Neue Ordner bekommen eins. Der User prüft das aktiv.
3. **DI-Boot-Gate:** Nach jeder Änderung an Registrierungen/Interfaces die App im Development-Mode mit `ValidateOnBuild`/`ValidateScopes` booten — Build + Unit-Tests allein fangen DI-Graph-Fehler nicht.
4. **Keine Claude-Attribution in Commits** (kein `Co-Authored-By`, kein „Generated with“). Konventionelle, sachliche Commit-Messages wie die bestehende Historie.
5. **Sprachen:** Code-Kommentare Englisch. UI-Strings Deutsch — bzw. nach F2 über `IStringLocalizer` (de vollständig, en Default). Doku für Endnutzer Englisch (README), interne Planungsdokumente Deutsch.
6. **Off-Limits-Zonen:** Auth-Middleware/-Reihenfolge und die SSH-Verbindungsschicht NICHT ohne explizites User-Go ändern (ADR-0002, NIED-25.2). Bei Auth-Arbeit (F1/C5/W1) vorab Freigabe einholen und den Scope schriftlich abgrenzen.
7. **DB-Safety:** Vor jeder destruktiven Daten-/Migrationsoperation nachweisbares Backup; Migrationen niemals Daten stillschweigend verwerfen; Umzüge (SQLite→Postgres, JSON→DB) lassen die Quelle unangetastet zurück.
8. **Deploy-Realität:** Produktion läuft als `/opt/ServerWatch` auf Badwolf; Runtime-IDs bleiben `serverwatch` (Rebrand betrifft sie nicht). Die gitignorierte Override-Datei mit Bind-Mount schützt die Daten beim Rebuild — bei Deploy-Änderungen zuerst prüfen, dass sie intakt bleibt. Nichts direkt produktiv testen, was einen Verbindungsabriss erzeugen kann (Erinnerung: Reboot/DNS-502-Historie).
9. **PR-Größe:** Ein Arbeitspaket = ein PR = eine Sache. Verschieben und Refactoring nie mischen (siehe RoadToSAP §4). Jeder PR endet mit: Build grün, Tests grün, DI-Boot-Gate, Smoke der betroffenen Seite, README-Sync.
10. **Tests zuerst dort, wo Strings zu Befehlen werden:** Alles, was über `IHostCommandExecutor` Shell-Kommandos baut, bekommt Command-Building-Tests (Injection-Schutz ist bereits einmal auditiert worden — Regressionen wären peinlich).

## 5. How NOT to (die häufigsten vorhersehbaren Fehler)

- **Nicht** mehrere Detaildokumente in einem Branch mischen („wo ich schon mal dran bin“) — die Dokumente sind bewusst als getrennte Pakete geschnitten.
- **Nicht** Entscheidungen neu aufrollen, die in den Dokumenten als getroffen markiert sind (ein DbContext statt zwei; Single-Replica für 1.0; MCP-first statt REST; statische Modul-Liste statt Reflection; kein LDAP; kein Multi-Tenant). Wenn ein Dokument sagt „Entscheidung: X“, ist das die Vorgabe.
- **Nicht** bei Unklarheit raten, ob etwas Auth-/SSH-Off-Limits berührt — im Zweifel stoppen und fragen.
- **Nicht** `InitialCreate`-Migration anfassen (Name/Inhalt) — bricht bestehende Installationen (stableDB §3 Schritt 1).
- **Nicht** Feature-Parität mit Portainer als Selbstzweck jagen — die P2-Liste in missingFeatures ist bewusst NICHT für 1.0; die Differenzierer (MCP/Agent, Zero-SSH, CVE, Cloud-OOB) haben bei Zielkonflikten Vorrang.
- **Nicht** Secrets in Logs, Beispiel-Configs oder Tests (SecretHygieneTests erweitern, wenn neue Secret-Pfade entstehen).
- **Nicht** vergessen, dass `docs/roadmap/`-Dokumente lebende Dokumente sind: Nach Abschluss eines Pakets die Checkbox im jeweiligen Dokument abhaken und Abweichungen dokumentieren.

## 6. Fortschritts-Tracking

In jedem Detaildokument stehen Definition-of-Done-Checklisten. Zusätzlich hier der Wellen-Status (bei Abschluss aktualisieren):

- [x] **Welle 1 — Fundament KOMPLETT** (2026-07-09): **C1 ✓** (`refactor/c1-datapath-options`), **C11 ✓** = C11a `feat/c11a-health-endpoints` + C11b `feat/c11b-graceful-shutdown`, **C6 ✓** (`fix/c6-mcp-key-not-logged`, +McpPermissionService-Nachzug), **SAP Phase 0 ✓** (`feat/sap-phase0-scaffolding`: DI-Helper + IInitializable-Loop + inertes Modules-Gerüst), **F2-Start ✓** (`feat/f2-i18n-start`: i18n-Infrastruktur + Login-Pilot; voller UI-Sweep bleibt Daueraufgabe) · alles fast-forward in **lokalem `main`** (ungepusht — Push bleibt manuell) · build + 298 Tests + Boot-Gate grün
- [x] **Welle 2 — KOMPLETT** (2026-07-10): **stableDB ✓** (`feat/stabledb-postgres`, Steps 1–8, ADR-0004, PG auf Badwolf bewiesen) · **SAP Phase 1 KOMPLETT** — nicht nur Module 1–4, sondern ALLE 12 Feature-Module + §6 DoD + C8/C9 auf origin/main (siehe project_sap_phase1_modules). Alles auf Badwolf deployt (2026-07-10, `84eee4a`).
- [x] **Welle 3 — KOMPLETT** (2026-07-11): **Auth-Block ✓** (2026-07-10, User-Go erteilt): **C5** (`2986a6f`) + **F1** (`376825c`) + **W1** (`a9d5ffa`). **W2 ✓** (`84eee4a`; Pipeline live, KEIN v*-Tag → Image erst beim ersten Release; csproj dann 0.11→0.12 bumpen). **W3 ✓** (`425af6a`, 2026-07-11: Dashboard-Empty-State-Karte mit 2 Wegen + Deep-Links `servers?add=onboard|classic`, Tailscale-Vorabfrage VOR dem Onboarding, OnboardingService → schrittgetracktes `OnboardingResult` mit Klartext-Hinweisen + Retry/Resume (idempotente Schritte) + `OnboardingCommands` extrahiert mit 19 Command-Building-Tests + Tailnet-IP-Validierung, „Produktionsreif?"-Checkliste in Settings via `IProductionReadinessService`). **Helm Track A ✓** (`eda50e7`, siehe Welle 4). **NB:** Alles ab C5/F1/W1 ist auf origin/main aber NOCH NICHT auf Badwolf deployt (Badwolf bei `f093032`).
- [x] **Welle 4 — KOMPLETT** (2026-07-11) — SAP Module 5–8 ✓ (main + Badwolf). **C3 ✓** (`db84e36`: DockerService 1127→128-Zeilen-Fassade + 6 interne Operations-Kollaborateure, byte-verbatim, Logger-Kategorie + geteilter MemoryCache erhalten). **K8s Track B 1 ✓** (`ae7b206`: IWorkloadProvider-Seam + Capability-Flags + DockerWorkloadProvider + Factory + FakeWorkloadProvider). **Track B 2 ✓** (`6bf3a7b`: KubernetesWorkloadProvider — kubeconfig NUR verschlüsselt im Vault, Pods im Dashboard mit Owner-Gruppierung über das Compose-Label, ehrliche Scale/Rollout-Semantik, self-healing Client-Cache, Docker-Pfad-Guards, Servers-UI-Typ „Kubernetes-Cluster", RBAC-Manifest deploy/k8s/; **Exec + MCP-Tools = Track B.3/1.x**; noch NICHT gegen echten Cluster getestet — kein Cluster auf der Dev-Box). **Track A ✓** (`eda50e7`: Helm-Chart deploy/helm/whiskers, single-replica/Recreate, restricted PodSecurity, V4 local-Server-Gate + `WHISKERS_DISABLE_LOCAL_DOCKER`, helm-Job in release.yml → OCI ghcr.io/…/charts, chart-ci.yml mit Render-Invarianten; **kind/k3s-Live-Install steht aus** → manueller chart-ci-Job nach erstem Release).
- [x] **Welle 5 (1.0-Pflicht) — KOMPLETT** (2026-07-11): **F3 ✓** (`9b10bba`) · **F11 ✓** (`cf4f556`: Secret-Pflicht serverseitig 256-bit, fail-closed Trigger, One-Time-Anzeige + Regenerate, signierter UI-Test, Boot-Migration deaktiviert secret-lose Alt-Webhooks + Admin-Notification; dabei die nie verdrahtete `IWhiskersModule.InitializeAsync`-Schleife in `RunWhiskersStartupAsync` verdrahtet) · **F12 ✓** (`27d2a11`: Light Mode via `data-mode`-CSS-Block + volle PaletteLight, Hell/Dunkel/System-Toggle mit OS-Preference-Watch, Persistenz `sw-mode`). Kür (F5/F6/F7/F8/F9/F10, B Schritt 3) = 1.x.
- [x] **A-Block KOMPLETT** (User-Go: 2026-07-11 „alles der Reihe nach", Commit `23e691b`): **HOCH-11** = `accept-new` + `UserKnownHostsFile=<data>/ssh-keys/known_hosts` auf allen 3 SSH-Pfaden, zentral in `SshHostKeyPolicy`; Flotten-Host-Keys (10 Hosts, Tailnet+Legacy) vor dem Rollout per ssh-keyscan auf Badwolf geseedet; live verifiziert (Verbindung kommt bis zur Tailscale-SSH-Userauth, kein Host-Key-Fehler). **KRIT-3 Step 2** = Fallback-Authorization-Policy (RequireAuthenticatedUser) mit expliziten Anonymous-Opt-outs + Default-`[Authorize]` via `_Imports.razor` + gezielter `/_blazor`-Framework-Exempt (ohne den verlieren Login/Setup die Interaktivität — im Browser verifiziert); Härtungs-Nebeneffekt: SignalR-Hub anonym jetzt 401. **NIED-1** = Bearer case-insensitiv an ALLEN 4 Parse-Stellen (nicht nur der einen aus dem Review). ADR-0002 → UMGESETZT.
- [x] **Release v0.12.0 VERÖFFENTLICHT** (2026-07-11): csproj 0.12.0, Badwolf deployt (mehrfach, zuletzt inkl. A-Block; Healthcheck-Port-Mismatch in der Override-Datei gefixt → Container „healthy"), Tag `v0.12.0` → Pipeline **success** (Trivy-Gate bestanden!): ghcr-Image multi-arch + `latest`, Helm-Chart als OCI (`oci://ghcr.io/lupusmalusdeviant/charts/whiskers`), GitHub-Release mit Assets. **kind-Smoke-Job manuell ausgeführt → success** (helm install mit echtem Image, /readyz ok) → Track-A-DoD Punkt 1 (kind) ✓. CHANGELOG.md + Issue-/PR-Templates + CoC auf main. Gotcha: `aquasecurity/trivy-action@0.28.0` existierte nicht mehr → `@v0.36.0` (Tags sind jetzt v-prefixed). **Noch offen für „1.0":** W1-Wizard-Stoppuhr auf frischer VM, k3s-Livetest des K8s-Providers, Website-Update/Ankündigung.
- [x] **Kür (2026-07-11):** F5 Git-Deploy ✓ (`2e4d809`) · F8 Registries v1 ✓ (`6e70b38`) · bUnit-Pilot ✓ (`a59b452`, HealthBadge, 6 Tests, MudBlazor-KeyInterceptor-Workaround dokumentiert) · Audit-Lücken-Sweep ✓ (`db7828c`: Scheduler+Webhooks-UI) · i18n-Block 2 ✓ (`2779fc4`: komplette Nav + Chrome über Localizer, EN default/DE via Accept-Language E2E-verifiziert). Testbaseline: **549/551**.
- [x] **Release v0.12.1 (2026-07-11):** Patch-Release, bündelt alles nach dem 0.12.0-Tag: **A-Block-Security** (TOFU-Host-Keys, Fail-Closed-Auth, Bearer case-insensitiv — die 0.12.0-Artefakte enthielten das noch NICHT, erst ab 0.12.1 im Image), F5 Git-Deploy, F8 Registries, i18n-Nav, Audit-Sweep, CI-Workflow, kompletter Dependency-Batch (MudBlazor 9.7, YamlDotNet 18, Npgsql 10.0.3, MCP 1.4.1, docker-Actions v4/v7 — dieser Tag ist der erste Pipeline-Lauf mit den Actions-Majors). CHANGELOG entsprechend umsortiert.

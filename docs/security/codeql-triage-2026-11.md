# CodeQL-Triage vor 1.0 (November 2026)

Stand: `main` 4d7acc13, 62 offene Alarme der Code-Scanning-Analyse (`security.yml`, Job `codeql`, Suite
`security-extended`, C# buildless). Jeder Alarm wurde am Code geprüft. Behoben auf Branch `fix/codeql-triage`.
Auf GitHub ist nichts geschlossen oder verworfen worden; das entscheidet der Maintainer nach dieser Liste.

## Ergebnis

| Regel | Alarme | echt | Fehlalarm | Mittel |
|---|---:|---:|---:|---|
| `cs/log-forging` | 29 | 27 | 2 | zentral im Logger (`RedactingLoggerFactory` → `LogSafe.OneLine`), dazu `AuditLogService` an der Stelle |
| `cs/exposure-of-sensitive-information` | 26 | 18 | 8 | zentral im Logger (`LogSafe.Addresses`), dazu `LogSafe.Account`/`LogSafe.Value` an den Stellen, die ein Konto loggen |
| `cs/cleartext-storage-of-sensitive-information` | 4 | 4 | 0 | **behoben im Datenfluss:** das DB-Passwort steht in keinem Befehl mehr; der Befehl nennt die Variable (`docker exec -e PGPASSWORD`), der Wert geht über `HostCommandSecrets` beiseite (Umgebung bzw. stdin) |
| `cs/web/cookie-secure-not-set` | 3 | 2 | 1 (#86, trotzdem gehärtet) | **behoben** nach Freigabe des Nutzers (2026-11-10): OIDC-Nonce/-Correlation ohne HTTPS-Metadaten `SameAsRequest`; Sprach-Cookie `Secure` nach Schema, `HttpOnly`, `Lax` |

„Echt“ heisst: ein Wert, der von aussen kommt (Tool-/HTTP-Argument, Webhook-Pfad, Ausgabe eines Hosts, Konto-Adresse),
erreichte das Anwendungs-Log ohne Bereinigung. Die meisten Log-Forging-Fälle sind in der Praxis schwer auszunutzen
(Server-IDs, Containernamen), aber nicht nachweislich an jeder Stelle gegen eine Erlaubnisliste geprüft — deshalb
konservativ als echt gewertet und zentral behoben.

## Das gemeinsame Mittel

`src/Whiskers/Utils/LogSafe.cs`:

- `OneLine(value)` — CR, LF, NEL, U+2028, U+2029 werden zu Leerzeichen (vorher `AuditLogService.OneLine`, das jetzt
  delegiert).
- `Account(account)` — Pseudonym `[account:1a2b3c4d]`: HMAC-SHA256 mit einem Schlüssel, der pro Prozess zufällig
  entsteht und nirgends gespeichert wird. Innerhalb eines Laufs gleich (Zeilen eines Kontos gehören zusammen), nicht
  umkehrbar, auch nicht per Wörterbuch.
- `Addresses(text)` — jede Adresse `name@host.tld` im Text wird zu ihrem Pseudonym; `nginx@sha256:…` und
  `root@10.0.0.1` bleiben.
- `Value(value)` = `OneLine(Addresses(value))`.

`RedactingLoggerFactory` (INV-51, steht vor jedem Logger der App) wendet das an: jeder strukturierte Wert geht
einzeilig und ohne Adresse an die Provider; Nachricht, Scope und Ausnahmetext ohne Adresse. Das Template selbst behält
seine Zeilenumbrüche (es ist Code). Damit ist jede heutige und künftige Logstelle abgedeckt, auch die, die CodeQL
nicht gefunden hat.

**Was CodeQL davon sieht.** CodeQL kennt die Logger-Fassade nicht als Bereinigung. Die zentral behobenen Stellen
bleiben deshalb voraussichtlich als Alarm stehen. Empfehlung: nach dem nächsten Lauf auf `main` die verbliebenen
Log-Forging- und Exposure-Alarme dieser Liste mit „Won't fix“ und dem Verweis „zentral bereinigt in
RedactingLoggerFactory/LogSafe, Test `LogSafeTests`“ schliessen. An den Stellen, die direkt `LogSafe.OneLine` /
`LogSafe.Value` aufrufen (`String.Replace` im Pfad), erkennt CodeQL die Bereinigung für Log-Forging; für Exposure gibt
es in der Standardabfrage keinen Sanitizer — diese Alarme werden voraussichtlich ebenfalls zum Schliessen anstehen.
Unterdrückungskommentare im Code wurden **nicht** gesetzt.

Das Audit-Log in der Datenbank ist unverändert: die Zeile behält Adresse, Ziel und Details wie bisher (Test
`An_audit_row_keeps_the_address_and_its_log_line_neither_names_it_nor_breaks`).

## Alarme im Einzelnen

### cs/log-forging (29)

| # | Ort | Urteil | Begründung | Massnahme |
|---|---|---|---|---|
| 87 | `Services/Mutations/ComposeRunner.cs:555` | echt | `serverId` aus Tool-/HTTP-Argument, im Fehlerpfad geloggt (auch für eine unbekannte ID) | zentral |
| 84 | `Services/AuditLog/AuditLogService.cs:95` | echt | Akteur, Aktion, Zieltyp, Zielname eines Audit-Eintrags (Zielname kann Freitext sein) ungefiltert | `LogSafe.Value` je Wert + zentral |
| 50 | `Services/AuditLog/AuditLogService.cs:120` | echt | Notfall-Zeile bei ausgefallener Audit-DB: Werte nur redigiert, nicht einzeilig | `LogSafe.Value` je Wert + zentral |
| 49 | `Services/AuditLog/AuditLogService.cs:120` | echt | wie #50 (anderer Pfad) | wie #50 |
| 48 | `Services/AuditLog/AuditLogService.cs:120` | echt | wie #50 (anderer Pfad) | wie #50 |
| 54 | `Services/Notifications/CompositeNotificationService.cs:90` | echt | `ContainerName`/`EventType` eines Ereignisses (auch aus Webhook/Trigger), Debug-Level | zentral |
| 53 | `Services/Mutations/MutationGateway.cs:1040` | echt | `OperationId` kann vom Aufrufer kommen (Idempotenzschlüssel, INV-39) | zentral |
| 52 | `Services/Mutations/MutationGateway.cs:833` | echt | wie #53 | zentral |
| 51 | `Services/Mutations/MutationGateway.cs:482` | echt | wie #53 | zentral |
| 47 | `Services/Agent/Triggers/AiTriggerDispatcher.cs:206` | echt | Trigger-Name ist vom Nutzer frei gewählt | zentral |
| 46 | `Services/Webhooks/WebhookService.cs:378` | echt | `webhookId` aus dem Pfad eines **nicht authentifizierten** Aufrufs (nur Länge ≤ 64 geprüft) | zentral |
| 45 | `Services/Server/SystemdService.cs:46` | echt | `result.Error` = stderr des Hosts, mehrzeilig | zentral |
| 44 | `Services/Server/NginxService.cs:81` | echt | `siteName` (Argument) und stderr des Hosts | zentral |
| 43 | `Services/Server/SslCertService.cs:125` | echt | `serverId` aus Argument (Zähler ist eine Zahl) | zentral |
| 42 | `Services/Server/SslCertService.cs:111` | echt | `dateStr`/`CertName` aus der Ausgabe von certbot auf dem Host | zentral |
| 41 | `Services/Server/SslCertService.cs:53` | echt | stderr des Hosts | zentral |
| 40 | `Services/Server/FirewallService.cs:50` | echt | stderr des Hosts | zentral |
| 39 | `Services/Observability/Correlation/RequestCorrelationMiddleware.cs:32` | **Fehlalarm** | `RequestCorrelation.Choose` übernimmt den Header nur nach `IsAcceptable` (1–64 Zeichen `A–Z a–z 0–9 . _ -`), sonst Trace-ID (Hex) oder neue GUID | keine |
| 38 | `Services/Docker/SshTunnelManager.cs:170` | **Fehlalarm** | geloggt nur nach erfolgreichem `TryRemove`: die ID ist Schlüssel eines bestehenden Tunnels, also eine konfigurierte Server-ID | keine (zentral ohnehin abgedeckt) |
| 37 | `Services/Docker/Operations/ContainerOperations.cs:305` | echt | `containerId` aus Argument, im Fehlerpfad | zentral |
| 36 | `Services/Docker/DockerConnectionManager.cs:293` | echt | Server-ID aus Argument, nicht an dieser Stelle gegen die Konfiguration geprüft | zentral |
| 35 | `Services/Docker/DockerConnectionManager.cs:288` | echt | wie #36 | zentral |
| 34 | `Services/Docker/DockerConnectionManager.cs:235` | echt | wie #36 | zentral |
| 33 | `Services/Docker/Budget/ServerBudget.cs:170` | echt | wie #36 (Spur wird pro übergebener ID angelegt) | zentral |
| 32 | `Services/Docker/Budget/ServerCircuitBreaker.cs:238` | echt | wie #36 | zentral |
| 31 | `Services/Docker/Budget/ServerCircuitBreaker.cs:213` | echt | wie #36 | zentral |
| 30 | `Services/Docker/Budget/ServerCircuitBreaker.cs:138` | echt | wie #36 | zentral |
| 29 | `Services/Docker/Budget/ServerCircuitBreaker.cs:91` | echt | wie #36 | zentral |
| 28 | `Services/Docker/Budget/ServerCircuitBreaker.cs:62` | echt | wie #36 | zentral |

### cs/exposure-of-sensitive-information (26)

CodeQL hält jeden Bezeichner mit „Email“ im Namen für private Daten — auch Konstanten wie
`AuthAuditReasons.NoEmail = "no_email"`. Solche Grund-Codes sind keine Adressen.

| # | Ort | Urteil | Begründung | Massnahme |
|---|---|---|---|---|
| 91 | `Services/Mutations/AutomationMutationRunner.cs:473` | echt | `OwnerId` ist `user:<adresse>` | `LogSafe.Value` + zentral |
| 90 | `Services/Mutations/AutomationMutationRunner.cs:429` | echt | wie #91 | `LogSafe.Value` + zentral |
| 89 | `Services/Guardrails/GuardrailStore.cs:511` | echt | `editor.DisplayName` ist bei einer Person die Adresse (Quelle im Alarm ist eine Konstante, der Fluss gilt für jede Person) | `LogSafe.Value` + zentral |
| 88 | `Services/Guardrails/GuardrailStore.cs:424` | echt | wie #89 | `LogSafe.Value` + zentral |
| 81 | `Services/AuditLog/AuditLogService.cs:95` | echt | Akteur einer Person bzw. Ziel `setup.first_admin` (kein `auth.*`-Eintrag) ist eine Adresse | `LogSafe.Value` + zentral |
| 80 | `Services/AuditLog/AuditLogService.cs:95` | echt | wie #81 (anderer Pfad) | wie #81 |
| 79 | `Services/Auth/AuthAuditLog.cs:105` | **Fehlalarm** | Konto ist schon `[account]`; geloggt wird der Grund-Code `no_email` | keine |
| 78 | `Services/AuditLog/AuditLogService.cs:107` | **Fehlalarm** | `auth.*`-Zweig: Konto ist `[account]`; Details enthalten Grund-Codes `nonstandard_email_claim`/`email_unverified` | keine |
| 70 | `Services/Auth/IdentityBindingStore.cs:340` | **Fehlalarm** | geloggt: Provider, Grund-Code, Request-ID | keine |
| 69 | `Services/Auth/FederatedAdmission.cs:110` | **Fehlalarm** | geloggt: Grund-Code und Fehler-Code | keine |
| 67 | `Services/Auth/FederatedAdmission.cs:52` | **Fehlalarm** | geloggt: Provider, Binding-ID, Grund-Code | keine |
| 63 | `Services/AuditLog/AuditLogService.cs:120` | **Fehlalarm** | Quelle sind die Grund-Code-Konstanten (der echte Adressfluss an derselben Zeile ist #2/#16/#17) | Zeile ohnehin über `LogSafe.Value` |
| 20 | `Services/Guardrails/DirectMcpCallGuard.cs:68` | echt | `ActorOf(principal)` ist bei einer Person die Adresse | `LogSafe.Value` + zentral |
| 17 | `Services/AuditLog/AuditLogService.cs:120` | echt | Notfall-Zeile: Akteur/Ziel eines Nicht-`auth.*`-Eintrags kann eine Adresse sein (z. B. `setup.first_admin`) | `LogSafe.Value` + zentral |
| 16 | `Services/AuditLog/AuditLogService.cs:120` | echt | wie #17 | wie #17 |
| 15 | `Services/Approvals/ApprovalStore.cs:322` | echt | `resolver.PrincipalId` ist `user:<adresse>` | `LogSafe.Value` + zentral |
| 14 | `Services/Approvals/ApprovalStore.cs:297` | echt | wie #15 | `LogSafe.Value` + zentral |
| 11 | `Services/Auth/SessionRevalidatingAuthenticationStateProvider.cs:135` | **Fehlalarm** | geloggt nur der Grund-Code (`no_email` u. a.); die Adresse geht ins Audit, nicht ins Log | keine |
| 10 | `Services/Auth/LocalAccountService.cs:303` | echt | Adresse des lokalen Kontos | `LogSafe.Account` + zentral |
| 9 | `Services/Auth/LocalAccountService.cs:267` | echt | wie #10 | `LogSafe.Account` + zentral |
| 8 | `Services/Auth/LocalAccountService.cs:190` | echt | wie #10 | `LogSafe.Account` + zentral |
| 7 | `Services/Auth/LocalAdminSeeder.cs:38` | echt | Adresse des Start-Admins | `LogSafe.Account` + zentral |
| 6 | `Services/Auth/LocalAdminSeeder.cs:35` | echt | wie #7 | `LogSafe.Account` + zentral |
| 5 | `Services/Auth/LocalAdminSeeder.cs:43` | echt | wie #7 | `LogSafe.Account` + zentral |
| 2 | `Services/AuditLog/AuditLogService.cs:119` | echt | wie #17 | wie #17 |
| 1 | `Hubs/HubSessionSweep.cs:53` | **Fehlalarm** | geloggt nur der Grund-Code | keine |


### cs/cleartext-storage-of-sensitive-information (4, sec=high)

| # | Ort | Urteil | Begründung | Massnahme |
|---|---|---|---|---|
| 26 | `Services/Server/HostCommandExecutor.cs:340` (jetzt 357) | echt — **behoben (Datenfluss)** | Zeitüberschreitung (Warning, wird immer geschrieben) loggt die Argumente des Prozesses. Bei einer Datenbank-Aktion standen darin DB-Passwörter aus der Container-Umgebung (`DatabaseDetector.GetFirst`, `POSTGRES_PASSWORD` usw.): `PGPASSWORD='…' pg_dump`, `MYSQL_PWD='…' mysql`, `NEO4J_PASSWORD='…' cypher-shell` in einem `sh -c` und Neo4j als argv `-p <pw>`. `SecretRedactor` erkannte das nur teilweise (bis zum ersten Leerzeichen, nicht in der zweifach gequoteten Form) | Das Passwort gelangt nicht mehr in den Befehl (siehe unten). Die Zeile loggt jetzt z. B. `docker exec -e PGPASSWORD -- 'c0ffee' 'pg_dump' …` |
| 25 | `Services/Server/HostCommandExecutor.cs:181` (jetzt 189) | echt — **behoben (Datenfluss)** | wie #26, Debug-Zeile vor einem SSH-Befehl | wie #26; vor dem Befehl steht nur `IFS= read -r PGPASSWORD \|\| exit 97; export PGPASSWORD;` |
| 24 | `Services/Server/HostCommandExecutor.cs:114` (jetzt 118) | echt — **behoben (Datenfluss)** | wie #26, Debug-Zeile vor einem mTLS-Docker-Hostbefehl | wie #26 |
| 23 | `Services/Server/HostCommandExecutor.cs:97` (jetzt 99) | echt — **behoben (Datenfluss)** | wie #26, Debug-Zeile vor einem lokalen nsenter-Befehl | wie #26 |

**Was an den vier Stellen gespeichert wird.** Alle vier schreiben in das Anwendungs-Log (drei Debug-Zeilen, eine
Warning bei Zeitüberschreitung); derselbe Befehlstext ist außerdem die argv des `nsenter`-/`ssh`-Prozesses (Prozessliste
des Whiskers-Hosts) bzw. das `Cmd` des Helfer-Containers, und das `sh -c` im Datenbank-Container stand in dessen
Prozessliste. In eine Befehls-Historie oder das Audit-Log gehen die DB-Befehle nicht (das Audit der Datenbank-Aktionen
schreibt die Abfrage, nicht den Befehl).

**Der neue Datenfluss.** Der Befehl nennt nur noch den Namen der Variable, aus der der Client sein Passwort liest:
`docker exec -e PGPASSWORD -- <container> pg_dump …`. Den Wert reicht `DatabaseService` über
`HostCommandSecrets.Provide` an genau diesen einen Executor-Aufruf (`AsyncLocal`, die Schnittstelle
`IHostCommandExecutor` und die Gateway-Regeln auf ihren Aufrufern bleiben unverändert). Der Executor liefert ihn je
Transport neben dem Befehl aus:

| Transport | Übergabe |
|---|---|
| lokal (`nsenter`) | Umgebung des `nsenter`-Prozesses → Host-Shell → `docker exec -e` |
| mTLS (Helfer-Container) | `Env` des Helfer-Containers → `nsenter` → Host-Shell → `docker exec -e`. Der Wert steht bis zum Entfernen des Helfers in dessen Konfiguration auf diesem Host — auf demselben Host, auf dem er ohnehin in der Konfiguration des Datenbank-Containers steht, aus der er stammt |
| SSH | Standardeingabe von `ssh`; die entfernte Shell liest ihn mit `IFS= read -r NAME` in die Variable und exportiert sie. Fehlt der Wert, bricht der Befehl vor dem Start ab (Exit 97) |

| Datenbank | Variable | vorher | jetzt |
|---|---|---|---|
| PostgreSQL | `PGPASSWORD` | im Befehlstext (nur beim Dump) | `docker exec -e PGPASSWORD`, für jede Aktion |
| MySQL/MariaDB | `MYSQL_PWD` | im Befehlstext (`sh -c "MYSQL_PWD='…' mysql …"`) | `docker exec -e MYSQL_PWD`, `mysql`/`mysqldump` direkt als argv |
| Redis | `REDISCLI_AUTH` | gar nicht übergeben | `docker exec -e REDISCLI_AUTH` (authentifiziert jetzt, wenn ein Passwort gesetzt ist) |
| Neo4j | `NEO4J_PASSWORD` | im Befehlstext bzw. als argv `-p <pw>` | `docker exec -e NEO4J_PASSWORD`, `cypher-shell -u <user>` |
| MongoDB | – | gar nicht übergeben | weiterhin nicht: `mongosh` liest ein Passwort nur aus argv, einer URI oder einer Eingabeaufforderung, und Whiskers authentifiziert sich bei MongoDB nicht. Sollte das kommen, ginge es ohne argv nur über eine Konfigurationsdatei — offen benannt, nicht gebaut |

**Kurze Passwörter.** Die Lücke für Passwörter unter 8 Zeichen ist strukturell zu: der Wert steht in keinem Text mehr,
den ein Muster oder `KnownSecrets` bereinigen müsste. Eine zusätzliche Erkennungsregel für kurze Werte gibt es bewusst
nicht — `KnownSecrets` bleibt bei 8 Zeichen Mindestlänge, weil ein kurzer, häufiger Wert („admin“, „root“) sonst überall
in fremder Ausgabe zu `***` würde. `DatabaseService.Withhold` registriert Passwörter ab 8 Zeichen weiter als bekanntes
Geheimnis, als zweite Linie für den Fall, dass ein Client es einmal in einer Fehlermeldung zurückgibt.

**CodeQL.** Der Fluss von `GetFirst` endet jetzt in `ProcessStartInfo.Environment`, im `Env` des Helfers bzw. in der
Standardeingabe von `ssh` — keine Log-Senke mehr. Ob CodeQL die Standardeingabe als Speicher-Senke wertet, zeigt der
nächste Lauf; dann wäre das ein neuer Alarm an `HostCommandExecutor.RunProcessAsync` und kein Klartext-Speicher.

Tests: `HostCommandSecretsTests` (je Datenbank-Art und mit Passwörtern aus 3 Zeichen, mit Leerzeichen, Hochkomma,
Anführungszeichen, `$HOME` und Backticks: weder im Befehl noch als argv, der Wert wird beiseite übergeben; der
mTLS-Helfer bekommt ihn als `Env`; über SSH liest eine echte `sh` ihn aus der Standardeingabe, lokal aus der Umgebung;
falsche Namen und Werte mit Zeilenumbruch werden abgelehnt).

**INV-52 / SSH-Schlüssel:** nicht betroffen. Quelle aller vier Alarme ist `DatabaseDetector.GetFirst` (Passwörter aus
der Umgebung von Datenbank-Containern), nicht der SSH-Schlüssel. Der Schlüssel liegt seit INV-52 versiegelt auf der
Platte und wird nur als kurzlebige 0600-Lease in `/dev/shm` geöffnet; sein Inhalt wird nie geloggt.


### cs/web/cookie-secure-not-set (3) — behoben nach Freigabe des Nutzers (2026-11-10)

| # | Ort | Urteil | Begründung | Massnahme |
|---|---|---|---|---|
| 86 | `Startup/WhiskersPipelineExtensions.cs:241` | Fehlalarm, trotzdem gehärtet — **behoben** | Sprach-Cookie `.AspNetCore.Culture` (`c=en\|uic=en`) von `/set-culture`: kein Geheimnis, keine Sitzung. War ohne `Secure`, damit es auch bei HTTP im LAN gesetzt wird | `Secure = ctx.Request.IsHttps`, `SameSite = Lax`, `HttpOnly = true` |
| 60 | `Startup/WhiskersAuthenticationExtensions.cs:139` | echt — **behoben** | OIDC-Nonce-Cookie: `SecurePolicy = None`, **nur** wenn `Oidc:RequireHttpsMetadata=false` (ausdrücklicher Opt-in für LAN über HTTP) — auch bei HTTPS. Standard ist `true`; dann bleibt der Framework-Standard `Always` | `SameAsRequest` im Zweig: über HTTPS `Secure`, über HTTP weiter ohne |
| 61 | `Startup/WhiskersAuthenticationExtensions.cs:140` | echt — **behoben** | OIDC-Correlation-Cookie, wie #60 | wie #60 |

Tests: `HttpSecurityTests.Oidc_nonce_and_correlation_cookies_follow_the_scheme_without_https_metadata` (HTTP und HTTPS)
und `HttpSecurityTests.The_language_cookie_is_http_only_lax_and_secure_over_https` (HTTP und HTTPS). Middleware,
Reihenfolge, OAuth-Whitelist und Rollen sind unverändert. Ob CodeQL `SameAsRequest` bzw. `Secure = IsHttps` als
erledigt erkennt, zeigt der nächste Lauf (das Sitzungs-Cookie mit `SameAsRequest` wird nicht gemeldet); bleibt ein
Alarm stehen, mit Verweis auf diese Tests schliessen.

**Empfehlung (vom Nutzer am 2026-11-10 freigegeben und so umgesetzt):**

> 1. **#60/#61:** Im Zweig `if (!oidcRequireHttps)` `CookieSecurePolicy.None` durch `CookieSecurePolicy.SameAsRequest`
>    ersetzen. Über HTTP bleibt das Cookie ohne `Secure` (der LAN-Fall funktioniert wie heute), über HTTPS — direkt oder
>    hinter einem vertrauenswürdigen Proxy, dessen `X-Forwarded-Proto` die Forwarded-Headers-Middleware übernimmt —
>    bekommt es `Secure`. Das ist in jedem Fall mindestens so sicher wie heute und schliesst die Lücke, dass ein
>    HTTPS-Betrieb mit `RequireHttpsMetadata=false` (z. B. IdP im LAN per HTTP, Whiskers per HTTPS) heute Nonce und
>    Correlation ohne `Secure` setzt. Den Zweig nicht entfernen: ein bedingungsloses `SameAsRequest` wäre hinter einem
>    nicht eingetragenen TLS-Proxy schwächer als das heutige `Always`. Das Sitzungs-Cookie (Zeile 66) nutzt bereits
>    `SameAsRequest` und wird von CodeQL nicht gemeldet — die Änderung schliesst die beiden Alarme also voraussichtlich.
>    Test dazu: ein OIDC-Challenge über HTTPS mit `RequireHttpsMetadata=false` setzt `.AspNetCore.Correlation.*` und
>    `.AspNetCore.OpenIdConnect.Nonce.*` mit `secure`; über HTTP ohne.
> 2. **#86:** `new CookieOptions { …, Secure = ctx.Request.IsHttps, SameSite = SameSiteMode.Lax, HttpOnly = true }`.
>    Das Cookie liest nur der Server (`CookieRequestCultureProvider`), `HttpOnly` bricht also nichts; `Secure` folgt dem
>    Schema wie beim Sitzungs-Cookie. Reine Härtung, kein Sicherheitsgewinn im engeren Sinn. Alternativ den Alarm als
>    „Won't fix — Sprach-Cookie ohne Geheimnis“ schliessen.
>
> Beides berührt die Startup-/Auth-Konfiguration; freigegeben waren nur diese zwei Stellen.

## Tests

`src/Whiskers.Tests/LogSafeTests.cs` (15 Fälle):

- `A_value_becomes_one_line` (6 Fälle: CR LF, LF, CR, NEL, U+2028, U+2029)
- `An_account_is_a_stable_pseudonym_that_does_not_show_the_address`
- `Addresses_become_pseudonyms_and_digests_and_ssh_targets_stay`
- `A_logged_value_with_line_breaks_stays_one_line_in_every_provider` (über die echte Logger-Fassade)
- `A_multi_line_template_keeps_its_own_line_breaks`
- `An_address_reaches_no_provider_in_a_value_a_scope_or_an_exception`
- `An_audit_row_keeps_the_address_and_its_log_line_neither_names_it_nor_breaks` (DB-Zeile unverändert, Log-Zeile
  einzeilig und ohne Adresse — Erfolg und Notfall-Zeile, ohne die Fassade)
- `A_database_password_never_stands_in_a_logged_command` (PostgreSQL, MySQL, Neo4j; Passwort mit Leerzeichen und
  Hochkomma; kein `-p` als argv-Element)

`src/Whiskers.Tests/HostCommandSecretsTests.cs` (CodeQL #23–#26, Datenfluss):

- `A_database_password_is_passed_beside_the_command_never_in_it` (25 Fälle: PostgreSQL, MySQL, Redis, Neo4j, MongoDB ×
  5 Passwörter — 3 Zeichen, mit Leerzeichen, Hochkomma, Anführungszeichen, `$HOME` und Backticks)
- `Without_a_password_no_variable_is_named_or_passed`
- `The_mtls_helper_gets_the_secret_as_its_environment_and_not_in_its_command`
- `Over_ssh_the_remote_shell_reads_each_secret_from_standard_input`, `Over_ssh_a_missing_value_stops_the_command_before_it_runs`,
  `Locally_the_secret_is_in_the_environment_of_the_process_not_its_arguments` (echte `sh`; ohne `sh` übersprungen)
- `A_name_or_a_value_that_cannot_travel_is_refused` (6 Fälle), `A_scope_ends_with_its_handle_and_restores_the_outer_one`

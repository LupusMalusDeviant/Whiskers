# 0002 — SSH-Host-Key-Verifizierung: `accept-new` (Trust-On-First-Use)

- **Status:** UMGESETZT (2026-07-11, ausdrückliche User-Freigabe erteilt) — `accept-new` + eigenes `known_hosts` unter dem Data-Dir auf allen 3 Pfaden, zentral in `Services/Server/SshHostKeyPolicy`. Host-Keys der Bestandsflotte wurden vor dem Rollout per `ssh-keyscan` geseedet. Nach einem beabsichtigten Server-Rebuild: die Host-Zeile aus `<data>/ssh-keys/known_hosts` entfernen, dann neu verbinden.
- **Hinweis zum Dateinamen:** Der Dateiname (`…-deferred`) stammt aus der ersten Fassung (2026-07-06), in der die Umstellung zurückgestellt war; er bleibt, damit bestehende Verweise gültig bleiben. Gültig ist die Entscheidung unten (R1-51, D-17).
- **Betrifft:** `src/Whiskers/Services/Docker/SshTunnelManager.cs`, `src/Whiskers/Services/Server/HostCommandExecutor.cs`, `src/Whiskers/Services/Terminal/TerminalSession.cs`
- **Bezug:** Full-Repo-Review 2026-07-06, Finding HOCH-11

## Kontext

Bis 2026-07-11 setzten alle drei SSH-Pfade (Docker-Tunnel, Host-Command-Executor, Web-Terminal) `-o StrictHostKeyChecking=no`. Damit wurde der Host-Key nie geprüft: ein On-Path-Angreifer (ARP/DNS/BGP) kann die SSH-Verbindung transparent per MITM übernehmen — inklusive Mitlesen aller Kommandos/Keystrokes und, auf dem sshpass-Bootstrap-Pfad des `HostCommandExecutor`, Abgreifen des Root-Passworts.

Der Review empfiehlt `-o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=/app/data/ssh-keys/known_hosts` (Trust-On-First-Use: Erstverbindung akzeptiert und pinnt den Key, spätere Key-Wechsel werden erkannt und die Verbindung schlägt fehl).

## Anforderungen

- MITM auf den SSH-Pfaden erschweren, ohne die Erstverbindungs-UX zu ändern.
- **Keine Selbst-Aussperrung** von der verwalteten Infrastruktur.
- Respektieren der Projekt-Grenze (heute: docs/ARCHITECTURE.md, „Protected layers"): der mTLS/WireGuard-Netzwerk-Layer und die SSH-/Auth-Pfade sind „Off-Limits" ohne ausdrückliche Erlaubnis.

## Optionen

1. **`accept-new` + gepinnte `known_hosts` sofort umsetzen.** Höchste Sicherheit, aber: pinnt bei Erstkontakt und lässt danach jede Verbindung mit geändertem Host-Key **fehlschlagen**.
2. **Zurückstellen bis zur ausdrücklichen Freigabe.** Sicherheitslücke bleibt vorerst offen, aber kein Risiko einer flächendeckenden Nicht-Erreichbarkeit.
3. **Voll `StrictHostKeyChecking=yes` mit vorab hinterlegten Host-Keys pro Server.** Sicherste, aber betrieblich aufwändigste Variante (Host-Keys müssen beim Server-Onboarding erfasst werden).

## Entscheidung

**Option 1 — `accept-new` mit eigenem `known_hosts`, umgesetzt am 2026-07-11** (Commit `23e691b`, ausdrückliche
Freigabe für den geschützten SSH-Pfad). Option 3 (vorab hinterlegte Host-Keys je Server, `StrictHostKeyChecking=yes`)
ist nicht umgesetzt; die Bestandsflotte wurde vor dem Rollout einmalig per `ssh-keyscan` geseedet.

**Verlauf.** Die erste Fassung dieses ADR (2026-07-06) entschied **Option 2 — zurückgestellt**, mit dieser Begründung:

- Die Docker-Hosts werden regelmäßig rebootet/neu aufgesetzt; ein Host-Key-Wechsel nach einem Rebuild würde mit
  `accept-new` den betroffenen Server unerreichbar machen — dieselbe Fehlerklasse „SSH-Server Unreachable", die schon
  einmal auftrat (Tunnel-Leak-Historie).
- Die Projekt-Regeln (heute docs/ARCHITECTURE.md) markieren den Netzwerk-/Auth-Layer als Off-Limits ohne ausdrückliche
  Erlaubnis.

Diese Zurückstellung ist durch die Freigabe vom 2026-07-11 abgelöst. Das Risiko der Rebuilds trägt seither der
Runbook-Schritt unter *Konsequenzen*.

## Umsetzung

Alle drei SSH-Pfade (`SshTunnelManager`, `HostCommandExecutor`, `TerminalSession`) hängen die Optionen aus
`Services/Server/SshHostKeyPolicy.Options()` an, statt `-o StrictHostKeyChecking=no` zu setzen:

```
-o StrictHostKeyChecking=accept-new
-o UserKnownHostsFile=<Datenverzeichnis>/ssh-keys/known_hosts
```

Das Datenverzeichnis kommt aus `DataPathOptions` (`/app/data` im Image); `SshHostKeyPolicy` legt `ssh-keys/` vor dem
ersten Verbindungsaufbau an. Die Datei liegt im Daten-Volume und überlebt Container-Rebuilds; ein Self-Backup enthält
sie (`ssh-keys/`).

## Konsequenzen

- Ein späterer Host-Key-Wechsel lässt die Verbindung zu diesem Server fehlschlagen, statt still mit einem möglichen
  Man-in-the-Middle zu sprechen.
- Nach einem **beabsichtigten** Rebuild eines Servers die Zeile des Hosts aus `<Daten>/ssh-keys/known_hosts` entfernen
  (wie `ssh-keygen -R`), dann neu verbinden; der erste Kontakt pinnt den neuen Key. Siehe
  `docs/operations/troubleshooting.md`.
- Der **erste** Kontakt wird nicht geprüft (Trust-On-First-Use). Wer das ausschließen will, seedet die `known_hosts`
  vorab mit einem außerhalb geprüften Host-Key.
- **Nachtrag (Vertrag 0018, angenommen 2026-11-16; Umsetzung in dessen PR 2):** Das Onboarding pinnt den Host-Key
  ausdrücklich vor dem ersten Befehl, hält ihn am Server fest und protokolliert ihn; ersetzen darf ihn nur ein
  interaktiver Admin mit Freigabe (`server.hostkey_replace`, wie INV-59). Der Hetzner-Rescue-Weg liest die Host-Keys
  von der Platte des Servers, aber über eine SSH-Sitzung zum Rescue-System, und diese Sitzung ist selbst
  **Trust-On-First-Use auf dem Rescue-System** (`accept-new` gegen eine leere, laufeigene `known_hosts`). Der so
  gepinnte Key ist also so vertrauenswürdig wie diese eine Sitzung; er ist nicht „außerhalb geprüft".
- **Umsetzung des Nachtrags (Vertrag 0018, PR 2, INV-63):** `Services/Server/SshHostKeyPins` mit dem Prozess-Seam
  `SshToolchain`. Der Onboarding-Schritt `HostKey` läuft für jeden SSH-Datensatz vor dem ersten Befehl; ohne den Dienst
  bricht der Lauf ab (fail closed). Erstkontakt: `ssh-keyscan` (ed25519, ecdsa, rsa), Vergleich je Schlüsseltyp mit der
  eigenen `known_hosts` über `ssh-keygen -F` (auch gehashte Zeilen; `[host]:port` bei einem anderen Port als 22),
  fehlende Typen werden als **ungehashte** Zeilen angehängt; der bevorzugte Key (ed25519 vor ecdsa vor rsa) steht mit
  Typ, Fingerprint (immer aus `ssh-keygen -l`), Quelle (FirstContact / RescueDisk / Operator) und Zeitpunkt am Server
  und im Audit (`hostkey.pin`). Ein Typ, den der Host nicht mehr anbietet, ist keine Abweichung; ein anderer Key
  irgendeines Typs bricht bei `HostKey` ab und zeigt beide Fingerprints. Für vor 0018 onboardete Hosts zeigt die
  Server-Seite einen aus `known_hosts` gelesenen Pin an (nur im Speicher, kein Schreiben, kein Audit-Ereignis).
  Ersetzen: `server.hostkey_replace` (Admin, nur interaktiv, Freigabe wie INV-59, Grant einmal verwendet) liest den
  angebotenen Key neu, lehnt ab, wenn einer der beiden Fingerprints nicht dem verglichenen entspricht, entfernt die
  Zeilen mit `ssh-keygen -R`, hängt die neuen an und protokolliert alten und neuen Fingerprint (`hostkey.replace`).
  `StrictHostKeyChecking=accept-new` auf den drei SSH-Pfaden bleibt unverändert.
- Onboardete Hosts laufen danach ohne SSH über den mTLS-Kanal (`docs/ARCHITECTURE.md`); dieser ADR betrifft nur
  Bootstrap und nicht migrierte SSH-Server.

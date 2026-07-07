# 0002 — SSH-Host-Key-Verifizierung: Umstellung auf `accept-new` zurückgestellt

- **Status:** Offen / zurückgestellt — braucht ausdrückliche Freigabe (2026-07-07)
- **Betrifft:** `src/ServerWatch/Services/Docker/SshTunnelManager.cs`, `src/ServerWatch/Services/Server/HostCommandExecutor.cs`, `src/ServerWatch/Services/Terminal/TerminalSession.cs`
- **Bezug:** Full-Repo-Review 2026-07-06, Finding HOCH-11

## Kontext

Alle drei SSH-Pfade (Docker-Tunnel, Host-Command-Executor, Web-Terminal) setzen `-o StrictHostKeyChecking=no`. Damit wird der Host-Key nie geprüft: ein On-Path-Angreifer (ARP/DNS/BGP) kann die SSH-Verbindung transparent per MITM übernehmen — inklusive Mitlesen aller Kommandos/Keystrokes und, auf dem sshpass-Bootstrap-Pfad des `HostCommandExecutor`, Abgreifen des Root-Passworts.

Der Review empfiehlt `-o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=/app/data/ssh-keys/known_hosts` (Trust-On-First-Use: Erstverbindung akzeptiert und pinnt den Key, spätere Key-Wechsel werden erkannt und die Verbindung schlägt fehl).

## Anforderungen

- MITM auf den SSH-Pfaden erschweren, ohne die Erstverbindungs-UX zu ändern.
- **Keine Selbst-Aussperrung** von der verwalteten Infrastruktur.
- Respektieren der CLAUDE.md-Grenze: der mTLS/WireGuard-Netzwerk-Layer und die SSH-/Auth-Pfade sind „Off-Limits" ohne ausdrückliche Erlaubnis.

## Optionen

1. **`accept-new` + gepinnte `known_hosts` sofort umsetzen.** Höchste Sicherheit, aber: pinnt bei Erstkontakt und lässt danach jede Verbindung mit geändertem Host-Key **fehlschlagen**.
2. **Zurückstellen bis zur ausdrücklichen Freigabe.** Sicherheitslücke bleibt vorerst offen, aber kein Risiko einer flächendeckenden Nicht-Erreichbarkeit.
3. **Voll `StrictHostKeyChecking=yes` mit vorab hinterlegten Host-Keys pro Server.** Sicherste, aber betrieblich aufwändigste Variante (Host-Keys müssen beim Server-Onboarding erfasst werden).

## Entscheidung

**Option 2 — zurückgestellt.** Begründung:

- Diese Docker-Hosts werden regelmäßig rebootet/neu aufgesetzt; ein Host-Key-Wechsel nach einem Rebuild würde mit `accept-new` **alle** SSH-verwalteten Server gleichzeitig unerreichbar machen — genau die Fehlerklasse „alle SSH-Server Unreachable", die schon einmal auftrat (Tunnel-Leak-Historie).
- CLAUDE.md markiert den Netzwerk-/Auth-Layer explizit als Off-Limits ohne ausdrückliche Erlaubnis.

Der fertige Patch liegt bereit (siehe unten) und wird nach ausdrücklicher Freigabe umgesetzt — dann zusammen mit dem Anlegen von `/app/data/ssh-keys/` und idealerweise dem Vorab-Hinterlegen der erwarteten Host-Keys pro Server (Richtung Option 3), um Rebuild-bedingte Ausfälle abzufangen.

## Umsetzung nach Freigabe (Patch)

In allen drei Dateien `-o StrictHostKeyChecking=no` ersetzen durch:

```
-o StrictHostKeyChecking=accept-new
-o UserKnownHostsFile=/app/data/ssh-keys/known_hosts
```

Zusätzlich das Verzeichnis `/app/data/ssh-keys/` vor dem ersten Verbindungsaufbau anlegen (persistiert über Rebuilds im Data-Volume). Optional den erwarteten Host-Key pro `ServerConfig` speichern und vor dem Connect in die `known_hosts` schreiben.

## Konsequenzen

- **Solange offen:** die MITM-Angriffsfläche auf den SSH-Pfaden bleibt bestehen. Mitigiert durch die Mesh-/Tailscale-Bindung (kein öffentliches Exposé) und Single-Admin-Betrieb.
- Nach Umsetzung: Rebuild-bedingte Host-Key-Wechsel erfordern das Entfernen des veralteten `known_hosts`-Eintrags, sonst schlägt die Verbindung zum betroffenen Server fehl. Deshalb Kopplung an einen Onboarding-/Rebuild-Runbook-Schritt empfohlen.

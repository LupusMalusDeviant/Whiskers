# Vorfall 2026-08-26: Der Log-Monitor legt dockerd lahm

**Betroffen:** BurgCloud (Hetzner, 2 Kerne, Debian 12) — Docker 29.5.2, 10 Container
**Dauer:** 20.08.2026 14:02 UTC bis 26.08.2026 15:07 UTC (6 Tage)
**Komponente:** `Services/LogMonitor/LogMonitorService.cs`, `Services/Docker/Operations/ContainerOperations.cs`
**Status:** Auf dem Server entschärft; im Code behoben (v0.12.x/0.13.0: Abbruch bis in die Leseschleife, Lastbudget und Schutzschalter, Fensterdeckel und Aussperrung, Selbstbeobachtung, Host-/Baseline-Alarme, Hygiene-Inventar — siehe CHANGELOG)

## Kurzfassung

Sechs Tage lang lief BurgCloud durchgehend bei 98 % CPU. Verursacher war nicht die überwachte
Anwendung, sondern Whiskers selbst.

Der Log-Monitor startete alle 60 Sekunden neue Log-Abrufe gegen zwei Container, deren Logs er nicht
mehr innerhalb seines 15-Sekunden-Fensters lesen konnte. Er brach das *Warten* ab, aber nicht die
*Anfrage* — die lief serverseitig weiter, bis der vorgelagerte Proxy sie nach 600 Sekunden kappte.
Weil ein fehlgeschlagener Abruf das Wasserzeichen nicht fortschreibt, wurde das `since`-Fenster mit
jedem Versuch größer, der Abruf also zuverlässig langsamer. Es entstand ein stabiler Zustand aus
13 gleichzeitig laufenden Voll-Log-Scans, in dem dockerd rund **1,15 Millionen `read()`-Syscalls pro
Sekunde** absetzte und **184 % CPU** (von 200 %) verbrauchte.

Whiskers hat diesen Zustand die ganze Zeit gemessen, rund 1.600-mal am Tag nach `ServerMetrics`
geschrieben — und nie gemeldet.

## Messwerte

| | während des Vorfalls | nach der Entschärfung |
|---|---|---|
| dockerd CPU | 184 % | 1 % |
| `read()`-Syscalls | 1.152.562/s | 86/s |
| offene dockerd-FDs auf die zwei Logs | 22 | 0 |
| Server-CPU (Whiskers) | 98,3 % | 9,0 % |
| Server-CPU (Hetzner, Außenmessung) | 195,8 | 31,4 |
| Load average | 3,36 | 0,12 |
| `/var/lib/docker/containers` | 880 MB | 35 MB |

Die Außenmessung über die Hetzner-API ist wichtig: sie schließt aus, dass die Last ein Artefakt der
eigenen Messung war.

## Der Mechanismus

### 1. Abgebrochenes Warten ist kein abgebrochener Auftrag

`LogMonitorService.cs:259-272`

```csharp
var fetch = _docker.GetContainerLogsAsync(container.Id, TailLines, container.ServerId, since);
if (await Task.WhenAny(fetch, Task.Delay(LogFetchTimeout)) != fetch)
{
    _ = fetch.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    _logger.LogWarning("... timed out after {Timeout}s — skipped this cycle", ...);
    throw new TimeoutException(...);
}
```

Der XML-Kommentar darüber benennt die Ursache bereits selbst: *"the Docker log call carries no
cancellation token"*. Genau das ist der Defekt, nicht nur eine Randnotiz. `Task.WhenAny` lässt die
abgelaufene Aufgabe **weiterlaufen**. Die HTTP-Anfrage an dockerd bleibt offen und arbeitet weiter;
nur Whiskers schaut nicht mehr hin. Die Meldung *"skipped this cycle"* ist damit sachlich falsch:
übersprungen wurde die Auswertung, nicht die Arbeit.

Jeder 60-Sekunden-Zyklus legt eine weitere solche Anfrage obendrauf. Die einzige Instanz, die
aufräumt, ist der 600-Sekunden-Timeout des vorgelagerten haproxy. 600 / 60 = **10 gleichzeitige
Anfragen je betroffenem Container** im Gleichgewicht. Gemessen wurden 7 (socket-proxy) und
6 (ghostunnel) offene Dateideskriptoren — die Rechnung geht auf.

`IDockerService.cs:20` und `ContainerOperations.cs:256` führen keinen `CancellationToken`.
Docker.DotNet bietet die Überladung an; die Kette gibt sie nur nicht durch.

### 2. Die Wasserzeichen-Ratsche

`LogMonitorService.cs:200-203` und `244-249`

```csharp
var fetchedAt = DateTime.UtcNow;
var since = _lastLogCheck.TryGetValue(key, out var last) ? last : fetchedAt;
var logs = await FetchLogsAsync(container, since);
_lastLogCheck[key] = fetchedAt;      // wird bei Exception nie erreicht
```

Der Kommentar im `catch` sagt: *"No watermark update on failure — the next cycle retries the same
window."* Das stimmt nicht ganz, und die Abweichung ist der Kern des Problems: es ist **nicht
dasselbe** Fenster. `since` bleibt stehen, `now` läuft weiter — das Fenster wächst mit jedem
Fehlschlag um 60 Sekunden.

Damit ist der Zustand selbstverstärkend und ohne Eingriff endgültig: Fehlschlag → größeres Fenster →
teurerer Abruf → sicherer Fehlschlag. Das erklärt, warum die Last am 20.08. innerhalb von zwei
Minuten von 12 % auf 98 % sprang und in sechs Tagen kein einziges Mal zurückkam.

### 3. `since` kostet die ganze Datei, nicht das Fenster

`ContainerOperations.cs:263-271`

```csharp
// The tail limit applies in BOTH cases. Docker filters by `since` first and then keeps the last
// N of those, so the monitor still sees only new lines — but a container that dumps thousands of
// lines between two cycles can no longer pull its whole burst over a remote connection every minute
```

Die Beobachtung ist richtig, die Schlussfolgerung greift zu kurz. Gedeckelt wird die
**Übertragung**, nicht die **Arbeit**: um `since` anzuwenden, liest und dekodiert dockerd die
JSON-Logdatei von vorn. Bei 494 MB und 328 MB ist das pro Anfrage ein dreistelliger
Megabyte-Betrag an JSON-Parsing — Arbeit, die keine einzige Zeile Ausgabe erzeugt, wenn das Fenster
leer ist.

Messbar an den Antwortzeiten im Proxy-Log: Container mit kleinen Logs antworteten in 70–140 ms, die
beiden großen liefen ausnahmslos in den 600-Sekunden-Timeout.

### 4. Die Rückkopplung: das Monitoring schreibt sein eigenes Futter

Betroffen waren ausgerechnet `socket-proxy` und `ghostunnel` — die beiden Container, über die
Whiskers seinen Docker-Zugriff führt. Beide protokollieren jede einzelne API-Anfrage, also auch jede
Anfrage des Log-Monitors: rund 110 Zeilen pro Minute, etwa 1 KB/s, gut 80 MB pro Tag. Ohne
Rotationslimit wuchsen sie in zwei Wochen auf zusammen 822 MB.

Der Log-Monitor produziert damit selbst die Logzeilen, an denen er sich anschließend festfrisst. Je
mehr er sich verschluckt, desto mehr Zeilen entstehen.

## Warum die vorhandenen Schutzmaßnahmen nicht griffen

Alle drei greifen — nur eine Ebene daneben:

- **Selbstausschluss** (`LogMonitorService.cs:41-50`) schließt den eigenen Container aus und
  verhindert damit die bereits bekannte Alarm-Rückkopplung. Die beiden Container, die den eigenen
  *Verkehr* protokollieren, sind aber ebenso "selbst" — sie stehen nicht auf der Liste.
- **`LogFetchTimeout`** schützt die eigene Verarbeitungskette. Genau das war nie das Problem —
  Whiskers lief die ganze Zeit rund. Belastet wurde der überwachte Server.
- **`TailLines`-Deckel** begrenzt, was ankommt, nicht, was der Daemon dafür tun muss.

Gemeinsamer Nenner: alle drei schützen **Whiskers vor dem Server**. Keine schützt **den Server vor
Whiskers**.

## Die blinde Stelle

`Services/Metrics/MetricsCollectorService.cs:200-217` wertet Schwellwerte aus — die Signatur sagt
alles:

```csharp
private async Task EvaluateAlertsAsync(ContainerInfo c, ContainerStats stats, ...)
```

CPU- und RAM-Alarme gibt es **je Container**. Verbrannt hat die CPU aber `dockerd`, ein
Host-Prozess, der in keinem Container läuft. Für Platten existiert ein Host-Schlüssel
(`disk:{server}`), für CPU und RAM des Hosts nichts.

Ergebnis: Whiskers hat sechs Tage lang etwa 8.900 Messpunkte für BurgCloud aufgezeichnet, davon
praktisch jeden über 98 %, und keinen einzigen bewertet. Aufgefallen ist es, weil ein Mensch auf die
Übersichtsseite geschaut hat.

## Vorschläge

Nach Wirkung sortiert.

1. **`CancellationToken` durch die Docker-Kette führen** und im Log-Monitor eine
   `CancellationTokenSource(LogFetchTimeout)` verwenden statt `Task.WhenAny`. Behebt die Ursache:
   eine abgelaufene Anfrage endet dann auch auf dem Server. Betrifft
   `IDockerService.GetContainerLogsAsync`, `DockerService`, `ContainerOperations` und
   `LogMonitorService.FetchLogsAsync`.

2. **Wasserzeichen auch im Fehlerfall fortschreiben** — oder das `since`-Fenster hart deckeln
   (etwa `max(since, now - 10 min)`). Nimmt der Ratsche die Zähne: ein Fehlschlag darf den nächsten
   Versuch nicht teurer machen. Der Preis sind verlorene Zeilen im Ausfallfenster; das ist die
   richtige Seite des Kompromisses, denn aktuell gehen sie ohnehin verloren, nur eben dauerhaft.

3. **Aussperrung nach wiederholtem Timeout.** Scheitert ein Container n-mal in Folge, ihn für eine
   Weile aus dem Scan nehmen und **das als Alarm melden**. Ein Container, dessen Log nicht mehr
   lesbar ist, ist eine Meldung wert und kein Grund, es endlos weiter zu versuchen.

4. **Host-Schwellwerte für CPU und RAM**, analog zum vorhandenen `disk:{server}`. Ein Server, der
   sechs Tage bei 98 % steht, muss vom Monitoring gemeldet werden — sonst misst es nur.

5. **Die Docker-Proxy-Container in den Selbstausschluss aufnehmen**
   (`SERVERWATCH_SELF_CONTAINERS` um `socket-proxy` und `ghostunnel` erweitern, oder Container mit
   dem eigenen Zugriffspfad automatisch erkennen). Sie protokollieren ausschließlich
   Whiskers-Verkehr; ihr Loginhalt ist für Alarmregeln wertlos.

6. **Log-Rotation beim Anlegen prüfen.** Whiskers sieht `HostConfig.LogConfig` jedes Containers. Ein
   Container ohne Rotationslimit ist eine Zeitbombe für den Log-Scan — das ließe sich als Hinweis in
   der Oberfläche anzeigen.

## Selbst erkennen und gegensteuern

Der Vorfall lief sechs Tage. Nicht, weil die Daten fehlten — Whiskers hat den Zustand
1.600-mal am Tag gemessen — sondern weil niemand sie bewertet hat. Die Kette von der Messung zur
Handlung ist an genau einer Stelle unterbrochen, und das ist die billigste Stelle, sie zu schließen.
Der Aktuator existiert bereits: die AI-Trigger-Regel „Fehler-Logs → Agent" führt heute schon von
einem Alarm zu einem handelnden Agenten. Was fehlt, ist das Signal, nicht die Fähigkeit zu handeln.

### Fünf Signale, vier davon aus vorhandenen Daten

1. **Host-CPU über Schwelle, anhaltend.** `ServerMetrics.CpuPercent` liegt bereits in der Datenbank;
   ausgewertet wird nur `EvaluateAlertsAsync(ContainerInfo, ContainerStats)`, also pro Container.
   Ein Host-Schwellwert analog zum vorhandenen `disk:{server}` hätte am 20.08. um 14:17 gemeldet.
2. **Host-Last, die kein Container erklärt.** Beide Zahlen liegen vor: Host-CPU und die Summe der
   Container-Stats. Klaffen sie auseinander, ist der Verursacher ein Host-Prozess — genau der Fall,
   der hier durch jedes Raster fiel, weil `dockerd` in keinem Container läuft. Dieses Signal ist
   spezifischer als eine reine Schwelle und nennt die Ursachenklasse gleich mit.
3. **Eigene Log-Fetch-Timeouts, gezählt statt nur geloggt.** `FetchLogsAsync` schreibt heute schon
   eine Warnung — sie versickert. Ein Zähler je `{serverId}:{containerId}` und ein Alarm ab n
   Fehlschlägen in Folge wäre das **früheste und präziseste** Signal von allen: es hätte innerhalb
   von drei Zyklen angeschlagen, also nach drei Minuten statt nach sechs Tagen.
4. **Container ohne Rotationslimit, mit großem Log.** `HostConfig.LogConfig.Config == {}` plus die
   Größe der Datei hinter `LogPath`. Das meldet die Zeitbombe, **bevor** sie zündet — eine
   Inventarprüfung, kein Alarm im Ernstfall.
5. **Antwortzeit der Docker-API, rollender Median je Server.** Whiskers macht ohnehin hunderte
   Aufrufe pro Minute. Ein Sprung von 100 ms auf 5 s ist der Fingerabdruck eines überlasteten
   Daemons, unabhängig davon, wer ihn überlastet.

### Gegenmaßnahmen, gestuft

Die Trennlinie verläuft nicht zwischen „harmlos" und „gefährlich", sondern zwischen **sich selbst
zurücknehmen** und **den überwachten Server verändern**.

**Stufe 0 — Selbstdrosselung, ohne Rückfrage.** Betrifft ausschließlich Whiskers' eigenes Verhalten,
kann den Server also nur entlasten:
- Container nach n Timeouts in Folge mit Backoff aus dem Log-Scan nehmen. Das allein hätte diesen
  Vorfall im Keim erstickt.
- Laufende Anfragen bei Timeout tatsächlich abbrechen (siehe Vorschlag 1 oben) — ohne das bleibt
  jede Drosselung wirkungslos, weil die alten Anfragen weiterlaufen.
- Scan-Intervall strecken, solange ein Server über der Schwelle steht.

**Bedingung:** jede Selbstdrosselung wird gemeldet. Sonst wird aus „leise" wieder „blind", und der
nächste Vorfall versteckt sich hinter der Maßnahme gegen den letzten.

**Stufe 1 — vorschlagen, nicht tun.** Meldung mit Diagnose und fertigem Befehl: *„ghostunnel: 494 MB
Log, kein Rotationslimit, Log-Abrufe laufen in den Timeout. Behebung: `logging.max-size` setzen und
den Container neu erzeugen."* In der Oberfläche ein Knopf, der den Befehl vorbereitet.

**Stufe 2 — nur auf ausdrückliche Freigabe.** Log-Rotation setzen (erfordert das Neuerzeugen des
Containers), einen Container neu starten, Logs abschneiden. Nichts davon darf automatisch laufen:
das Neuerzeugen von `ghostunnel` kappt den eigenen Steuerkanal, und ein Container-Neustart ist aus
Sicht der Nutzer ein Ausfall.

### Warum diese Reihenfolge

Automatische Gegenmaßnahmen an fremden Systemen sind verlockend und in der Sache riskant: der
Log-Monitor hat diesen Vorfall in dem Glauben verursacht, sein 15-Sekunden-Timeout schütze bereits.
Eine Automatik, die auf ein Signal hin handelt, das sie selbst erzeugt, ist derselbe Fehler eine
Ebene höher. Stufe 0 ist deshalb nicht der zaghafte Anfang, sondern die eigentliche Lösung — und
Stufe 1 sorgt dafür, dass ein Mensch überhaupt erfährt, dass etwas nicht stimmt.

## Prüfkriterium

Ein Test, der den Vorfall reproduziert und die Behebung belegt:

> Ein Container mit einem Log jenseits der Fetch-Timeout-Grenze wird über mehrere Zyklen gescannt.
> **Erwartung:** die Zahl der gleichzeitig offenen Log-Anfragen gegen diesen Container bleibt bei
> höchstens 1, und die Dauer eines Abrufs wächst über die Zyklen nicht an.

Auf einem laufenden System lässt sich beides direkt messen:

```bash
# offene Log-Leser von dockerd
for f in /proc/$(pidof dockerd)/fd/*; do readlink $f; done | grep -c 'json.log'

# Leerlauf-Spin sichtbar machen: viele Reads, kaum Bytes
awk '/^syscr|^rchar/{print $1,$2}' /proc/$(pidof dockerd)/io
```

## Sofortmaßnahme auf dem Server (bereits erfolgt)

Am Symptom, nicht an der Ursache: `ghostunnel` und `socket-proxy` haben Log-Rotation bekommen
(10 MB, 3 Dateien) und wurden leiser gestellt (`LOG_LEVEL: warning` bzw.
`--quiet=conns --quiet=conn-errs`, Handshake-Fehler bleiben sichtbar). Zusätzlich wurde ein
Rotations-Default in `/etc/docker/daemon.json` gesetzt und `live-restore` aktiviert.

Damit sind die Logs klein und die Abrufe schnell. Der Defekt im Log-Monitor ist dadurch nur nicht
mehr auslösbar, nicht behoben: der nächste Container, dessen Log groß genug wird, löst denselben
Zustand erneut aus.

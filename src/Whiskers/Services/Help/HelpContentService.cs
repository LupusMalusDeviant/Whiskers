using System.Globalization;
using Whiskers.Models.Help;

namespace Whiskers.Services.Help;

/// <summary>
/// The complete in-app user handbook. Content lives here as Markdown prose plus a handful of
/// hand-drawn, theme-aware SVG diagrams; UI-heavy chapters carry screenshot placeholders that can
/// be swapped for real captures later. Pure static content, no external state. This class holds
/// the German chapters; the English set (the default for every non-German culture) lives in
/// <see cref="HelpContentEn"/> and must be kept structurally identical chapter-for-chapter.
/// </summary>
public sealed class HelpContentService : IHelpContentService
{
    public IReadOnlyList<HelpChapter> GetChapters() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? Chapters : HelpContentEn.Chapters;

    private static HelpFigure Shot(string caption) => new(HelpFigureKind.Screenshot, caption);
    private static HelpFigure Img(string caption, string path) => new(HelpFigureKind.Image, caption, Image: path);
    private static HelpFigure Diagram(string caption, string svg) => new(HelpFigureKind.Svg, caption, svg);

    private static readonly IReadOnlyList<HelpChapter> Chapters = new List<HelpChapter>
    {
        new("erste-schritte", "Erste Schritte", "Rocket",
            "Was Whiskers ist, Anmelden und der Aufbau der Oberfläche.",
            new List<HelpSection>
            {
                new("Was ist Whiskers?", """
                    **Whiskers** ist eine selbst gehostete **Control Plane**, mit der Menschen und KI-Agenten
                    Infrastruktur bedienen können, ohne unkontrollierten SSH- oder Root-Zugriff zu erhalten:
                    Jede Aktion ist berechtigt, durch Richtlinien geprüft und nachvollziehbar protokolliert.
                    Statt einer Shell nutzen die Bedienenden (Mensch **oder** KI) explizite Werkzeuge —
                    begrenzt durch Berechtigungen, geprüft durch im Code erzwungene Guardrails und, bei
                    heiklen Aktionen, zurückgehalten für deine Freigabe.

                    Diese eine Control Plane erreicht Docker-Hosts und Kubernetes-Workloads — Container,
                    Images, Netzwerke, Datenbanken, Firewall, Nginx, systemd, SSL, Metriken und Logs — und
                    stellt dieselben Fähigkeiten optional einer **KI** über einen abgesicherten
                    **MCP-Endpunkt** bereit (siehe Kapitel *KI-Agent* und *MCP-Server*). Die Reichweite ist
                    der Beleg, der Kern sind kontrollierte, nachvollziehbare Operationen.

                    Ein Designziel ist **SSH-schlüsselfreier Regelbetrieb**: Nach einem einmaligen Bootstrap
                    werden Hosts über ein privates Mesh mit gegenseitigem TLS (mTLS) angesprochen, sodass kein
                    dauerhafter Privatschlüssel herumliegt, den jemand stehlen könnte.
                    """),
                new("Erster Start & Anmelden", """
                    Beim **ersten Start** führt dich der **Setup-Wizard** im Browser durch die Einrichtung:
                    Admin-Konto anlegen, Schlüssel einmalig sichern — ganz ohne Konfigurationsdatei.

                    Danach meldest du dich wahlweise **lokal** (E-Mail + Passwort) oder per **Single Sign-On**
                    (Google/OIDC) an. Nur freigegebene E-Mail-Adressen erhalten Zugang. Deine Rolle bestimmt,
                    was du darfst:

                    - **Viewer**: alles ansehen, keine Änderungen.
                    - **Operator**: Container starten/stoppen/neustarten, Befehle ausführen.
                    - **Admin**: alles inkl. Server anlegen, Guardrails, Einstellungen, Löschen.
                    """),
                new("Der Aufbau der Oberfläche", """
                    Die Oberfläche besteht aus vier Bereichen:

                    1. **Sidebar (links)**: die Navigation, nach Themen gruppiert (Übersicht, Deployment,
                       Infrastruktur, Automatisierung) plus *Einstellungen* und *Hilfe*.
                    2. **Topbar (oben)**: die **Glocke** (Benachrichtigungen), das **Paletten-Icon**
                       (Theme wechseln) und dein Profil/Abmelden.
                    3. **Inhaltsbereich**: die jeweils geöffnete Seite.
                    4. **KI-Widget (unten rechts)**: der schwebende Agent-Chat, auf jeder Seite verfügbar
                       (wenn der Agent aktiviert ist).
                    """,
                    Diagram("Aufbau der Whiskers-Oberfläche", SvgLayout)),
            }),

        new("dashboard", "Dashboard & Übersicht", "Dashboard",
            "Server-Karten, Container-Gruppen, Status und Schnellaktionen.",
            new List<HelpSection>
            {
                new("Server- und Container-Karten", """
                    Das **Dashboard** zeigt jeden Server mit seinen Containern. Container sind gruppiert nach
                    **Standalone**, **Compose-Projekt** und **Telemetry**. Pro Container siehst du Name, Image,
                    **Status** (running/exited/created), **Zustand** (gesund/ungesund), CPU- und RAM-Last sowie
                    den Statustext.

                    Klick auf einen **Container-Namen** öffnet die Detailansicht; das **▶/■-Symbol** startet bzw.
                    stoppt ihn direkt, der **Mülleimer** entfernt ihn (nur mit ausreichender Rolle).
                    """,
                    Img("Dashboard mit Server-Karten und aufgeklappten Container-Gruppen", "/help/dashboard.png")),
                new("Schnell zur richtigen Stelle", """
                    Viele Stellen verlinken direkt: Klickst du z. B. eine **Image-Update-Benachrichtigung** an,
                    landest du sofort auf der Detailseite des betroffenen Containers, auch wenn er auf einem
                    anderen Server läuft.
                    """),
            }),

        new("container", "Container verwalten", "ViewInAr",
            "Detailansicht: Statistiken, Logs, Terminal, Umgebung, Datenbank, CVEs.",
            new List<HelpSection>
            {
                new("Die Detailansicht", """
                    Die Container-Detailseite bündelt alles in Tabs:

                    - **Übersicht**: ID, Image, Ports, Labels, Erstellzeit.
                    - **Statistiken**: Live-CPU/RAM/Netz/Disk plus historische Verlaufs-Charts (1 h bis 7 Tage).
                    - **Logs**: Live-Logfenster mit Filter.
                    - **Zustand**: Health-Historie (Status, Exit-Code).
                    - **Terminal**: interaktive Shell im Container (wo unterstützt).
                    - **Umgebungsvariablen**: laufende Variablen (sensible maskiert) und, bei Compose, 
                      die `.env`-Datei bearbeitbar.
                    - **Datenbank**: nur bei DB-Containern: Query-Builder, Tabellen-Browser, Backup, Migration/Seed.
                    - **CVEs**: die Sicherheitslücken im Image.
                    """,
                    Shot("Container-Detailseite mit Tab-Leiste")),
                new("Aktionen", """
                    Oben rechts: **Starten / Stoppen / Neustarten** (Operator) und **Entfernen** (Admin).
                    Jede Aktion wird im **Audit-Protokoll** festgehalten.
                    """),
                new("Umgebungsvariablen ändern", """
                    Im Tab *Umgebungsvariablen* kannst du bei Compose-Projekten die `.env` bearbeiten.
                    Sensible Keys (Passwörter, Tokens) bleiben maskiert, zum Überschreiben auf das Stift-Symbol.
                    **Achtung:** Speichern startet den Container neu (kurze Downtime).
                    """),
            }),

        new("cve", "CVE-Monitor", "Security",
            "Schwachstellen-Scan der Images, Schweregrad, Fixes und Alter.",
            new List<HelpSection>
            {
                new("Wie der Scan funktioniert", """
                    Der **CVE-Monitor** scannt Container-Images (Trivy) und das Host-OS auf bekannte
                    Schwachstellen. Jede CVE erscheint **einmal**: alle betroffenen Container/Server stehen
                    dahinter, statt dieselbe CVE dutzendfach zu listen.

                    Pro Finding siehst du **Schweregrad** (Critical/High/Medium/Low), die **CVE-ID** (verlinkt),
                    das betroffene **Paket**, die installierte Version und, falls vorhanden, die **Fix-Version**.
                    """,
                    Img("CVE-Monitor mit deduplizierter Findings-Liste", "/help/cve-monitor.png")),
                new("Alter & Scan-Intervall", """
                    Zu jeder CVE wird das **Alter** geführt, sowohl seit wann sie in *deiner* Umgebung
                    auftaucht als auch das offizielle Veröffentlichungsdatum. So erkennst du Altlasten.

                    Der Scan läuft **nicht** bei jedem Seitenaufruf, sondern im Hintergrund (Standard alle 12 h)
                    und lässt sich manuell anstoßen. Benachrichtigt wird nur bei **neuen** CVEs.
                    """),
                new("Fixes einspielen", """
                    „Fix available" heißt: Es gibt eine gepatchte Paketversion. Bei Images bedeutet das meist
                    ein **Image-Update** (neueres Tag ziehen + Container neu erstellen), siehe *Deployment*.
                    Beachte: Manchmal ist der Fix upstream noch nicht in ein lauffähiges Image eingeflossen.
                    """),
            }),

        new("logs", "Log-Suche & Alerts", "Search",
            "Container-Logs durchsuchen und Alarm-Regeln einrichten.",
            new List<HelpSection>
            {
                new("Logs durchsuchen", """
                    Auf der Seite **Log-Suche** gibst du einen Suchbegriff (oder mit Schalter *Regex* einen
                    regulären Ausdruck) ein und schränkst optional auf einen **Container** ein. Praktisch zur
                    schnellen Fehlersuche über Container hinweg.
                    """,
                    Shot("Log-Suche mit Suchfeld, Regex-Schalter und Container-Auswahl")),
                new("Alarm-Regeln", """
                    Unter **Alert-Regeln** legst du Muster an, auf die Whiskers das Log laufend prüft. Jede
                    Regel hat **Name**, **Muster** (Text oder Regex), **Container** (oder *Alle*) und **Severity**
                    (error/critical). Schlägt eine Regel an, kommt eine Benachrichtigung, und optional ein
                    **AI-Trigger** (siehe *AI-Trigger*), der den Agenten automatisch reagieren lässt.

                    Tipp: Halte Muster eng genug, um Rauschen zu vermeiden, aber so, dass echte
                    Service-Exceptions und Crashes weiter zuverlässig erfasst werden.
                    """),
            }),

        new("infrastruktur", "Infrastruktur & Server", "Storage",
            "Server hinzufügen, Cloud-Steuerung, Netzwerke und Backups.",
            new List<HelpSection>
            {
                new("Wie Whiskers Server erreicht", """
                    Whiskers spricht jeden Host über die **Docker-API** an, entweder lokal, über einen
                    **SSH-Tunnel** oder über **TCP mit gegenseitigem TLS (mTLS)**. Für Host-Befehle (Firewall,
                    Nginx, systemd ...) startet es einen kurzlebigen, privilegierten Helfer-Container, der per
                    `nsenter` in den Host springt, **ohne** dauerhaften SSH-Schlüssel.
                    """,
                    Diagram("Verbindungs-Architektur", SvgArchitecture)),
                new("Einen Server hinzufügen", """
                    Unter **Infrastruktur > Server > Hinzufügen** legst du einen Host an:

                    1. **Name** und **Verbindungstyp** (Local / SSH / TCP+mTLS).
                    2. Verbindungsdaten (Host, Port, ggf. Zertifikate/Schlüssel).
                    3. Optional **Cloud-Provider** (Hetzner/Hostinger) + API-Key für Out-of-Band-Steuerung.

                    Beim **Speichern** baut Whiskers die Verbindung gleich auf und **testet sie** (kurzes
                    Timeout): Klappt es, schließt der Dialog mit der Container-Zahl; klappt es nicht, bleibt er
                    offen und zeigt den Fehler, so wird kein toter Server unbemerkt gespeichert. Ein nicht
                    erreichbarer Host wird im Dashboard als **„nicht erreichbar"** markiert, statt die ganze
                    Übersicht leer zu machen.
                    """,
                    Shot("Server-Anlegen-Formular mit Verbindungstyp und Cloud-Provider")),
                new("Onboarding, SSH-frei werden", """
                    Für einen frischen Host bringt dich **„Speichern & Onboarden"** (bei Verbindungstyp **SSH**)
                    in einem Rutsch ins Mesh: Über **eine einmalige SSH-Bootstrap-Verbindung**: wahlweise mit
                    **SSH-Key oder Root-Passwort**: installiert Whiskers (falls nötig) Docker, bringt Tailscale
                    hoch (der Login-Link erscheint direkt in der App), deployt Telemetrie + den **mTLS-Proxy** und
                    stellt den Host auf **TCP+mTLS** um.

                    Danach ist der Server **SSH-frei**: und die Bootstrap-Zugangsdaten werden **automatisch
                    entfernt**: das Passwort aus dem Speicher, der SSH-Key von der Platte. Es bleibt kein stehender
                    Zugang übrig.
                    """),
                new("Cloud, Netzwerke, Backups", """
                    - **Cloud**: Power on/off, Reboot, Snapshots und Metriken direkt beim Provider, selbst wenn
                      SSH/Docker mal nicht erreichbar ist. Funktioniert sobald pro Server Provider + API-Key gesetzt sind.
                    - **Netzwerke**: Docker-Netzwerke anlegen, Container verbinden/trennen.
                    - **Backups**: Volume-Backups als komprimierte Archive; vor riskanten Aktionen empfohlen.
                    """),
            }),

        new("kubernetes", "Kubernetes (k3s)", "Lan",
            "Cluster verbinden, Pods im Dashboard, Whiskers per Helm betreiben.",
            new List<HelpSection>
            {
                new("Cluster verbinden", """
                    Unter **Infrastruktur > Server > Hinzufügen** legst du einen Server vom Typ **Kubernetes**
                    an und fügst die **kubeconfig** ein — sie wird **verschlüsselt im Vault** gespeichert,
                    nie im Klartext auf der Platte. Ausgelegt auf **k3s**, funktioniert mit jedem
                    erreichbaren Cluster.
                    """),
                new("Pods im Dashboard", """
                    Pods erscheinen wie Container im **Dashboard**, gruppiert nach ihrem Besitzer
                    (**Deployment/StatefulSet/DaemonSet**), mit Status und **Logs**. Als Aktionen stehen
                    **Skalieren** und **Rollout-Neustart** bereit — bewusst ehrlich gehalten: Was Kubernetes
                    selbst heilt, verspricht Whiskers nicht doppelt.
                    """),
                new("Whiskers auf Kubernetes", """
                    Andersherum läuft Whiskers selbst per **Helm-Chart** auf deinem Cluster
                    (`oci://ghcr.io/lupusmalusdeviant/charts/whiskers`) — Single-Replica by design, non-root,
                    restricted PodSecurity, Daten auf einem PVC.
                    """),
            }),

        new("deployment", "Deployment", "RocketLaunch",
            "Bereitstellen, Compose-Editor, App Store, Git-Deploy, Registries und Updates.",
            new List<HelpSection>
            {
                new("Container bereitstellen", """
                    Unter **Deployment > Bereitstellen** startest du einen neuen Container: Image, Name, Ports,
                    Volumes, Umgebungsvariablen und Restart-Policy, wie ein geführtes `docker run`.
                    """,
                    Shot("Bereitstellen-Formular")),
                new("Compose-Editor & App Store", """
                    - **Compose Editor**: `docker-compose.yml` direkt im Browser bearbeiten und deployen.
                    - **App Store**: kuratierte Vorlagen (Redis, Nginx, Ghost ...) als Startpunkt; Platzhalter
                      wie `{PROJECT}`/`{PORT}` werden beim Deploy ersetzt.
                    """),
                new("Git-Deployments", """
                    Unter **Deployment > Git-Deploy** verbindest du ein Git-Repository mit einem Zielserver:
                    Whiskers klont bzw. pullt das Repo dort und bringt es per **Docker Compose** hoch. Der
                    Zugriffs-Token liegt **nur im Vault** und wird git ausschließlich flüchtig gereicht.
                    Zusammen mit der Webhook-Aktion **git-deploy** wird daraus Push-to-Deploy direkt aus
                    deiner CI.
                    """),
                new("Private Registries", """
                    In **Einstellungen > Registries** hinterlegst du private Container-Registries (GHCR,
                    Harbor ...); die Zugangsdaten liegen im **Vault**. Image-Pulls authentifizieren sich
                    automatisch, sobald der Registry-Host der Image-Referenz passt.
                    """),
                new("Image-Updates & Rollback", """
                    Whiskers erkennt neuere Image-Versionen und aktualisiert Container auf Wunsch
                    **automatisch** (opt-in, auch als geplante Aufgabe) — oder du stößt das Update manuell an.
                    Vor jedem Update wird die alte Container-Konfiguration samt Image-Stand als **Snapshot**
                    festgehalten; über den **Rollback-Knopf** auf dem Dashboard kehrst du mit einem Klick zum
                    letzten funktionierenden Stand zurück.
                    """),
            }),

        new("zeitplaene-webhooks", "Geplante Aufgaben & Webhooks", "Schedule",
            "Wiederkehrende Aktionen planen und Aktionen von außen anstoßen.",
            new List<HelpSection>
            {
                new("Geplante Aufgaben", """
                    Unter **Automatisierung > Geplante Aufgaben** planst du wiederkehrende Aktionen per
                    **Cron-Ausdruck**: Container-Neustarts, Image-Updates, Selbst-Backups, Aufräum-Jobs und
                    mehr. Jeder Lauf landet im Audit-Protokoll.
                    """),
                new("Eingehende Webhooks", """
                    **Webhooks** stoßen Aktionen von außen an, z. B. ein Redeploy oder Git-Deploy aus der CI.
                    Jeder Webhook hat ein **Pflicht-Secret**; Aufrufe müssen über den Raw-Body per
                    **HMAC-Signatur** (`X-Hub-Signature-256`) signiert sein — kompatibel mit GitHub, GitLab
                    und Gitea. Das Secret wird genau **einmal** angezeigt.
                    """),
            }),

        new("backup", "Backup & Wiederherstellung", "Archive",
            "Whiskers' eigene Daten sichern und crash-sicher zurückspielen.",
            new List<HelpSection>
            {
                new("Selbst-Backup", """
                    Unter **Einstellungen > Backup** sichert Whiskers sein **eigenes Datenverzeichnis**
                    (Server-Liste, Vault, Metriken, Schlüssel) als tar.gz — auf Wunsch **verschlüsselt**
                    (AES-256-GCM, abgeleitet vom `VAULT_KEY`). Auch als **geplante Aufgabe** mit
                    Aufbewahrungsregel. Davon getrennt gibt es weiterhin **Volume-Backups** für die Daten
                    deiner Container.
                    """),
                new("Wiederherstellen", """
                    Ein Backup spielst du per Upload zurück: Whiskers **validiert** es, legt automatisch ein
                    **Pre-Restore-Backup** an, wechselt in den **Wartungsmodus** und tauscht die Daten
                    **crash-sicher beim Neustart**. Verschlüsselte Backups brauchen zum Entschlüsseln
                    denselben `VAULT_KEY`.
                    """),
            }),

        new("agent", "Der KI-Agent", "SmartToy",
            "LLM-Anbindung, Chat-Widget, Vision und der Wechsel Berater > handelnder Agent.",
            new List<HelpSection>
            {
                new("Einrichten", """
                    Unter **Automatisierung > Agent** verbindest du ein LLM: **Anbieter** (OpenAI/Anthropic/
                    Gemini ...), **API-Key** und **Modell**. Beim Einfügen des Keys testet Whiskers die
                    Verbindung und füllt die Modellliste automatisch.
                    """,
                    Img("Agent-Konfiguration mit Anbieter, Key-Test und Modell-Dropdown", "/help/agent.png")),
                new("Berater vs. handelnder Agent", """
                    Das **schwebende Chat-Widget** (unten rechts) ist auf der gesamten Seite verfügbar. Es kennt
                    die **aktuell geöffnete Seite** (liest den angezeigten Inhalt) und kann auf Wunsch einen
                    **Screenshot** ans Vision-Modell mitschicken.

                    - **Deaktiviert/Berater**: der Agent erklärt und schlägt vor, führt aber nichts aus.
                    - **Aktiviert/handelnd**: der Agent darf Werkzeuge benutzen (Container neu starten, Befehle
                      ausführen ...), immer begrenzt durch die **Guardrails** und ggf. **Freigaben**.
                    """,
                    Diagram("Berater > handelnder Agent (mit Guardrail-Grenze)", SvgAgent)),
                new("Das Widget bedienen", """
                    Das Fenster ist **verschiebbar** (am Kopf ziehen) und **größenveränderbar** (untere Ecke).
                    Eingabe mit **Enter** senden, **Shift+Enter** für eine neue Zeile. Antworten können
                    **Live-Widgets** enthalten, z. B. ein CPU/RAM-Chart oder eine Status-Karte direkt im Chat.
                    """),
            }),

        new("guardrails", "Guardrails", "Shield",
            "Code-erzwungene Sicherheits-Policy für den Agenten.",
            new List<HelpSection>
            {
                new("Was Guardrails tun", """
                    **Guardrails** sind eine **unumgängliche** Policy, die am **Werkzeug-Boundary** durchgesetzt
                    wird, nicht bloß im Prompt. Selbst wenn das Modell etwas Verbotenes versucht, wird es im Code
                    geblockt. Nur **Admins** können Guardrails ändern.
                    """,
                    Img("Guardrails-Seite mit Preset-Auswahl und Werkzeug-Raster", "/help/guardrails.png")),
                new("Presets & Werkzeug-Modi", """
                    Du legst mehrere **Presets** an und schaltest das aktive um. Pro Werkzeug wählst du den Modus:

                    - **Erlauben**: frei nutzbar.
                    - **Bestätigen**: erzeugt eine **Freigabe** (siehe nächstes Kapitel), bevor es läuft.
                    - **Sperren**: komplett verboten.
                    """),
            }),

        new("freigaben", "Freigaben (Human-in-the-Loop)", "Approval",
            "Heikle Agent-Aktionen genehmigen oder ablehnen.",
            new List<HelpSection>
            {
                new("Wie Freigaben funktionieren", """
                    Wenn der Agent eine Aktion auslöst, die laut Guardrail eine **Bestätigung** braucht,
                    entsteht eine **Freigabe**: Was, von welchem Agenten/Akteur, welches Werkzeug, mit welchen
                    Parametern, plus optionalem Diff. Du bekommst eine Push-Benachrichtigung an die Glocke
                    (und ggf. Mattermost/Matrix).
                    """,
                    Shot("Freigaben-Seite mit ausstehenden Anfragen")),
                new("Genehmigen / Ablehnen", """
                    Auf der Seite **Freigaben** klickst du **Genehmigen** oder **Ablehnen**. Genehmigst du, läuft
                    die Aktion weiter; lehnst du ab, bricht der Agent sie sauber ab. Abgelaufene Anfragen verfallen
                    automatisch.
                    """),
            }),

        new("agent-history", "Agent-History", "Policy",
            "Lückenlose Aufzeichnung jedes Werkzeug-Aufrufs.",
            new List<HelpSection>
            {
                new("MCP-Beobachtbarkeit", """
                    Die **Agent-History** protokolliert **jeden** Werkzeug-Aufruf: Werkzeug, Akteur/Key,
                    Parameter (sensible Werte redigiert), Entscheidung (erlaubt/abgelehnt/bestätigt), Ergebnis
                    bzw. Fehler, Dauer und Server. So ist jederzeit nachvollziehbar, was die KI getan hat.
                    """,
                    Shot("Agent-History mit filterbarer Aufruf-Liste")),
                new("Filtern & Details", """
                    Filtere nach **Akteur**, **Werkzeug**, **Zeitraum** oder nur **Schreibzugriffe/Ablehnungen**.
                    Ein Klick öffnet die Detailansicht mit Parametern und Ergebnis. Aufbewahrung wie das
                    Audit-Protokoll (90 Tage).
                    """),
            }),

        new("ai-trigger", "AI-Trigger", "Bolt",
            "Den Agenten automatisch auf Ereignisse reagieren lassen.",
            new List<HelpSection>
            {
                new("Automatische Reaktionen", """
                    Ein **AI-Trigger** verbindet ein Ereignis (z. B. eine angeschlagene **Log-Alarm-Regel** oder
                    ein ungesunder Container) mit einem autonomen Agent-Lauf. Du legst fest, **wann** er feuert und
                    **welche Anweisung** der Agent dann bekommt.

                    Auch hier gilt: Der autonome Lauf ist durch **Guardrails** und **Freigaben** begrenzt, 
                    heikle Schritte landen weiterhin zur Bestätigung bei dir.
                    """,
                    Shot("AI-Trigger-Liste mit Auslöser und Anweisung")),
            }),

        new("benachrichtigungen", "Benachrichtigungen", "Notifications",
            "Die Glocke, Kanäle und Deep-Links.",
            new List<HelpSection>
            {
                new("Die Glocke", """
                    Die **Glocke** oben rechts sammelt Ereignisse: Image-Updates, ungesunde/abgestürzte Container,
                    neue CVEs, Log-Alarme, Freigaben, Metrik-Ausreißer u. v. m. Die Zahl zeigt Ungelesene; ein
                    Klick öffnet die Liste und markiert alles als gelesen.
                    """,
                    Shot("Geöffnete Benachrichtigungs-Liste")),
                new("Kanäle & Deep-Links", """
                    Neben der In-App-Glocke kann Whiskers zusätzlich an **Mattermost** oder **Matrix** senden.
                    Viele Benachrichtigungen sind **klickbar** und springen direkt zur richtigen Stelle, 
                    z. B. ein Image-Update direkt zum betroffenen Container.
                    """),
            }),

        new("einstellungen", "Einstellungen", "Settings",
            "Bearbeitbare App-Einstellungen und Metrik-Alarme.",
            new List<HelpSection>
            {
                new("Einstellungen ändern", """
                    Unter **Einstellungen** passt du das Verhalten an, gruppiert nach Themen. Dazu gehören
                    **Metrik-Alarme**: Schwellen für CPU/RAM, bei deren Überschreiten du benachrichtigt wirst.
                    Einstellungen sind nur für **Admins** schreibbar.
                    """,
                    Shot("Einstellungen-Seite mit Gruppen und Metrik-Schwellen")),
            }),

        new("mcp", "MCP-Server (KI-Anbindung)", "Hub",
            "Externe KI-Clients wie Claude Code anbinden.",
            new List<HelpSection>
            {
                new("Was MCP ist", """
                    Whiskers stellt seine Fähigkeiten über das **Model Context Protocol (MCP)** bereit. Ein
                    externer KI-Client (z. B. **Claude Code**) verbindet sich mit dem MCP-Endpunkt und kann dann, 
                    im Rahmen seiner Berechtigung, Container listen, Logs lesen, Befehle ausführen usw.
                    """,
                    Diagram("MCP-Fluss: KI-Client > Berechtigung > Werkzeuge > Server", SvgMcp)),
                new("API-Keys & Berechtigungen", """
                    In den **Einstellungen > MCP** legst du **API-Keys** an. Jeder Key hat eine Stufe:

                    - **Read**: nur lesen (listen, inspizieren, Logs).
                    - **Write**: zusätzlich verändern (start/stop, deploy, Befehle).
                    - **Admin**: alles.

                    Alternativ schränkst du einen Key auf eine **explizite Werkzeugliste** ein. Jeder Aufruf
                    landet in der **Agent-History**.
                    """),
            }),

        new("weiteres", "Topologie, Vergleichen, Audit & Health", "Insights",
            "Die übrigen Analyse- und Nachvollziehbarkeits-Werkzeuge.",
            new List<HelpSection>
            {
                new("Topologie", """
                    **Übersicht > Topologie** zeichnet ein Netzwerk-Diagramm: welche Container in welchen
                    Docker-Netzwerken hängen. Gut, um Abhängigkeiten und Isolation auf einen Blick zu sehen.
                    """,
                    Img("Topologie-Graph der Container und Netzwerke", "/help/topologie.png")),
                new("Vergleichen & Audit-Protokoll", """
                    - **Vergleichen**: stellt Konfigurationen/Zustände gegenüber, um Abweichungen zu finden.
                    - **Audit-Protokoll**: lückenlose Historie aller *Benutzer*-Aktionen (wer hat wann was
                      gestartet/gestoppt/geändert). Ergänzt die *Agent-History* (KI-Aktionen).
                    """),
                new("Statusberichte (Health)", """
                    **Übersicht > Statusberichte** fasst den Gesundheitszustand der Flotte zusammen, ungesunde
                    Container, Restart-Loops und auffällige Hosts auf einen Blick.
                    """),
            }),

        new("themes", "Themes, Sprache & Branding", "Palette",
            "Aussehen und Sprache umstellen.",
            new List<HelpSection>
            {
                new("Theme & Modus wechseln", """
                    Über das **Paletten-Icon** oben rechts wählst du den Modus — **Hell**, **Dunkel** oder
                    **System** (folgt deinem Betriebssystem live) — und das Farbschema (Ember, Aurora, Ocean,
                    Nebula, Rose). Die Wahl wird im Browser gespeichert. Das **Logo** in der Sidebar färbt sich
                    passend zum aktiven Theme.
                    """,
                    Shot("Theme-Auswahl im Paletten-Menü")),
                new("Sprache", """
                    Im selben Menü stellst du die **Sprache** um (Deutsch/Englisch). Ohne eigene Wahl folgt
                    Whiskers der Browser-Sprache; Englisch ist der Standard.
                    """),
            }),
    };

    // ----- Theme-aware SVG diagrams (inherit CSS variables from the page) -----

    private const string SvgLayout = """
        <svg viewBox="0 0 640 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;font-family:inherit;">
          <rect x="8" y="8" width="624" height="284" rx="12" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <rect x="8" y="8" width="624" height="36" rx="12" fill="var(--sw-bg-glass-strong)" stroke="var(--sw-glass-border)"/>
          <text x="24" y="31" fill="var(--sw-text-secondary)" font-size="13">Topbar, Glocke · Theme · Profil</text>
          <circle cx="560" cy="26" r="6" fill="var(--sw-accent-primary)"/>
          <circle cx="588" cy="26" r="6" fill="var(--sw-text-muted)"/>
          <circle cx="610" cy="26" r="6" fill="var(--sw-text-muted)"/>
          <rect x="20" y="56" width="150" height="224" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="34" y="84" fill="var(--sw-text-primary)" font-size="13" font-weight="600">Sidebar</text>
          <rect x="34" y="98" width="120" height="12" rx="6" fill="var(--sw-accent-primary-soft)"/>
          <rect x="34" y="120" width="100" height="10" rx="5" fill="var(--sw-glass-hover)"/>
          <rect x="34" y="138" width="110" height="10" rx="5" fill="var(--sw-glass-hover)"/>
          <rect x="34" y="156" width="90" height="10" rx="5" fill="var(--sw-glass-hover)"/>
          <rect x="190" y="56" width="430" height="224" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="206" y="84" fill="var(--sw-text-primary)" font-size="13" font-weight="600">Inhaltsbereich, die geöffnete Seite</text>
          <rect x="206" y="100" width="398" height="60" rx="8" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <rect x="206" y="172" width="190" height="90" rx="8" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <rect x="414" y="172" width="190" height="90" rx="8" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <circle cx="592" cy="252" r="20" fill="var(--sw-accent-primary)"/>
          <text x="592" y="257" fill="#fff" font-size="16" text-anchor="middle">🐱</text>
          <text x="500" y="290" fill="var(--sw-text-secondary)" font-size="11" text-anchor="middle">KI-Widget</text>
        </svg>
        """;

    private const string SvgArchitecture = """
        <svg viewBox="0 0 640 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;font-family:inherit;">
          <rect x="20" y="96" width="150" height="70" rx="12" fill="var(--sw-accent-primary-soft)" stroke="var(--sw-accent-primary)"/>
          <text x="95" y="126" fill="var(--sw-text-primary)" font-size="13" font-weight="600" text-anchor="middle">Whiskers</text>
          <text x="95" y="146" fill="var(--sw-text-secondary)" font-size="11" text-anchor="middle">Control Plane</text>
          <line x1="170" y1="131" x2="250" y2="131" stroke="var(--sw-accent-primary)" stroke-width="2"/>
          <text x="210" y="122" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">mTLS / SSH</text>
          <rect x="250" y="20" width="180" height="60" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="340" y="46" fill="var(--sw-text-primary)" font-size="12" text-anchor="middle">Docker-Host A</text>
          <text x="340" y="64" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Docker-API + nsenter-Helfer</text>
          <rect x="250" y="100" width="180" height="60" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="340" y="126" fill="var(--sw-text-primary)" font-size="12" text-anchor="middle">Docker-Host B</text>
          <text x="340" y="144" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">kein dauerhafter SSH-Key</text>
          <rect x="250" y="180" width="180" height="60" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="340" y="206" fill="var(--sw-text-primary)" font-size="12" text-anchor="middle">Docker-Host C</text>
          <text x="340" y="224" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Container · Logs · Metriken</text>
          <line x1="200" y1="120" x2="250" y2="50" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <line x1="200" y1="131" x2="250" y2="130" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <line x1="200" y1="142" x2="250" y2="210" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <rect x="470" y="100" width="150" height="60" rx="10" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <text x="545" y="126" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Cloud-API</text>
          <text x="545" y="144" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Power · Snapshots</text>
          <line x1="430" y1="130" x2="470" y2="130" stroke="var(--sw-glass-border)" stroke-width="1.5" stroke-dasharray="4 3"/>
        </svg>
        """;

    private const string SvgAgent = """
        <svg viewBox="0 0 640 240" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;font-family:inherit;">
          <rect x="20" y="40" width="160" height="70" rx="12" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="100" y="70" fill="var(--sw-text-primary)" font-size="13" font-weight="600" text-anchor="middle">Berater</text>
          <text x="100" y="90" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">erklärt · schlägt vor</text>
          <rect x="20" y="130" width="160" height="70" rx="12" fill="var(--sw-accent-primary-soft)" stroke="var(--sw-accent-primary)"/>
          <text x="100" y="160" fill="var(--sw-text-primary)" font-size="13" font-weight="600" text-anchor="middle">Handelnder Agent</text>
          <text x="100" y="180" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">darf Werkzeuge nutzen</text>
          <text x="100" y="225" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">aktivieren ↑</text>
          <line x1="180" y1="165" x2="250" y2="165" stroke="var(--sw-accent-primary)" stroke-width="2"/>
          <rect x="250" y="120" width="150" height="90" rx="12" fill="var(--sw-bg-secondary)" stroke="var(--sw-accent-primary)" stroke-dasharray="5 4"/>
          <text x="325" y="150" fill="var(--sw-text-primary)" font-size="12" font-weight="600" text-anchor="middle">Guardrails</text>
          <text x="325" y="170" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">erlauben / bestätigen</text>
          <text x="325" y="184" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">/ sperren</text>
          <line x1="400" y1="150" x2="460" y2="120" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <line x1="400" y1="175" x2="460" y2="190" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <rect x="460" y="90" width="160" height="56" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="540" y="114" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Werkzeuge laufen</text>
          <text x="540" y="132" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Aktion am Server</text>
          <rect x="460" y="160" width="160" height="56" rx="10" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <text x="540" y="184" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Freigabe nötig</text>
          <text x="540" y="202" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">du bestätigst</text>
        </svg>
        """;

    private const string SvgMcp = """
        <svg viewBox="0 0 640 200" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;font-family:inherit;">
          <rect x="20" y="70" width="150" height="60" rx="12" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="95" y="96" fill="var(--sw-text-primary)" font-size="12" text-anchor="middle">KI-Client</text>
          <text x="95" y="114" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">z. B. Claude Code</text>
          <line x1="170" y1="100" x2="235" y2="100" stroke="var(--sw-accent-primary)" stroke-width="2"/>
          <text x="202" y="91" fill="var(--sw-text-secondary)" font-size="9" text-anchor="middle">MCP + API-Key</text>
          <rect x="235" y="70" width="160" height="60" rx="12" fill="var(--sw-accent-primary-soft)" stroke="var(--sw-accent-primary)"/>
          <text x="315" y="96" fill="var(--sw-text-primary)" font-size="12" font-weight="600" text-anchor="middle">Berechtigung</text>
          <text x="315" y="114" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Read / Write / Admin</text>
          <line x1="395" y1="100" x2="460" y2="100" stroke="var(--sw-accent-primary)" stroke-width="2"/>
          <rect x="460" y="40" width="160" height="50" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="540" y="70" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Werkzeuge</text>
          <rect x="460" y="110" width="160" height="50" rx="10" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <text x="540" y="132" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Agent-History</text>
          <text x="540" y="149" fill="var(--sw-text-secondary)" font-size="9" text-anchor="middle">jeder Aufruf protokolliert</text>
        </svg>
        """;
}

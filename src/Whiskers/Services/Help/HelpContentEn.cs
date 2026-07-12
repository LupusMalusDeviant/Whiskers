using Whiskers.Models.Help;

namespace Whiskers.Services.Help;

/// <summary>
/// The English chapters of the in-app user handbook — the default for every non-German culture.
/// Must be kept structurally identical (chapter ids, order, section counts, figures) to the German
/// set in <see cref="HelpContentService"/>; only the user-visible strings differ.
/// </summary>
internal static class HelpContentEn
{
    private static HelpFigure Shot(string caption) => new(HelpFigureKind.Screenshot, caption);
    private static HelpFigure Img(string caption, string path) => new(HelpFigureKind.Image, caption, Image: path);
    private static HelpFigure Diagram(string caption, string svg) => new(HelpFigureKind.Svg, caption, svg);

    internal static readonly IReadOnlyList<HelpChapter> Chapters = new List<HelpChapter>
    {
        new("erste-schritte", "Getting Started", "Rocket",
            "What Whiskers is, signing in, and how the interface is laid out.",
            new List<HelpSection>
            {
                new("What is Whiskers?", """
                    **Whiskers** is your central web control plane for a fleet of Docker hosts:
                    containers, images, networks, databases, firewall, Nginx, systemd, SSL, metrics and
                    logs — all in one place. The same capabilities are optionally available to an **AI**
                    through a locked-down **MCP endpoint** (see the *AI Agent* and *MCP Server* chapters).

                    The design goal is **SSH-key-free operation**: hosts are reached over a private mesh
                    with mutual TLS (mTLS), so there is no long-lived private key lying around for anyone
                    to steal.
                    """),
                new("First Launch & Signing In", """
                    On **first launch**, the **setup wizard** walks you through the initial configuration
                    in the browser: create the admin account, back up the key once — no configuration file
                    required.

                    After that you sign in either **locally** (email + password) or via **single sign-on**
                    (Google/OIDC). Only whitelisted email addresses get access. Your role determines what
                    you can do:

                    - **Viewer**: see everything, change nothing.
                    - **Operator**: start/stop/restart containers, run commands.
                    - **Admin**: everything, including adding servers, guardrails, settings, deletion.
                    """),
                new("How the Interface Is Laid Out", """
                    The interface consists of four areas:

                    1. **Sidebar (left)**: the navigation, grouped by topic (Overview, Deployment,
                       Infrastructure, Automation) plus *Settings* and *Help*.
                    2. **Topbar (top)**: the **bell** (notifications), the **palette icon**
                       (switch theme) and your profile/sign-out.
                    3. **Content area**: whatever page is currently open.
                    4. **AI widget (bottom right)**: the floating agent chat, available on every page
                       (when the agent is enabled).
                    """,
                    Diagram("Layout of the Whiskers interface", SvgLayout)),
            }),

        new("dashboard", "Dashboard & Overview", "Dashboard",
            "Server cards, container groups, status and quick actions.",
            new List<HelpSection>
            {
                new("Server and Container Cards", """
                    The **Dashboard** shows every server with its containers. Containers are grouped into
                    **standalone**, **Compose project** and **telemetry**. For each container you see its
                    name, image, **status** (running/exited/created), **health** (healthy/unhealthy),
                    CPU and RAM load, and the status text.

                    Clicking a **container name** opens the detail view; the **▶/■ icon** starts or
                    stops it directly, and the **trash can** removes it (with a sufficient role only).
                    """,
                    Img("Dashboard with server cards and expanded container groups", "/help/dashboard.png")),
                new("Getting to the Right Place Fast", """
                    Many places link straight through: click an **image-update notification**, for
                    example, and you land directly on the detail page of the affected container — even
                    if it runs on a different server.
                    """),
            }),

        new("container", "Managing Containers", "ViewInAr",
            "Detail view: statistics, logs, terminal, environment, database, CVEs.",
            new List<HelpSection>
            {
                new("The Detail View", """
                    The container detail page bundles everything into tabs:

                    - **Overview**: ID, image, ports, labels, creation time.
                    - **Statistics**: live CPU/RAM/network/disk plus historical trend charts (1 h to 7 days).
                    - **Logs**: live log window with filtering.
                    - **Health**: health history (status, exit code).
                    - **Terminal**: interactive shell inside the container (where supported).
                    - **Environment variables**: running variables (sensitive ones masked) and, for Compose,
                      the editable `.env` file.
                    - **Database**: DB containers only: query builder, table browser, backup, migration/seed.
                    - **CVEs**: the vulnerabilities found in the image.
                    """,
                    Shot("Container detail page with tab bar")),
                new("Actions", """
                    Top right: **Start / Stop / Restart** (Operator) and **Remove** (Admin).
                    Every action is recorded in the **audit log**.
                    """),
                new("Changing Environment Variables", """
                    In the *Environment variables* tab you can edit the `.env` file of Compose projects.
                    Sensitive keys (passwords, tokens) stay masked; click the pencil icon to overwrite one.
                    **Careful:** saving restarts the container (brief downtime).
                    """),
            }),

        new("cve", "CVE Monitor", "Security",
            "Vulnerability scanning of images, severity, fixes and age.",
            new List<HelpSection>
            {
                new("How the Scan Works", """
                    The **CVE Monitor** scans container images (Trivy) and the host OS for known
                    vulnerabilities. Each CVE appears **once**: all affected containers/servers are listed
                    behind it, instead of the same CVE showing up dozens of times.

                    For each finding you see the **severity** (Critical/High/Medium/Low), the **CVE ID**
                    (linked), the affected **package**, the installed version and, if available, the
                    **fixed version**.
                    """,
                    Img("CVE Monitor with deduplicated findings list", "/help/cve-monitor.png")),
                new("Age & Scan Interval", """
                    Whiskers tracks the **age** of every CVE — both how long it has been showing up in
                    *your* environment and its official publication date. That makes long-standing
                    stragglers easy to spot.

                    The scan does **not** run on every page load; it runs in the background (every 12 h
                    by default) and can be triggered manually. You are only notified about **new** CVEs.
                    """),
                new("Applying Fixes", """
                    "Fix available" means a patched package version exists. For images that usually means
                    an **image update** (pull a newer tag + recreate the container), see *Deployment*.
                    Note: sometimes the fix hasn't made it into a runnable upstream image yet.
                    """),
            }),

        new("logs", "Log Search & Alerts", "Search",
            "Searching container logs and setting up alert rules.",
            new List<HelpSection>
            {
                new("Searching Logs", """
                    On the **Log Search** page you enter a search term (or a regular expression with the
                    *Regex* toggle) and optionally narrow it down to one **container**. Handy for quick
                    troubleshooting across containers.
                    """,
                    Shot("Log search with search field, regex toggle and container picker")),
                new("Alert Rules", """
                    Under **Alert Rules** you define patterns that Whiskers continuously checks the logs
                    against. Each rule has a **name**, a **pattern** (text or regex), a **container** (or
                    *All*) and a **severity** (error/critical). When a rule fires, you get a notification
                    and, optionally, an **AI trigger** (see *AI Triggers*) that lets the agent react
                    automatically.

                    Tip: keep patterns tight enough to avoid noise, but loose enough that real service
                    exceptions and crashes are still reliably caught.
                    """),
            }),

        new("infrastruktur", "Infrastructure & Servers", "Storage",
            "Adding servers, cloud control, networks and backups.",
            new List<HelpSection>
            {
                new("How Whiskers Reaches Servers", """
                    Whiskers talks to every host through the **Docker API** — either locally, through an
                    **SSH tunnel**, or over **TCP with mutual TLS (mTLS)**. For host commands (firewall,
                    Nginx, systemd ...) it launches a short-lived, privileged helper container that jumps
                    into the host via `nsenter` — **without** a persistent SSH key.
                    """,
                    Diagram("Connection architecture", SvgArchitecture)),
                new("Adding a Server", """
                    Under **Infrastructure > Servers > Add** you register a host:

                    1. **Name** and **connection type** (Local / SSH / TCP+mTLS).
                    2. Connection details (host, port, certificates/keys where needed).
                    3. Optionally a **cloud provider** (Hetzner/Hostinger) + API key for out-of-band control.

                    On **Save**, Whiskers establishes the connection right away and **tests it** (short
                    timeout): if it works, the dialog closes showing the container count; if it doesn't, the
                    dialog stays open and shows the error — so no dead server gets saved unnoticed. An
                    unreachable host is marked **"unreachable"** on the Dashboard instead of blanking out
                    the whole overview.
                    """,
                    Shot("Add-server form with connection type and cloud provider")),
                new("Onboarding, Going SSH-Free", """
                    For a fresh host, **"Save & Onboard"** (with connection type **SSH**) brings it into
                    the mesh in one go: over **a single one-time SSH bootstrap connection** — using either
                    an **SSH key or the root password** — Whiskers installs Docker (if needed), brings up
                    Tailscale (the login link appears right in the app), deploys telemetry + the **mTLS
                    proxy** and switches the host to **TCP+mTLS**.

                    After that the server is **SSH-free** — and the bootstrap credentials are **removed
                    automatically**: the password from memory, the SSH key from disk. No standing access
                    is left behind.
                    """),
                new("Cloud, Networks, Backups", """
                    - **Cloud**: power on/off, reboot, snapshots and metrics straight at the provider —
                      even when SSH/Docker happens to be unreachable. Works as soon as a provider + API key
                      is set per server.
                    - **Networks**: create Docker networks, connect/disconnect containers.
                    - **Backups**: volume backups as compressed archives; recommended before risky actions.
                    """),
            }),

        new("kubernetes", "Kubernetes (k3s)", "Lan",
            "Connecting clusters, pods on the Dashboard, running Whiskers via Helm.",
            new List<HelpSection>
            {
                new("Connecting a Cluster", """
                    Under **Infrastructure > Servers > Add** you create a server of type **Kubernetes**
                    and paste in the **kubeconfig** — it is stored **encrypted in the vault**, never in
                    plain text on disk. Designed for **k3s**, but works with any reachable cluster.
                    """),
                new("Pods on the Dashboard", """
                    Pods appear like containers on the **Dashboard**, grouped by their owner
                    (**Deployment/StatefulSet/DaemonSet**), with status and **logs**. The available
                    actions are **Scale** and **Rollout restart** — deliberately kept honest: what
                    Kubernetes heals on its own, Whiskers doesn't promise twice.
                    """),
                new("Whiskers on Kubernetes", """
                    The other way around, Whiskers itself runs on your cluster via **Helm chart**
                    (`oci://ghcr.io/lupusmalusdeviant/charts/whiskers`) — single replica by design,
                    non-root, restricted PodSecurity, data on a PVC.
                    """),
            }),

        new("deployment", "Deployment", "RocketLaunch",
            "Deploying, Compose editor, App Store, Git deploy, registries and updates.",
            new List<HelpSection>
            {
                new("Deploying Containers", """
                    Under **Deployment > Deploy** you launch a new container: image, name, ports,
                    volumes, environment variables and restart policy — like a guided `docker run`.
                    """,
                    Shot("Deploy form")),
                new("Compose Editor & App Store", """
                    - **Compose Editor**: edit and deploy the `docker-compose.yml` right in the browser.
                    - **App Store**: curated templates (Redis, Nginx, Ghost ...) as a starting point;
                      placeholders like `{PROJECT}`/`{PORT}` are substituted on deploy.
                    """),
                new("Git Deployments", """
                    Under **Deployment > Git Deploy** you connect a Git repository to a target server:
                    Whiskers clones or pulls the repo there and brings it up via **Docker Compose**. The
                    access token lives **only in the vault** and is handed to git strictly transiently.
                    Combined with the **git-deploy** webhook action, this becomes push-to-deploy straight
                    from your CI.
                    """),
                new("Private Registries", """
                    In **Settings > Registries** you store private container registries (GHCR,
                    Harbor ...); the credentials live in the **vault**. Image pulls authenticate
                    automatically whenever the registry host matches the image reference.
                    """),
                new("Image Updates & Rollback", """
                    Whiskers detects newer image versions and, if you want, updates containers
                    **automatically** (opt-in, also as a scheduled task) — or you trigger the update
                    manually. Before every update, the old container configuration including its image
                    state is captured as a **snapshot**; the **rollback button** on the Dashboard takes
                    you back to the last working state with one click.
                    """),
            }),

        new("zeitplaene-webhooks", "Scheduled Tasks & Webhooks", "Schedule",
            "Scheduling recurring actions and triggering actions from outside.",
            new List<HelpSection>
            {
                new("Scheduled Tasks", """
                    Under **Automation > Scheduled Tasks** you schedule recurring actions with a
                    **cron expression**: container restarts, image updates, self-backups, cleanup jobs and
                    more. Every run is recorded in the audit log.
                    """),
                new("Incoming Webhooks", """
                    **Webhooks** trigger actions from outside — a redeploy or Git deploy from your CI,
                    for example. Every webhook has a **mandatory secret**; calls must be signed over the
                    raw body with an **HMAC signature** (`X-Hub-Signature-256`) — compatible with GitHub,
                    GitLab and Gitea. The secret is shown exactly **once**.
                    """),
            }),

        new("backup", "Backup & Restore", "Archive",
            "Backing up Whiskers' own data and restoring it crash-safely.",
            new List<HelpSection>
            {
                new("Self-Backup", """
                    Under **Settings > Backup**, Whiskers backs up its **own data directory**
                    (server list, vault, metrics, keys) as a tar.gz — optionally **encrypted**
                    (AES-256-GCM, derived from the `VAULT_KEY`). Also available as a **scheduled task**
                    with a retention rule. Separate from this, **volume backups** for your containers'
                    data continue to exist.
                    """),
                new("Restoring", """
                    You restore a backup by uploading it: Whiskers **validates** it, automatically creates
                    a **pre-restore backup**, switches into **maintenance mode** and swaps the data in
                    **crash-safely on restart**. Encrypted backups need the same `VAULT_KEY` to decrypt.
                    """),
            }),

        new("agent", "The AI Agent", "SmartToy",
            "LLM connection, chat widget, vision, and the switch from advisor to acting agent.",
            new List<HelpSection>
            {
                new("Setting It Up", """
                    Under **Automation > Agent** you connect an LLM: **provider** (OpenAI/Anthropic/
                    Gemini ...), **API key** and **model**. When you paste the key, Whiskers tests the
                    connection and fills the model list automatically.
                    """,
                    Img("Agent configuration with provider, key test and model dropdown", "/help/agent.png")),
                new("Advisor vs. Acting Agent", """
                    The **floating chat widget** (bottom right) is available across the whole app. It
                    knows the **currently open page** (it reads the displayed content) and can optionally
                    send a **screenshot** along to the vision model.

                    - **Disabled/advisor**: the agent explains and suggests, but executes nothing.
                    - **Enabled/acting**: the agent may use tools (restart containers, run commands ...),
                      always bounded by the **guardrails** and, where required, **approvals**.
                    """,
                    Diagram("Advisor > acting agent (with guardrail boundary)", SvgAgent)),
                new("Using the Widget", """
                    The window is **draggable** (grab the header) and **resizable** (bottom corner).
                    Send with **Enter**, **Shift+Enter** for a new line. Responses can contain
                    **live widgets**, e.g. a CPU/RAM chart or a status card right inside the chat.
                    """),
            }),

        new("guardrails", "Guardrails", "Shield",
            "Code-enforced security policy for the agent.",
            new List<HelpSection>
            {
                new("What Guardrails Do", """
                    **Guardrails** are an **inescapable** policy enforced at the **tool boundary**, not
                    merely in the prompt. Even if the model attempts something forbidden, it is blocked in
                    code. Only **admins** can change guardrails.
                    """,
                    Img("Guardrails page with preset picker and tool grid", "/help/guardrails.png")),
                new("Presets & Tool Modes", """
                    You create multiple **presets** and switch which one is active. For each tool you pick
                    a mode:

                    - **Allow**: freely usable.
                    - **Confirm**: creates an **approval** (see the next chapter) before it runs.
                    - **Block**: completely forbidden.
                    """),
            }),

        new("freigaben", "Approvals (Human-in-the-Loop)", "Approval",
            "Approving or rejecting sensitive agent actions.",
            new List<HelpSection>
            {
                new("How Approvals Work", """
                    When the agent triggers an action that the guardrail says needs a **confirmation**,
                    an **approval** is created: what, by which agent/actor, which tool, with which
                    parameters, plus an optional diff. You get a push notification at the bell
                    (and, if configured, Mattermost/Matrix).
                    """,
                    Shot("Approvals page with pending requests")),
                new("Approve / Reject", """
                    On the **Approvals** page you click **Approve** or **Reject**. If you approve, the
                    action continues; if you reject, the agent aborts it cleanly. Expired requests lapse
                    automatically.
                    """),
            }),

        new("agent-history", "Agent History", "Policy",
            "A complete record of every tool call.",
            new List<HelpSection>
            {
                new("MCP Observability", """
                    The **Agent History** logs **every** tool call: tool, actor/key, parameters
                    (sensitive values redacted), decision (allowed/rejected/confirmed), result or error,
                    duration and server. So you can always trace what the AI actually did.
                    """,
                    Shot("Agent History with filterable call list")),
                new("Filtering & Details", """
                    Filter by **actor**, **tool**, **time range**, or just **write calls/rejections**.
                    One click opens the detail view with parameters and result. Retention matches the
                    audit log (90 days).
                    """),
            }),

        new("ai-trigger", "AI Triggers", "Bolt",
            "Letting the agent react to events automatically.",
            new List<HelpSection>
            {
                new("Automatic Reactions", """
                    An **AI trigger** connects an event (e.g. a fired **log alert rule** or an unhealthy
                    container) to an autonomous agent run. You define **when** it fires and **which
                    instruction** the agent then receives.

                    The same rule applies here: the autonomous run is bounded by **guardrails** and
                    **approvals** — sensitive steps still land on your desk for confirmation.
                    """,
                    Shot("AI trigger list with trigger condition and instruction")),
            }),

        new("benachrichtigungen", "Notifications", "Notifications",
            "The bell, channels and deep links.",
            new List<HelpSection>
            {
                new("The Bell", """
                    The **bell** at the top right collects events: image updates, unhealthy/crashed
                    containers, new CVEs, log alerts, approvals, metric outliers and much more. The badge
                    shows unread items; a click opens the list and marks everything as read.
                    """,
                    Shot("Opened notification list")),
                new("Channels & Deep Links", """
                    Besides the in-app bell, Whiskers can additionally send to **Mattermost** or
                    **Matrix**. Many notifications are **clickable** and jump straight to the right
                    place — an image update, for example, straight to the affected container.
                    """),
            }),

        new("einstellungen", "Settings", "Settings",
            "Editable app settings and metric alerts.",
            new List<HelpSection>
            {
                new("Changing Settings", """
                    Under **Settings** you adjust the behavior, grouped by topic. This includes
                    **metric alerts**: thresholds for CPU/RAM that notify you when exceeded.
                    Settings are writable by **admins** only.
                    """,
                    Shot("Settings page with groups and metric thresholds")),
            }),

        new("mcp", "MCP Server (AI Integration)", "Hub",
            "Connecting external AI clients like Claude Code.",
            new List<HelpSection>
            {
                new("What MCP Is", """
                    Whiskers exposes its capabilities via the **Model Context Protocol (MCP)**. An
                    external AI client (e.g. **Claude Code**) connects to the MCP endpoint and can then —
                    within its permission level — list containers, read logs, run commands and so on.
                    """,
                    Diagram("MCP flow: AI client > permission > tools > server", SvgMcp)),
                new("API Keys & Permissions", """
                    In **Settings > MCP** you create **API keys**. Each key has a level:

                    - **Read**: read only (list, inspect, logs).
                    - **Write**: additionally modify (start/stop, deploy, commands).
                    - **Admin**: everything.

                    Alternatively you restrict a key to an **explicit tool list**. Every call ends up in
                    the **Agent History**.
                    """),
            }),

        new("weiteres", "Topology, Compare, Audit & Health", "Insights",
            "The remaining analysis and traceability tools.",
            new List<HelpSection>
            {
                new("Topology", """
                    **Overview > Topology** draws a network diagram: which containers sit in which
                    Docker networks. Great for seeing dependencies and isolation at a glance.
                    """,
                    Img("Topology graph of containers and networks", "/help/topologie.png")),
                new("Compare & Audit Log", """
                    - **Compare**: puts configurations/states side by side to find deviations.
                    - **Audit log**: a complete history of all *user* actions (who started/stopped/changed
                      what and when). Complements the *Agent History* (AI actions).
                    """),
                new("Health Reports", """
                    **Overview > Health Reports** summarizes the health of the fleet — unhealthy
                    containers, restart loops and conspicuous hosts at a glance.
                    """),
            }),

        new("themes", "Themes, Language & Branding", "Palette",
            "Changing the look and the language.",
            new List<HelpSection>
            {
                new("Switching Theme & Mode", """
                    Via the **palette icon** at the top right you pick the mode — **Light**, **Dark** or
                    **System** (follows your operating system live) — and the color scheme (Ember, Aurora,
                    Ocean, Nebula, Rose). Your choice is stored in the browser. The **logo** in the
                    sidebar recolors to match the active theme.
                    """,
                    Shot("Theme picker in the palette menu")),
                new("Language", """
                    In the same menu you switch the **language** (German/English). Without an explicit
                    choice, Whiskers follows the browser language; English is the default.
                    """),
            }),
    };

    // ----- Theme-aware SVG diagrams (inherit CSS variables from the page) -----

    private const string SvgLayout = """
        <svg viewBox="0 0 640 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;font-family:inherit;">
          <rect x="8" y="8" width="624" height="284" rx="12" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <rect x="8" y="8" width="624" height="36" rx="12" fill="var(--sw-bg-glass-strong)" stroke="var(--sw-glass-border)"/>
          <text x="24" y="31" fill="var(--sw-text-secondary)" font-size="13">Topbar, bell · theme · profile</text>
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
          <text x="206" y="84" fill="var(--sw-text-primary)" font-size="13" font-weight="600">Content area, the open page</text>
          <rect x="206" y="100" width="398" height="60" rx="8" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <rect x="206" y="172" width="190" height="90" rx="8" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <rect x="414" y="172" width="190" height="90" rx="8" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <circle cx="592" cy="252" r="20" fill="var(--sw-accent-primary)"/>
          <text x="592" y="257" fill="#fff" font-size="16" text-anchor="middle">🐱</text>
          <text x="500" y="290" fill="var(--sw-text-secondary)" font-size="11" text-anchor="middle">AI widget</text>
        </svg>
        """;

    private const string SvgArchitecture = """
        <svg viewBox="0 0 640 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;font-family:inherit;">
          <rect x="20" y="96" width="150" height="70" rx="12" fill="var(--sw-accent-primary-soft)" stroke="var(--sw-accent-primary)"/>
          <text x="95" y="126" fill="var(--sw-text-primary)" font-size="13" font-weight="600" text-anchor="middle">Whiskers</text>
          <text x="95" y="146" fill="var(--sw-text-secondary)" font-size="11" text-anchor="middle">Control plane</text>
          <line x1="170" y1="131" x2="250" y2="131" stroke="var(--sw-accent-primary)" stroke-width="2"/>
          <text x="210" y="122" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">mTLS / SSH</text>
          <rect x="250" y="20" width="180" height="60" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="340" y="46" fill="var(--sw-text-primary)" font-size="12" text-anchor="middle">Docker host A</text>
          <text x="340" y="64" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Docker API + nsenter helper</text>
          <rect x="250" y="100" width="180" height="60" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="340" y="126" fill="var(--sw-text-primary)" font-size="12" text-anchor="middle">Docker host B</text>
          <text x="340" y="144" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">no persistent SSH key</text>
          <rect x="250" y="180" width="180" height="60" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="340" y="206" fill="var(--sw-text-primary)" font-size="12" text-anchor="middle">Docker host C</text>
          <text x="340" y="224" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Containers · logs · metrics</text>
          <line x1="200" y1="120" x2="250" y2="50" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <line x1="200" y1="131" x2="250" y2="130" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <line x1="200" y1="142" x2="250" y2="210" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <rect x="470" y="100" width="150" height="60" rx="10" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <text x="545" y="126" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Cloud API</text>
          <text x="545" y="144" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Power · snapshots</text>
          <line x1="430" y1="130" x2="470" y2="130" stroke="var(--sw-glass-border)" stroke-width="1.5" stroke-dasharray="4 3"/>
        </svg>
        """;

    private const string SvgAgent = """
        <svg viewBox="0 0 640 240" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;font-family:inherit;">
          <rect x="20" y="40" width="160" height="70" rx="12" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="100" y="70" fill="var(--sw-text-primary)" font-size="13" font-weight="600" text-anchor="middle">Advisor</text>
          <text x="100" y="90" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">explains · suggests</text>
          <rect x="20" y="130" width="160" height="70" rx="12" fill="var(--sw-accent-primary-soft)" stroke="var(--sw-accent-primary)"/>
          <text x="100" y="160" fill="var(--sw-text-primary)" font-size="13" font-weight="600" text-anchor="middle">Acting agent</text>
          <text x="100" y="180" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">may use tools</text>
          <text x="100" y="225" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">enable ↑</text>
          <line x1="180" y1="165" x2="250" y2="165" stroke="var(--sw-accent-primary)" stroke-width="2"/>
          <rect x="250" y="120" width="150" height="90" rx="12" fill="var(--sw-bg-secondary)" stroke="var(--sw-accent-primary)" stroke-dasharray="5 4"/>
          <text x="325" y="150" fill="var(--sw-text-primary)" font-size="12" font-weight="600" text-anchor="middle">Guardrails</text>
          <text x="325" y="170" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">allow / confirm</text>
          <text x="325" y="184" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">/ block</text>
          <line x1="400" y1="150" x2="460" y2="120" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <line x1="400" y1="175" x2="460" y2="190" stroke="var(--sw-glass-border)" stroke-width="1.5"/>
          <rect x="460" y="90" width="160" height="56" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="540" y="114" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Tools run</text>
          <text x="540" y="132" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">action on the server</text>
          <rect x="460" y="160" width="160" height="56" rx="10" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <text x="540" y="184" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Approval needed</text>
          <text x="540" y="202" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">you confirm</text>
        </svg>
        """;

    private const string SvgMcp = """
        <svg viewBox="0 0 640 200" xmlns="http://www.w3.org/2000/svg" style="width:100%;height:auto;font-family:inherit;">
          <rect x="20" y="70" width="150" height="60" rx="12" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="95" y="96" fill="var(--sw-text-primary)" font-size="12" text-anchor="middle">AI client</text>
          <text x="95" y="114" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">e.g. Claude Code</text>
          <line x1="170" y1="100" x2="235" y2="100" stroke="var(--sw-accent-primary)" stroke-width="2"/>
          <text x="202" y="91" fill="var(--sw-text-secondary)" font-size="9" text-anchor="middle">MCP + API key</text>
          <rect x="235" y="70" width="160" height="60" rx="12" fill="var(--sw-accent-primary-soft)" stroke="var(--sw-accent-primary)"/>
          <text x="315" y="96" fill="var(--sw-text-primary)" font-size="12" font-weight="600" text-anchor="middle">Permission</text>
          <text x="315" y="114" fill="var(--sw-text-secondary)" font-size="10" text-anchor="middle">Read / Write / Admin</text>
          <line x1="395" y1="100" x2="460" y2="100" stroke="var(--sw-accent-primary)" stroke-width="2"/>
          <rect x="460" y="40" width="160" height="50" rx="10" fill="var(--sw-bg-elevated)" stroke="var(--sw-glass-border)"/>
          <text x="540" y="70" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Tools</text>
          <rect x="460" y="110" width="160" height="50" rx="10" fill="var(--sw-bg-secondary)" stroke="var(--sw-glass-border)"/>
          <text x="540" y="132" fill="var(--sw-text-primary)" font-size="11" text-anchor="middle">Agent History</text>
          <text x="540" y="149" fill="var(--sw-text-secondary)" font-size="9" text-anchor="middle">every call logged</text>
        </svg>
        """;
}

# ServerWatch

Docker Container & Server Management Dashboard mit integriertem MCP-Server fuer KI-gesteuerte Infrastrukturverwaltung.

## Features

### Container-Management
- Live-Dashboard mit CPU/Memory/Health-Status aller Container
- Container starten, stoppen, neustarten, loeschen
- Image-Update-Erkennung mit One-Click-Update
- Container-Logs, Stats und Health-Reports
- Gruppierung nach Server und Docker Compose Projekt

### Server-Verwaltung
- Multi-Server-Unterstuetzung (Local, SSH und TCP/mTLS-Mesh)
- Firewall-Management (ufw) ueber Web-UI
- Nginx-Sites verwalten mit Config-Editor
- systemd-Services starten/stoppen/ueberwachen
- SSL-Zertifikate (Let's Encrypt) Status und Renewal
- Integriertes Web-Terminal (Host + Container)

### Sicherheit: Mesh + mTLS (SSH-key-frei)
- Verwaltung ueber ein privates WireGuard-Mesh (Tailscale) — keine Management-Ports oeffentlich
- Docker-Steuerung ueber **mutual TLS** (ghostunnel + verb-whitelisting socket-proxy) statt SSH-Tunnel
- Host-Shell-Befehle SSH-frei ueber denselben mTLS-Kanal (privilegierter `nsenter`-Container)
- **Kein gespeicherter SSH-Key** im Normalbetrieb — die zentrale Angriffsflaeche entfaellt
- Eigene PKI (step-ca) fuer Client-/Server-Zertifikate
- Telemetrie ueber `node_exporter` → VictoriaMetrics (Prometheus-kompatibel)
- **Ein-Klick-Onboarding** neuer Server: installiert Tailscale (Login-Link direkt in der App),
  deployt Telemetrie + mTLS-Proxy und stellt den Server SSH-frei

→ Design-Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

### Cloud-Control (out-of-band)
- Provider-agnostische Power/Snapshot/Metrics-Steuerung (Hetzner, Hostinger) ueber die Provider-API —
  funktioniert auch, wenn SSH/Docker gerade nicht erreichbar sind

### Monitoring & Alerting
- Historische Metriken (CPU, RAM, Disk) in SQLite
- Mattermost-Benachrichtigungen bei:
  - Container unhealthy / gestoppt / OOM
  - Restart-Loops
  - Image-Updates verfuegbar
- Health-Reports mit Zeitverlauf

### Deployment
- Container-Deployment per Formular (Image, Ports, Env, Volumes)
- Docker Compose Upload und Deployment
- Standardisierte App-Vorlagen fuer schnelles Deployment

### MCP-Server (Model Context Protocol)
Integrierter MCP-Server fuer KI-Agenten (z.B. Claude Code):

```json
{
  "mcpServers": {
    "serverwatch": {
      "url": "https://your-server.com/serverwatch/mcp",
      "headers": {
        "Authorization": "Bearer <API-KEY>"
      }
    }
  }
}
```

**Tools** (Berechtigungen pro API-Key als Read / Write / Admin durchgesetzt):
- **Container:** `list_containers`, `get_container_details`, `get_container_logs`, `get_container_metrics`, `get_container_env`, `set_container_env`, `start_container`, `stop_container`, `restart_container`, `update_container`
- **Server & Host:** `list_servers`, `get_server_info`, `get_server_logs`, `get_server_metrics`, `get_health_summary`, `execute_command` (Admin)
- **Deployment:** `deploy_app`, `deploy_compose`
- **Infrastruktur:** Firewall (`list_firewall_rules`, `add_firewall_rule`, `remove_firewall_rule`), Nginx (`list_nginx_sites`, `get_nginx_config`, `update_nginx_config`), systemd (`list_systemd_services`, `manage_systemd_service`), SSL (`list_ssl_certificates`, `renew_ssl_certificate`)
- **Datenbanken:** `detect_database`, `list_databases`, `list_tables`, `get_schema`, `execute_query`, `backup_database`
- **Netzwerke:** `list_networks`, `create_network`, `remove_network`, `connect_container_to_network`, `disconnect_container_from_network`
- **Logs & Alerts:** `search_logs`, `list_log_alerts`, `create_log_alert`
- **Scheduler:** `list_scheduled_tasks`, `create_scheduled_task`, `delete_scheduled_task`, `run_scheduled_task`
- **CVEs & Updates:** `get_cve_summary`, `get_server_cves`, `get_container_cves`, `get_update_status`
- **Cloud (out-of-band, Hetzner/Hostinger):** `list_cloud_servers`, `cloud_status`, `cloud_metrics`, `cloud_power_on`, `cloud_shutdown`, `cloud_reboot`, `cloud_hard_reset`, `cloud_create_snapshot` + Hetzner-Extras (`hetzner_enable_rescue`/`hetzner_disable_rescue`, `hetzner_enable_backups`/`hetzner_disable_backups`, `hetzner_list_snapshots`, `hetzner_delete_snapshot`, `hetzner_change_server_type`)
- **Coolify:** `list_coolify_applications`, `get_coolify_application`, `get_coolify_application_logs`, `list_coolify_servers`, `list_coolify_databases`, `get_coolify_env_vars`, `deploy_coolify_application`, `start_coolify_application`, `stop_coolify_application`, `restart_coolify_application`, `deploy_coolify_by_tag`, `set_coolify_env_var`

Die vollstaendige, aktuelle Liste samt Berechtigungsstufe steht im Web-UI unter **Settings → MCP**.

## Tech Stack

- **Backend:** C# / .NET 10.0 / ASP.NET Core
- **Frontend:** Blazor Server + MudBlazor
- **Docker API:** Docker.DotNet
- **Datenbank:** SQLite (Entity Framework Core)
- **Auth:** Google OAuth 2.0 oder generisches OIDC + Rollen & Email-Whitelist
- **Echtzeit:** SignalR
- **MCP:** ModelContextProtocol.AspNetCore

## Deployment

### Als Docker Container (empfohlen)

```bash
git clone https://github.com/LupusMalusDeviant/ServerWatch.git
cd ServerWatch
cp .env.example .env
# .env editieren und Werte eintragen
docker compose up -d
```

Alle Konfigurationswerte werden ueber `.env` gesetzt (siehe [.env.example](.env.example)).
Die Datei `.env` ist gitignored — Secrets landen nie im Repo.

### Nginx Reverse Proxy

```nginx
location /serverwatch/ {
    proxy_pass http://127.0.0.1:5100/serverwatch/;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection $connection_upgrade;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Prefix /serverwatch;
    proxy_read_timeout 300s;
    proxy_buffer_size 128k;
    proxy_buffers 4 256k;
}
```

## Konfiguration

### Google OAuth
1. Google Cloud Console > APIs & Services > Credentials
2. OAuth 2.0 Client-ID erstellen
3. Autorisierte Weiterleitungs-URI: `https://your-server.com/serverwatch/signin-google`
   (Google akzeptiert keine privaten IPs / intranet-TLDs — fuer LAN-only Deployments
   `AUTH_DISABLED=true` in `.env` setzen.)
4. `GOOGLE_CLIENT_ID` und `GOOGLE_CLIENT_SECRET` in `.env` eintragen
5. `GOOGLE_ADMIN_EMAIL` mit der initialen Admin-Adresse fuellen

### Email-Whitelist
Wird ueber Settings > Authentication im Web-UI verwaltet.
Aenderungen werden ohne Neustart uebernommen.

### MCP API-Key
Wird beim ersten Start automatisch generiert und in den Container-Logs ausgegeben.
Gespeichert in `/app/data/api-keys.json` (persistiert im Docker Volume).

### Mattermost
Settings > Mattermost Notifications > Webhook-URL eintragen.

## Projektstruktur

```
ServerWatch/
├── src/ServerWatch/
│   ├── Components/Pages/     # Blazor UI-Seiten
│   ├── Configuration/        # Settings-Klassen
│   ├── Hubs/                 # SignalR Hubs
│   ├── Mcp/                  # MCP-Server Tools
│   ├── Models/               # Datenmodelle
│   ├── Services/
│   │   ├── Auth/             # Whitelist
│   │   ├── Deployment/       # Container-Deployment
│   │   ├── Docker/           # Docker API
│   │   ├── HealthMonitor/    # Health-Ueberwachung
│   │   ├── ImageUpdate/      # Image-Update-Check
│   │   ├── Metrics/          # Metriken-Sammlung
│   │   ├── Notifications/    # Mattermost
│   │   ├── Onboarding/       # Mesh+mTLS Server-Onboarding (Tailscale, step-ca, ghostunnel)
│   │   ├── Persistence/      # SQLite + JSON
│   │   ├── Server/           # Host-Befehle (SSH-frei via mTLS), Firewall, Nginx, systemd, SSL
│   │   ├── ServerConfig/     # Multi-Server-Verwaltung
│   │   └── Terminal/         # Web-Terminal
│   └── wwwroot/              # Statische Assets
├── deploy/telemetry/         # Mesh/mTLS Deploy-Vorlagen (node_exporter, VictoriaMetrics, Tailscale-ACL)
├── docs/ARCHITECTURE.md      # Zero-SSH-Key Architektur
├── Dockerfile
├── docker-compose.yml
└── README.md
```

## Lizenz

Apache License 2.0 — siehe [LICENSE](LICENSE).

Copyright 2026 ServerWatch Contributors

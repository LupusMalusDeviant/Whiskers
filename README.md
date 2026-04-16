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
- Multi-Server-Unterstuetzung (Local + SSH)
- Firewall-Management (ufw) ueber Web-UI
- Nginx-Sites verwalten mit Config-Editor
- systemd-Services starten/stoppen/ueberwachen
- SSL-Zertifikate (Let's Encrypt) Status und Renewal
- Integriertes Web-Terminal (Host + Container)

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

**Verfuegbare Tools:**
- `list_containers`, `get_container_details`, `get_container_logs`
- `start_container`, `stop_container`, `restart_container`, `update_container`
- `deploy_app` — standardisiertes Deployment mit Vorlagen
- `list_servers`, `get_server_info`, `execute_command`
- `list_firewall_rules`, `add_firewall_rule`, `remove_firewall_rule`
- `list_nginx_sites`, `get_nginx_config`, `update_nginx_config`
- `list_systemd_services`, `manage_systemd_service`
- `list_ssl_certificates`, `renew_ssl_certificate`
- `get_health_summary`, `get_container_metrics`, `get_server_metrics`
- `get_server_logs`, `get_container_log_stream`

## Tech Stack

- **Backend:** C# / .NET 10.0 / ASP.NET Core
- **Frontend:** Blazor Server + MudBlazor
- **Docker API:** Docker.DotNet
- **Datenbank:** SQLite (Entity Framework Core)
- **Auth:** Google OAuth 2.0 + Email-Whitelist
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
│   │   ├── Persistence/      # SQLite + JSON
│   │   ├── Server/           # Host-Befehle, Firewall, Nginx, systemd, SSL
│   │   ├── ServerConfig/     # Multi-Server-Verwaltung
│   │   └── Terminal/         # Web-Terminal
│   └── wwwroot/              # Statische Assets
├── Dockerfile
├── docker-compose.yml
└── README.md
```

## Lizenz

Apache License 2.0 — siehe [LICENSE](LICENSE).

Copyright 2026 ServerWatch Contributors

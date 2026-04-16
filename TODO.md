# ServerWatch — TODO

## Erledigt

- [x] MCP Security (API-Keys mit Berechtigungsstufen, Web-UI Management)
- [x] UI komplett auf Deutsch
- [x] Git-Repo + README
- [x] MCP-Server mit 27 Tools (Container, Server, Monitoring, Deployment)
- [x] Image-Update-Alerting (Registry-Check, Update-Badge, One-Click-Update)
- [x] Dashboard Live-Stats (5s Refresh-Timer)
- [x] Terminal: Container (docker exec mit PTY via script)
- [x] Terminal: Host (nsenter -t 1 fuer Host-Zugriff, SSH fuer Remote)
- [x] Firewall-Management (ufw) ueber Web-UI
- [x] Nginx Manager (Config editieren, testen, reload)
- [x] systemd Manager (Services verwalten, Journal anzeigen)
- [x] SSL-Zertifikate (Status, Ablauf-Warnung, Renewal)
- [x] Mattermost-Benachrichtigungen
- [x] Google OAuth + Email-Whitelist
- [x] Multi-Server-Unterstuetzung (Local + SSH)
- [x] SQLite Metriken-Sammlung (CPU, RAM, Disk, Netzwerk — 30s Intervall, 7 Tage)

## Offen

### Chart-Visualisierung fuer historische Metriken
- MetricsQueryService existiert mit GetContainerCpuHistoryAsync(), GetServerMemoryHistoryAsync() etc.
- Daten werden in SQLite gesammelt (MetricsCollectorService)
- **Fehlend:** Zeitreihen-Charts im Dashboard und ContainerDetail
- Optionen: MudBlazor Charts, ApexCharts.Blazor, oder Plotly.Blazor

### Weitere Verbesserungen
- Disk-Usage-Alerts bei >90%
- Log-Pattern-Alerting (ERROR in Container-Logs -> Mattermost)
- SSH-Key-Management fuer Remote-Server verbessern
- Server-Gruppen/Tags
- Docker Compose Templates fuer haeufige Deployments

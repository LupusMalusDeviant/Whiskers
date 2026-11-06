# Configuration

Whiskers is configured entirely through environment variables. Every setting is optional. The full, commented list
is [../.env.example](../.env.example).

The app reads container environment variables; it does not read a `.env` file itself.

- **Release `docker-compose.yml`:** it maps `.env` names to container variables, for example `OIDC_ENABLED` →
  `Oidc__Enabled`, `PATH_BASE` → `PathBase`, `AUTH_DISABLED` → `Auth__Disabled`, `GOOGLE_CLIENT_ID` →
  `GoogleAuth__ClientId`. **Not mapped**, so putting them in `.env` does nothing: `WHISKERS_SETUP_TOKEN`,
  `WHISKERS_DATA_DIR`, `WHISKERS_DB_PROVIDER` / `WHISKERS_DB_CONNECTION` / `WHISKERS_DB_CONNECTION_FILE`,
  `Auth__LocalLogin__Enabled`, `Auth__DisabledAllowedNetworks__*`, `Auth__DisabledAllowedHosts__*` and
  `ForwardedHeaders__*`. Set those as `environment:` entries; for PostgreSQL use the `deploy/docker-compose.postgres.yml`
  overlay (it is in the repository, not a release asset, and fits only the release compose file, service
  `serverwatch`). `HOST_BIND` / `HOST_PORT` are read by compose for the port mapping.
- **Installer:** its compose file sets `TZ`, `VAULT_KEY_FILE` (installers after 0.15.0) and, with `--data`,
  `WHISKERS_DATA_HOST_DIR`. Add container variables (`Oidc__Enabled`, `PathBase`, …) as `environment:` entries in
  `./whiskers/docker-compose.yml`.
- **`docker run`:** nothing is set; pass container variables with `-e`.

| Group | Keys | Notes |
|---|---|---|
| Authentication | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_ADMIN_EMAIL`, `AUTH_DISABLED`, `WHISKERS_SETUP_TOKEN` | `AUTH_DISABLED=true` makes every request an Admin; it is meant for trusted LAN-only trials. Requests are then only served from loopback/private networks (`Auth__DisabledAllowedNetworks__0=…` to narrow), with an IP or `localhost` as host name (`Auth__DisabledAllowedHosts__0=whiskers.lan` to add one) and no cross-site `Origin`. `WHISKERS_SETUP_TOKEN` presets the first-run setup token. |
| Admin bootstrap | `WHISKERS_ADMIN_EMAIL` | Seeded as an **Admin** on first run (provider-neutral; `GOOGLE_ADMIN_EMAIL` does the same for Google) so a fresh instance is never admin-less. Also flips the email whitelist **fail-closed** once any role exists. Existing installs (with a `roles.json`) are untouched. |
| Local login | `WHISKERS_ADMIN_PASSWORD_FILE`, `Auth__LocalLogin__Enabled` | Username/password login without an IdP (ASP.NET Identity, its own tables in the same DB). On by default; set `Auth__LocalLogin__Enabled=false` for federated-only. Password policy: min 12 chars. |
| Secrets vault | `VAULT_KEY`, `VAULT_KEY_FILE` | Passphrase that encrypts stored secrets at rest (AES-256-GCM); or mount it as a file and point `_FILE` at it (wins; a missing or empty file stops the start). Empty = vault disabled. Keep it stable — losing it makes stored secrets undecryptable; a key that does not match the vault is detected at start and locks the vault without changing it |
| OIDC (optional) | `OIDC_ENABLED`, `OIDC_AUTHORITY`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET`, ... | Generic OpenID Connect (Authentik, Keycloak, Authelia, Zitadel, ...) for 2FA/passkeys from your IdP |
| Notifications | `MATTERMOST_WEBHOOK_URL`, `MATTERMOST_ENABLED` | The other channels are configured in the UI; their credentials are stored in the vault (needs `VAULT_KEY`) |
| Routing | `PATH_BASE` | Path prefix when reverse-proxied under a subpath |
| Forwarded headers | `ForwardedHeaders__TrustedNetworks__0`, … | Proxies whose `X-Forwarded-*` headers are believed; loopback only by default ([installation.md](installation.md#behind-an-nginx-reverse-proxy)) |
| AI chat | `AICHAT_ENABLED`, `AICHAT_API_KEY`, `AICHAT_API_URL`, `AICHAT_MODEL`, `AICHAT_PROVIDER` | Read-only advisor chat |
| Agent | `AGENT_ENABLED`, `AGENT_PROVIDER`, `AGENT_MODEL`, `AGENT_API_KEY`, ... | Acting agent ([agent.md](agent.md)) |
| Mesh VPN | `VPN_PROVIDER`, `TAILSCALE_AUTHKEY_FILE` (preferred) / `TAILSCALE_AUTHKEY`, `NETBIRD_SETUP_KEY_FILE` / `NETBIRD_SETUP_KEY`, `VPN_FAIL_STARTUP_ON_ERROR`, ... | Pluggable VPN bring-up (`none`/`tailscale`/`netbird`); empty = legacy in-container Tailscale. The in-app bring-up runs only after an admin takes "VPN bring-up" over in Settings → Automation owners |
| Image search | `HARBOR_URL`, `HARBOR_USERNAME`, `HARBOR_PASSWORD` | Add a self-hosted Harbor marketplace (Docker Hub + GHCR are on by default) |
| Host binding | `HOST_BIND`, `HOST_PORT` | The container always listens on `8080` internally |
| Data directory | `WHISKERS_DATA_DIR` | Root for SQLite, JSON stores, keys & certificates (default `/app/data`); repoint at any volume / host path |
| Database | `WHISKERS_DB_PROVIDER`, `WHISKERS_DB_CONNECTION`, `WHISKERS_DB_CONNECTION_FILE` | `sqlite` (default, zero-config) or `postgres`. For Postgres set a connection string, or mount it as a file and point `_FILE` at it. Ready-to-run overlay: `deploy/docker-compose.postgres.yml`. |

## Runtime details

- **Email whitelist**: managed in the UI under *Settings → Authentication*; changes apply without a restart.
- **MCP API keys**: created in the UI under *Settings → MCP* ([mcp.md](mcp.md#api-keys-and-scopes)).
- **Data persistence**: the metrics database (SQLite by default, or PostgreSQL), JSON stores and certificates live
  under the data directory (`WHISKERS_DATA_DIR`, default `/app/data`); never in the image.
- **Health probes**: anonymous `/healthz` (liveness) and `/readyz` (readiness); the image ships a Docker
  `HEALTHCHECK` and Kubernetes can use them as probes. `/health` without the `z` is the in-app status page.

## Tech stack

| Layer | Technology |
|---|---|
| Backend | C# / .NET 10 / ASP.NET Core |
| Frontend | Blazor Server + [MudBlazor](https://mudblazor.com/) |
| Docker API | [Docker.DotNet](https://github.com/dotnet/Docker.DotNet) |
| Database | SQLite (zero-config default) or PostgreSQL (Entity Framework Core) + JSON file stores |
| Auth | Local accounts (ASP.NET Identity), Google OAuth 2.0 or generic OIDC + roles & email whitelist |
| Real-time | SignalR |
| MCP | [ModelContextProtocol.AspNetCore](https://github.com/modelcontextprotocol) |
| Metrics | VictoriaMetrics (Prometheus-compatible) |

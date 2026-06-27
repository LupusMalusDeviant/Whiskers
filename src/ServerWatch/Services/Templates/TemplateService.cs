using ServerWatch.Models;

namespace ServerWatch.Services.Templates;

public class TemplateService : ITemplateService
{
    public List<AppTemplate> GetTemplates() => Templates;
    public AppTemplate? GetTemplate(string id) => Templates.FirstOrDefault(t => t.Id == id);
    public List<string> GetCategories() => Templates.Select(t => t.Category).Distinct().OrderBy(c => c).ToList();

    private static readonly List<AppTemplate> Templates = new()
    {
        // === Datenbanken ===
        T("postgres", "PostgreSQL", "Relationale SQL-Datenbank", "Datenbank", "Storage",
            "services:\n  postgres:\n    image: postgres:17-alpine\n    container_name: {PROJECT}-postgres\n    environment:\n      - POSTGRES_USER={POSTGRES_USER}\n      - POSTGRES_PASSWORD={POSTGRES_PASSWORD}\n      - POSTGRES_DB={POSTGRES_DB}\n    volumes:\n      - pg_data:/var/lib/postgresql/data\n    ports:\n      - \"{PORT}:5432\"\n    restart: unless-stopped\n    healthcheck:\n      test: [\"CMD-SHELL\", \"pg_isready -U {POSTGRES_USER}\"]\n      interval: 10s\n      timeout: 5s\n      retries: 5\nvolumes:\n  pg_data:",
            new() { ["POSTGRES_USER"] = "app", ["POSTGRES_DB"] = "app_db", ["PORT"] = "5432" }, new() { "POSTGRES_PASSWORD" }),

        T("mysql", "MySQL", "MySQL 8 Datenbank", "Datenbank", "Storage",
            "services:\n  mysql:\n    image: mysql:8.0\n    container_name: {PROJECT}-mysql\n    environment:\n      - MYSQL_ROOT_PASSWORD={MYSQL_ROOT_PASSWORD}\n      - MYSQL_DATABASE={MYSQL_DATABASE}\n    volumes:\n      - mysql_data:/var/lib/mysql\n    ports:\n      - \"{PORT}:3306\"\n    restart: unless-stopped\nvolumes:\n  mysql_data:",
            new() { ["MYSQL_DATABASE"] = "app_db", ["PORT"] = "3306" }, new() { "MYSQL_ROOT_PASSWORD" }),

        T("mariadb", "MariaDB", "MySQL-kompatibler Fork", "Datenbank", "Storage",
            "services:\n  mariadb:\n    image: mariadb:11\n    container_name: {PROJECT}-mariadb\n    environment:\n      - MARIADB_ROOT_PASSWORD={MARIADB_ROOT_PASSWORD}\n      - MARIADB_DATABASE={MARIADB_DATABASE}\n    volumes:\n      - mariadb_data:/var/lib/mysql\n    ports:\n      - \"{PORT}:3306\"\n    restart: unless-stopped\nvolumes:\n  mariadb_data:",
            new() { ["MARIADB_DATABASE"] = "app_db", ["PORT"] = "3307" }, new() { "MARIADB_ROOT_PASSWORD" }),

        T("mongo", "MongoDB", "NoSQL-Dokumentendatenbank", "Datenbank", "Storage",
            "services:\n  mongo:\n    image: mongo:7\n    container_name: {PROJECT}-mongo\n    environment:\n      - MONGO_INITDB_ROOT_USERNAME={MONGO_USER}\n      - MONGO_INITDB_ROOT_PASSWORD={MONGO_PASSWORD}\n    volumes:\n      - mongo_data:/data/db\n    ports:\n      - \"{PORT}:27017\"\n    restart: unless-stopped\nvolumes:\n  mongo_data:",
            new() { ["MONGO_USER"] = "admin", ["PORT"] = "27017" }, new() { "MONGO_PASSWORD" }),

        T("redis", "Redis", "In-Memory Cache & Message Broker", "Datenbank", "Speed",
            "services:\n  redis:\n    image: redis:7-alpine\n    container_name: {PROJECT}-redis\n    ports:\n      - \"{PORT}:6379\"\n    volumes:\n      - redis_data:/data\n    restart: unless-stopped\nvolumes:\n  redis_data:",
            new() { ["PORT"] = "6379" }, new()),

        T("neo4j", "Neo4j", "Graph-Datenbank", "Datenbank", "Share",
            "services:\n  neo4j:\n    image: neo4j:5-community\n    container_name: {PROJECT}-neo4j\n    environment:\n      - NEO4J_AUTH=neo4j/{NEO4J_PASSWORD}\n    volumes:\n      - neo4j_data:/data\n    ports:\n      - \"{HTTP_PORT}:7474\"\n      - \"{BOLT_PORT}:7687\"\n    restart: unless-stopped\nvolumes:\n  neo4j_data:",
            new() { ["HTTP_PORT"] = "7474", ["BOLT_PORT"] = "7687" }, new() { "NEO4J_PASSWORD" }),

        T("meilisearch", "Meilisearch", "Blitzschnelle Suchmaschine", "Datenbank", "Search",
            "services:\n  meilisearch:\n    image: getmeili/meilisearch:latest\n    container_name: {PROJECT}-meilisearch\n    environment:\n      - MEILI_MASTER_KEY={MEILI_KEY}\n    volumes:\n      - meili_data:/meili_data\n    ports:\n      - \"{PORT}:7700\"\n    restart: unless-stopped\nvolumes:\n  meili_data:",
            new() { ["PORT"] = "7700" }, new() { "MEILI_KEY" }),

        T("influxdb", "InfluxDB", "Zeitreihendatenbank", "Datenbank", "Timeline",
            "services:\n  influxdb:\n    image: influxdb:2\n    container_name: {PROJECT}-influxdb\n    environment:\n      - DOCKER_INFLUXDB_INIT_MODE=setup\n      - DOCKER_INFLUXDB_INIT_USERNAME={INFLUX_USER}\n      - DOCKER_INFLUXDB_INIT_PASSWORD={INFLUX_PASSWORD}\n      - DOCKER_INFLUXDB_INIT_ORG={INFLUX_ORG}\n      - DOCKER_INFLUXDB_INIT_BUCKET={INFLUX_BUCKET}\n    volumes:\n      - influx_data:/var/lib/influxdb2\n    ports:\n      - \"{PORT}:8086\"\n    restart: unless-stopped\nvolumes:\n  influx_data:",
            new() { ["INFLUX_USER"] = "admin", ["INFLUX_ORG"] = "myorg", ["INFLUX_BUCKET"] = "default", ["PORT"] = "8086" }, new() { "INFLUX_PASSWORD" }),

        // === Web & Proxy ===
        T("nginx", "Nginx", "Webserver & Reverse Proxy", "Web", "Language",
            "services:\n  nginx:\n    image: nginx:alpine\n    container_name: {PROJECT}-nginx\n    ports:\n      - \"{PORT}:80\"\n    volumes:\n      - ./html:/usr/share/nginx/html:ro\n    restart: unless-stopped",
            new() { ["PORT"] = "8080" }, new()),

        T("traefik", "Traefik", "Cloud-nativer Reverse Proxy mit Auto-SSL", "Web", "Router",
            "services:\n  traefik:\n    image: traefik:v3.0\n    container_name: {PROJECT}-traefik\n    command:\n      - --api.dashboard=true\n      - --entrypoints.web.address=:80\n      - --entrypoints.websecure.address=:443\n      - --certificatesresolvers.letsencrypt.acme.email={ACME_EMAIL}\n      - --certificatesresolvers.letsencrypt.acme.storage=/letsencrypt/acme.json\n      - --certificatesresolvers.letsencrypt.acme.httpchallenge.entrypoint=web\n      - --providers.docker=true\n    ports:\n      - \"80:80\"\n      - \"443:443\"\n      - \"{DASHBOARD_PORT}:8080\"\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock:ro\n      - traefik_certs:/letsencrypt\n    restart: unless-stopped\nvolumes:\n  traefik_certs:",
            new() { ["DASHBOARD_PORT"] = "8081" }, new() { "ACME_EMAIL" }),

        T("caddy", "Caddy", "Webserver mit automatischem HTTPS", "Web", "Lock",
            "services:\n  caddy:\n    image: caddy:2-alpine\n    container_name: {PROJECT}-caddy\n    ports:\n      - \"80:80\"\n      - \"443:443\"\n    volumes:\n      - ./Caddyfile:/etc/caddy/Caddyfile\n      - caddy_data:/data\n    restart: unless-stopped\nvolumes:\n  caddy_data:",
            new(), new()),

        T("wordpress", "WordPress", "CMS mit MySQL", "Web", "Web",
            "services:\n  wordpress:\n    image: wordpress:latest\n    container_name: {PROJECT}-wordpress\n    environment:\n      - WORDPRESS_DB_HOST=db\n      - WORDPRESS_DB_USER=wordpress\n      - WORDPRESS_DB_PASSWORD={DB_PASSWORD}\n      - WORDPRESS_DB_NAME=wordpress\n    volumes:\n      - wp_data:/var/www/html\n    ports:\n      - \"{PORT}:80\"\n    depends_on:\n      - db\n    restart: unless-stopped\n  db:\n    image: mysql:8.0\n    container_name: {PROJECT}-mysql\n    environment:\n      - MYSQL_ROOT_PASSWORD={DB_PASSWORD}\n      - MYSQL_DATABASE=wordpress\n      - MYSQL_USER=wordpress\n      - MYSQL_PASSWORD={DB_PASSWORD}\n    volumes:\n      - db_data:/var/lib/mysql\n    restart: unless-stopped\nvolumes:\n  wp_data:\n  db_data:",
            new() { ["PORT"] = "8080" }, new() { "DB_PASSWORD" }),

        T("ghost", "Ghost", "Modernes Publishing & Blog CMS", "Web", "Edit",
            "services:\n  ghost:\n    image: ghost:5-alpine\n    container_name: {PROJECT}-ghost\n    environment:\n      - url=http://localhost:{PORT}\n    volumes:\n      - ghost_data:/var/lib/ghost/content\n    ports:\n      - \"{PORT}:2368\"\n    restart: unless-stopped\nvolumes:\n  ghost_data:",
            new() { ["PORT"] = "2368" }, new()),

        // === DevOps ===
        T("gitea", "Gitea", "Leichtgewichtiger Git-Server", "DevOps", "Code",
            "services:\n  gitea:\n    image: gitea/gitea:latest\n    container_name: {PROJECT}-gitea\n    environment:\n      - USER_UID=1000\n      - USER_GID=1000\n    volumes:\n      - gitea_data:/data\n    ports:\n      - \"{HTTP_PORT}:3000\"\n      - \"{SSH_PORT}:22\"\n    restart: unless-stopped\nvolumes:\n  gitea_data:",
            new() { ["HTTP_PORT"] = "3000", ["SSH_PORT"] = "2222" }, new()),

        T("drone", "Drone CI", "Container-native CI/CD", "DevOps", "BuildCircle",
            "services:\n  drone:\n    image: drone/drone:latest\n    container_name: {PROJECT}-drone\n    environment:\n      - DRONE_GITEA_SERVER={GITEA_URL}\n      - DRONE_GITEA_CLIENT_ID={GITEA_CLIENT_ID}\n      - DRONE_GITEA_CLIENT_SECRET={GITEA_CLIENT_SECRET}\n      - DRONE_RPC_SECRET={RPC_SECRET}\n      - DRONE_SERVER_HOST={DRONE_HOST}\n      - DRONE_SERVER_PROTO=https\n    volumes:\n      - drone_data:/data\n    ports:\n      - \"{PORT}:80\"\n    restart: unless-stopped\nvolumes:\n  drone_data:",
            new() { ["PORT"] = "8082" }, new() { "GITEA_URL", "GITEA_CLIENT_ID", "GITEA_CLIENT_SECRET", "RPC_SECRET", "DRONE_HOST" }),

        T("registry", "Docker Registry", "Private Docker Image Registry", "DevOps", "CloudUpload",
            "services:\n  registry:\n    image: registry:2\n    container_name: {PROJECT}-registry\n    volumes:\n      - registry_data:/var/lib/registry\n    ports:\n      - \"{PORT}:5000\"\n    restart: unless-stopped\nvolumes:\n  registry_data:",
            new() { ["PORT"] = "5000" }, new()),

        // === Monitoring ===
        T("uptime-kuma", "Uptime Kuma", "Self-hosted Uptime Monitoring", "Monitoring", "MonitorHeart",
            "services:\n  uptime-kuma:\n    image: louislam/uptime-kuma:1\n    container_name: {PROJECT}-uptime-kuma\n    volumes:\n      - kuma_data:/app/data\n    ports:\n      - \"{PORT}:3001\"\n    restart: unless-stopped\nvolumes:\n  kuma_data:",
            new() { ["PORT"] = "3001" }, new()),

        T("grafana", "Grafana", "Dashboards & Visualisierung", "Monitoring", "BarChart",
            "services:\n  grafana:\n    image: grafana/grafana:latest\n    container_name: {PROJECT}-grafana\n    environment:\n      - GF_SECURITY_ADMIN_USER={ADMIN_USER}\n      - GF_SECURITY_ADMIN_PASSWORD={ADMIN_PASSWORD}\n    volumes:\n      - grafana_data:/var/lib/grafana\n    ports:\n      - \"{PORT}:3000\"\n    restart: unless-stopped\nvolumes:\n  grafana_data:",
            new() { ["ADMIN_USER"] = "admin", ["PORT"] = "3000" }, new() { "ADMIN_PASSWORD" }),

        T("prometheus", "Prometheus", "Metriken-Sammler & Alerting", "Monitoring", "QueryStats",
            "services:\n  prometheus:\n    image: prom/prometheus:latest\n    container_name: {PROJECT}-prometheus\n    volumes:\n      - prom_data:/prometheus\n      - ./prometheus.yml:/etc/prometheus/prometheus.yml\n    ports:\n      - \"{PORT}:9090\"\n    restart: unless-stopped\nvolumes:\n  prom_data:",
            new() { ["PORT"] = "9090" }, new()),

        T("portainer", "Portainer", "Docker Management UI", "Monitoring", "Dashboard",
            "services:\n  portainer:\n    image: portainer/portainer-ce:latest\n    container_name: {PROJECT}-portainer\n    volumes:\n      - /var/run/docker.sock:/var/run/docker.sock\n      - portainer_data:/data\n    ports:\n      - \"{PORT}:9443\"\n    restart: unless-stopped\nvolumes:\n  portainer_data:",
            new() { ["PORT"] = "9443" }, new()),

        // === Automatisierung ===
        T("n8n", "n8n", "Workflow-Automatisierung", "Automatisierung", "AccountTree",
            "services:\n  n8n:\n    image: n8nio/n8n:latest\n    container_name: {PROJECT}-n8n\n    environment:\n      - N8N_BASIC_AUTH_ACTIVE=true\n      - N8N_BASIC_AUTH_USER={N8N_USER}\n      - N8N_BASIC_AUTH_PASSWORD={N8N_PASSWORD}\n    volumes:\n      - n8n_data:/home/node/.n8n\n    ports:\n      - \"{PORT}:5678\"\n    restart: unless-stopped\nvolumes:\n  n8n_data:",
            new() { ["N8N_USER"] = "admin", ["PORT"] = "5678" }, new() { "N8N_PASSWORD" }),

        T("huginn", "Huginn", "Agenten-basierte Automatisierung (wie IFTTT)", "Automatisierung", "SmartToy",
            "services:\n  huginn:\n    image: ghcr.io/huginn/huginn:latest\n    container_name: {PROJECT}-huginn\n    environment:\n      - HUGINN_SEED_DATABASE=true\n      - DATABASE_ADAPTER=postgresql\n      - DATABASE_HOST=db\n      - DATABASE_NAME=huginn\n      - DATABASE_USERNAME=huginn\n      - DATABASE_PASSWORD={DB_PASSWORD}\n    ports:\n      - \"{PORT}:3000\"\n    depends_on:\n      - db\n    restart: unless-stopped\n  db:\n    image: postgres:16-alpine\n    container_name: {PROJECT}-huginn-db\n    environment:\n      - POSTGRES_USER=huginn\n      - POSTGRES_PASSWORD={DB_PASSWORD}\n      - POSTGRES_DB=huginn\n    volumes:\n      - huginn_db:/var/lib/postgresql/data\n    restart: unless-stopped\nvolumes:\n  huginn_db:",
            new() { ["PORT"] = "3002" }, new() { "DB_PASSWORD" }),

        // === Storage ===
        T("minio", "MinIO", "S3-kompatibler Object Storage", "Storage", "CloudQueue",
            "services:\n  minio:\n    image: minio/minio:latest\n    container_name: {PROJECT}-minio\n    command: server /data --console-address \":9001\"\n    environment:\n      - MINIO_ROOT_USER={MINIO_USER}\n      - MINIO_ROOT_PASSWORD={MINIO_PASSWORD}\n    volumes:\n      - minio_data:/data\n    ports:\n      - \"{API_PORT}:9000\"\n      - \"{CONSOLE_PORT}:9001\"\n    restart: unless-stopped\nvolumes:\n  minio_data:",
            new() { ["MINIO_USER"] = "admin", ["API_PORT"] = "9000", ["CONSOLE_PORT"] = "9001" }, new() { "MINIO_PASSWORD" }),

        T("nextcloud", "Nextcloud", "Self-hosted Cloud-Speicher & Office", "Storage", "Cloud",
            "services:\n  nextcloud:\n    image: nextcloud:latest\n    container_name: {PROJECT}-nextcloud\n    environment:\n      - POSTGRES_HOST=db\n      - POSTGRES_DB=nextcloud\n      - POSTGRES_USER=nextcloud\n      - POSTGRES_PASSWORD={DB_PASSWORD}\n      - NEXTCLOUD_ADMIN_USER={ADMIN_USER}\n      - NEXTCLOUD_ADMIN_PASSWORD={ADMIN_PASSWORD}\n    volumes:\n      - nc_data:/var/www/html\n    ports:\n      - \"{PORT}:80\"\n    depends_on:\n      - db\n    restart: unless-stopped\n  db:\n    image: postgres:16-alpine\n    container_name: {PROJECT}-nc-db\n    environment:\n      - POSTGRES_USER=nextcloud\n      - POSTGRES_PASSWORD={DB_PASSWORD}\n      - POSTGRES_DB=nextcloud\n    volumes:\n      - nc_db:/var/lib/postgresql/data\n    restart: unless-stopped\nvolumes:\n  nc_data:\n  nc_db:",
            new() { ["ADMIN_USER"] = "admin", ["PORT"] = "8085" }, new() { "DB_PASSWORD", "ADMIN_PASSWORD" }),

        T("filebrowser", "File Browser", "Web-basierter Dateimanager", "Storage", "Folder",
            "services:\n  filebrowser:\n    image: filebrowser/filebrowser:latest\n    container_name: {PROJECT}-filebrowser\n    volumes:\n      - {DATA_PATH}:/srv\n      - fb_db:/database\n    ports:\n      - \"{PORT}:80\"\n    restart: unless-stopped\nvolumes:\n  fb_db:",
            new() { ["DATA_PATH"] = "/opt/data", ["PORT"] = "8084" }, new()),

        // === Kommunikation ===
        T("matrix-dendrite", "Matrix (Dendrite)", "Dezentraler Chat-Server", "Kommunikation", "Chat",
            "services:\n  dendrite:\n    image: matrixdotorg/dendrite-monolith:latest\n    container_name: {PROJECT}-dendrite\n    volumes:\n      - dendrite_data:/etc/dendrite\n      - dendrite_media:/var/dendrite/media\n    ports:\n      - \"{PORT}:8008\"\n    restart: unless-stopped\nvolumes:\n  dendrite_data:\n  dendrite_media:",
            new() { ["PORT"] = "8008" }, new()),

        T("rocketchat", "Rocket.Chat", "Team-Chat Plattform", "Kommunikation", "Forum",
            "services:\n  rocketchat:\n    image: rocket.chat:latest\n    container_name: {PROJECT}-rocketchat\n    environment:\n      - MONGO_URL=mongodb://mongo:27017/rocketchat\n      - ROOT_URL=http://localhost:{PORT}\n    ports:\n      - \"{PORT}:3000\"\n    depends_on:\n      - mongo\n    restart: unless-stopped\n  mongo:\n    image: mongo:6\n    container_name: {PROJECT}-rc-mongo\n    volumes:\n      - rc_mongo:/data/db\n    restart: unless-stopped\nvolumes:\n  rc_mongo:",
            new() { ["PORT"] = "3004" }, new()),

        // === Security ===
        T("vaultwarden", "Vaultwarden", "Bitwarden-kompatibler Passwort-Manager", "Security", "VpnKey",
            "services:\n  vaultwarden:\n    image: vaultwarden/server:latest\n    container_name: {PROJECT}-vaultwarden\n    environment:\n      - ADMIN_TOKEN={ADMIN_TOKEN}\n    volumes:\n      - vw_data:/data\n    ports:\n      - \"{PORT}:80\"\n    restart: unless-stopped\nvolumes:\n  vw_data:",
            new() { ["PORT"] = "8083" }, new() { "ADMIN_TOKEN" }),

        T("authelia", "Authelia", "SSO & 2FA Authentication Proxy", "Security", "Shield",
            "services:\n  authelia:\n    image: authelia/authelia:latest\n    container_name: {PROJECT}-authelia\n    volumes:\n      - ./configuration.yml:/config/configuration.yml\n      - authelia_data:/data\n    ports:\n      - \"{PORT}:9091\"\n    restart: unless-stopped\nvolumes:\n  authelia_data:",
            new() { ["PORT"] = "9091" }, new()),

        // === Analytics ===
        T("plausible", "Plausible Analytics", "Datenschutzfreundliche Web-Analytics", "Analytics", "Analytics",
            "services:\n  plausible:\n    image: ghcr.io/plausible/community-edition:latest\n    container_name: {PROJECT}-plausible\n    environment:\n      - BASE_URL=http://localhost:{PORT}\n      - SECRET_KEY_BASE={SECRET_KEY}\n    ports:\n      - \"{PORT}:8000\"\n    depends_on:\n      - db\n      - clickhouse\n    restart: unless-stopped\n  db:\n    image: postgres:16-alpine\n    container_name: {PROJECT}-plausible-db\n    environment:\n      - POSTGRES_PASSWORD=postgres\n    volumes:\n      - pl_db:/var/lib/postgresql/data\n    restart: unless-stopped\n  clickhouse:\n    image: clickhouse/clickhouse-server:latest\n    container_name: {PROJECT}-clickhouse\n    volumes:\n      - pl_ch:/var/lib/clickhouse\n    restart: unless-stopped\nvolumes:\n  pl_db:\n  pl_ch:",
            new() { ["PORT"] = "8000" }, new() { "SECRET_KEY" }),

        T("matomo", "Matomo", "Self-hosted Web-Analytics (Google Analytics Alternative)", "Analytics", "TrendingUp",
            "services:\n  matomo:\n    image: matomo:latest\n    container_name: {PROJECT}-matomo\n    environment:\n      - MATOMO_DATABASE_HOST=db\n      - MATOMO_DATABASE_DBNAME=matomo\n      - MATOMO_DATABASE_USERNAME=matomo\n      - MATOMO_DATABASE_PASSWORD={DB_PASSWORD}\n    volumes:\n      - matomo_data:/var/www/html\n    ports:\n      - \"{PORT}:80\"\n    depends_on:\n      - db\n    restart: unless-stopped\n  db:\n    image: mariadb:11\n    container_name: {PROJECT}-matomo-db\n    environment:\n      - MARIADB_ROOT_PASSWORD={DB_PASSWORD}\n      - MARIADB_DATABASE=matomo\n      - MARIADB_USER=matomo\n      - MARIADB_PASSWORD={DB_PASSWORD}\n    volumes:\n      - matomo_db:/var/lib/mysql\n    restart: unless-stopped\nvolumes:\n  matomo_data:\n  matomo_db:",
            new() { ["PORT"] = "8086" }, new() { "DB_PASSWORD" }),
    };

    // Helper to reduce boilerplate
    private static AppTemplate T(string id, string name, string desc, string category, string icon,
        string compose, Dictionary<string, string> defaults, List<string> required) => new()
    {
        Id = id, Name = name, Description = desc, Category = category, Icon = icon,
        ComposeContent = compose, EnvDefaults = defaults, RequiredEnvVars = required
    };
}

#!/bin/bash
set -e

# ─────────────────────────────────────────────────────────
# ServerWatch Deploy Script
# Deploys ServerWatch to a remote server via SSH + Docker
# ─────────────────────────────────────────────────────────

# --- Configuration (edit these or pass as env vars) ---
SERVER="${SERVER:-}"
REMOTE_PATH="${REMOTE_PATH:-/opt/ServerWatch}"
ADMIN_EMAIL="${ADMIN_EMAIL:-}"
GOOGLE_CLIENT_ID="${GOOGLE_CLIENT_ID:-}"
GOOGLE_CLIENT_SECRET="${GOOGLE_CLIENT_SECRET:-}"
PATH_BASE="${PATH_BASE:-}"
PORT="${PORT:-5100}"

# --- Colors ---
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

print_step() { echo -e "${GREEN}==> $1${NC}"; }
print_warn() { echo -e "${YELLOW}[!] $1${NC}"; }
print_err()  { echo -e "${RED}[ERROR] $1${NC}"; }

# --- Interactive prompts for missing values ---
if [ -z "$SERVER" ]; then
    read -rp "SSH-Ziel (user@host oder Host aus ~/.ssh/config): " SERVER
fi

if [ -z "$ADMIN_EMAIL" ]; then
    echo ""
    print_warn "Eine Google-E-Mail-Adresse wird als Admin-Whitelist BENÖTIGT."
    print_warn "Nur diese Adresse kann sich nach dem Deploy einloggen."
    read -rp "Admin Google-E-Mail: " ADMIN_EMAIL
fi

if [ -z "$ADMIN_EMAIL" ]; then
    print_err "Admin-E-Mail ist Pflicht! Abbruch."
    exit 1
fi

if [ -z "$GOOGLE_CLIENT_ID" ]; then
    echo ""
    print_warn "Google OAuth Credentials werden für die Anmeldung benötigt."
    print_warn "Erstelle diese unter https://console.cloud.google.com/apis/credentials"
    read -rp "Google Client ID: " GOOGLE_CLIENT_ID
fi

if [ -z "$GOOGLE_CLIENT_SECRET" ]; then
    read -rp "Google Client Secret: " GOOGLE_CLIENT_SECRET
fi

if [ -z "$GOOGLE_CLIENT_ID" ] || [ -z "$GOOGLE_CLIENT_SECRET" ]; then
    print_err "Google OAuth Credentials sind Pflicht! Abbruch."
    exit 1
fi

if [ -z "$PATH_BASE" ]; then
    read -rp "URL-Pfad (leer für Root, z.B. '/serverwatch'): " PATH_BASE
fi

# --- Summary ---
echo ""
echo "─────────────────────────────────────────"
echo "  Server:       $SERVER"
echo "  Remote-Pfad:  $REMOTE_PATH"
echo "  Admin-Email:  $ADMIN_EMAIL"
echo "  Port:         $PORT (localhost)"
echo "  PathBase:     ${PATH_BASE:-(root)}"
echo "─────────────────────────────────────────"
echo ""
read -rp "Deploy starten? [Y/n] " CONFIRM
if [[ "$CONFIRM" =~ ^[nN] ]]; then
    echo "Abbruch."
    exit 0
fi

# --- Generate docker-compose.yml ---
print_step "Generiere docker-compose.yml ..."

COMPOSE_FILE="docker-compose.deploy.yml"
cat > "$COMPOSE_FILE" <<YAML
services:
  serverwatch:
    build: .
    container_name: serverwatch
    pid: host
    privileged: true
    ports:
      - "127.0.0.1:${PORT}:8080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - serverwatch-data:/app/data
      - /proc:/host_proc:ro
    environment:
      - GoogleAuth__ClientId=${GOOGLE_CLIENT_ID}
      - GoogleAuth__ClientSecret=${GOOGLE_CLIENT_SECRET}
      - GoogleAuth__AllowedEmails__0=${ADMIN_EMAIL}
      - Mattermost__WebhookUrl=
      - Mattermost__Enabled=false
      - PathBase=${PATH_BASE}
    restart: unless-stopped

volumes:
  serverwatch-data:
YAML

# --- Deploy ---
print_step "Erstelle Verzeichnis auf $SERVER ..."
ssh "$SERVER" "mkdir -p $REMOTE_PATH"

print_step "Kopiere Dateien ..."
scp -r ./src ./Dockerfile ./.dockerignore "$COMPOSE_FILE" "$SERVER:$REMOTE_PATH/"
ssh "$SERVER" "mv $REMOTE_PATH/$COMPOSE_FILE $REMOTE_PATH/docker-compose.yml"

print_step "Baue und starte Container ..."
ssh "$SERVER" "cd $REMOTE_PATH && docker compose up -d --build"

print_step "Warte auf Container ..."
sleep 3
ssh "$SERVER" "docker ps --filter name=serverwatch --format 'table {{.Names}}\t{{.Status}}'"

# --- Cleanup ---
rm -f "$COMPOSE_FILE"

# --- Done ---
echo ""
print_step "Deploy erfolgreich!"
echo ""
echo "  Nächste Schritte:"
echo "  1. Nginx/Reverse-Proxy einrichten (Port $PORT -> ServerWatch)"
if [ -n "$PATH_BASE" ]; then
    echo "     location ${PATH_BASE}/ {"
    echo "         proxy_pass http://127.0.0.1:${PORT}${PATH_BASE}/;"
    echo "         proxy_http_version 1.1;"
    echo "         proxy_set_header Upgrade \$http_upgrade;"
    echo "         proxy_set_header Connection \$connection_upgrade;"
    echo "         proxy_set_header Host \$host;"
    echo "         proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;"
    echo "         proxy_set_header X-Forwarded-Proto \$scheme;"
    echo "     }"
fi
echo ""
echo "  2. In den Settings einen MCP API-Key erstellen"
echo "  3. Key in .mcp.json eintragen:"
echo "     {"
echo "       \"mcpServers\": {"
echo "         \"serverwatch\": {"
echo "           \"type\": \"http\","
echo "           \"url\": \"https://<dein-host>${PATH_BASE}/mcp\","
echo "           \"headers\": {"
echo "             \"Authorization\": \"Bearer <dein-api-key>\""
echo "           }"
echo "         }"
echo "       }"
echo "     }"
echo ""

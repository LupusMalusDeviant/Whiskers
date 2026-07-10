#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Whiskers installer (outOfTheBox W2).
#
#   curl -fsSL https://get.whiskers.dev | bash          # once get.whiskers.dev points here
#   # or:
#   bash install.sh [--port 5100] [--bind 127.0.0.1] [--data <path>] [--yes]
#
# Pulls the published image and brings Whiskers up with a small compose file in ./whiskers/.
# Re-running it UPDATES in place (pull + up -d). It never installs Docker and never exposes
# anything publicly without an explicit opt-in.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

IMAGE_DEFAULT="ghcr.io/lupusmalusdeviant/whiskers:latest"
IMAGE="${WHISKERS_IMAGE:-$IMAGE_DEFAULT}"
PORT="${WHISKERS_PORT:-5100}"
BIND="${WHISKERS_BIND:-127.0.0.1}"
DATA="${WHISKERS_DATA:-}"            # empty → named docker volume `whiskers-data`
DIR="${WHISKERS_DIR:-./whiskers}"
ASSUME_YES="${WHISKERS_YES:-false}"
INSTALL_DOCKER=false

# ── pretty logging ───────────────────────────────────────────────────────────
if [ -t 1 ]; then B=$'\033[1m'; G=$'\033[32m'; Y=$'\033[33m'; R=$'\033[31m'; N=$'\033[0m'; else B=; G=; Y=; R=; N=; fi
info()  { printf '%s\n' "${B}==>${N} $*"; }
ok()    { printf '%s\n' "${G}✓${N} $*"; }
warn()  { printf '%s\n' "${Y}!${N} $*" >&2; }
die()   { printf '%s\n' "${R}✗ $*${N}" >&2; exit 1; }

usage() {
  cat <<EOF
Whiskers installer

Usage: install.sh [options]
  --port <n>        Host port (default 5100)
  --bind <addr>     Host bind address (default 127.0.0.1 — localhost only; put a
                    reverse proxy in front before exposing, or use --bind 0.0.0.0)
  --data <path>     Host directory for /app/data (default: named volume whiskers-data)
  --dir <path>      Where to write the compose project (default ./whiskers)
  --image <ref>     Image to run (default $IMAGE_DEFAULT)
  --install-docker  Install Docker via get.docker.com if missing (explicit opt-in)
  --yes             Non-interactive; accept defaults, no prompts
  --help            This help

Env equivalents: WHISKERS_PORT, WHISKERS_BIND, WHISKERS_DATA, WHISKERS_DIR, WHISKERS_IMAGE, WHISKERS_YES=true
EOF
}

# ── args ─────────────────────────────────────────────────────────────────────
while [ $# -gt 0 ]; do
  case "$1" in
    --port) PORT="${2:?}"; shift 2;;
    --bind) BIND="${2:?}"; shift 2;;
    --data) DATA="${2:?}"; shift 2;;
    --dir)  DIR="${2:?}"; shift 2;;
    --image) IMAGE="${2:?}"; shift 2;;
    --install-docker) INSTALL_DOCKER=true; shift;;
    --yes|-y) ASSUME_YES=true; shift;;
    --help|-h) usage; exit 0;;
    *) die "Unknown option: $1 (see --help)";;
  esac
done

ask() { # ask "prompt" "default" -> echoes answer (or default in --yes mode)
  local prompt="$1" default="$2" ans
  if [ "$ASSUME_YES" = true ] || [ ! -t 0 ]; then printf '%s' "$default"; return; fi
  read -r -p "$prompt [$default]: " ans </dev/tty || true
  printf '%s' "${ans:-$default}"
}

# ── preflight: architecture ──────────────────────────────────────────────────
case "$(uname -m)" in
  x86_64|amd64)  ARCH=amd64;;
  aarch64|arm64) ARCH=arm64;;
  *) die "Unsupported CPU architecture: $(uname -m). Whiskers ships linux/amd64 and linux/arm64.";;
esac
ok "Architecture: $ARCH"

# ── preflight: docker + compose v2 ───────────────────────────────────────────
if ! command -v docker >/dev/null 2>&1; then
  if [ "$INSTALL_DOCKER" = true ]; then
    info "Installing Docker via get.docker.com (you opted in with --install-docker)…"
    curl -fsSL https://get.docker.com | sh
  else
    die "Docker is not installed. Install Docker Engine (https://docs.docker.com/engine/install/) and re-run, or pass --install-docker to let this script install it."
  fi
fi
if ! docker info >/dev/null 2>&1; then
  die "Docker is installed but not reachable. Start the Docker daemon (and ensure your user can talk to it) and re-run."
fi
if docker compose version >/dev/null 2>&1; then
  DC=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
  DC=(docker-compose)
else
  die "Docker Compose v2 not found. Install the Compose plugin (https://docs.docker.com/compose/install/) and re-run."
fi
ok "Docker $(docker version -f '{{.Server.Version}}' 2>/dev/null || echo present) + Compose available"

# ── interactive settings (skipped with --yes / no TTY) ───────────────────────
if [ "$ASSUME_YES" != true ] && [ -t 0 ]; then
  PORT="$(ask 'Host port' "$PORT")"
  BIND="$(ask 'Bind address (127.0.0.1 = localhost only)' "$BIND")"
  DATA="$(ask 'Data directory (empty = docker named volume)' "$DATA")"
fi
[ "$PORT" -gt 0 ] 2>/dev/null || die "Invalid port: $PORT"

# ── port availability (best-effort warning, not fatal) ───────────────────────
port_in_use() {
  if command -v ss >/dev/null 2>&1; then ss -ltn 2>/dev/null | grep -qE "[:.]${PORT}[[:space:]]"
  elif command -v netstat >/dev/null 2>&1; then netstat -ltn 2>/dev/null | grep -qE "[:.]${PORT}[[:space:]]"
  else return 1; fi
}
if port_in_use; then
  warn "Port $PORT already appears to be in use — if bring-up fails, re-run with --port <free-port>."
fi

# ── data volume mapping ──────────────────────────────────────────────────────
if [ -n "$DATA" ]; then
  mkdir -p "$DATA"
  DATA_MOUNT="$(cd "$DATA" && pwd):/app/data"
else
  DATA_MOUNT="whiskers-data:/app/data"
fi

# ── write the compose project ────────────────────────────────────────────────
mkdir -p "$DIR"
COMPOSE="$DIR/docker-compose.yml"
if [ -f "$COMPOSE" ]; then
  info "Existing install found at $COMPOSE — updating (pull + up), keeping your compose file."
else
  info "Writing $COMPOSE"
  cat > "$COMPOSE" <<EOF
# Generated by the Whiskers installer. Edit freely; re-running install.sh keeps this file.
# Full-management profile (manages this host). For the locked-down profile see docker-compose.hardened.yml.
services:
  whiskers:
    image: ${IMAGE}
    container_name: whiskers
    pid: host
    privileged: true
    ports:
      - "${BIND}:${PORT}:8080"
    devices:
      - /dev/net/tun:/dev/net/tun
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - ${DATA_MOUNT}
      - /proc:/host_proc:ro
    environment:
      - TZ=\${TZ:-Europe/Berlin}
    restart: unless-stopped
EOF
  # Declare the named volume only when we are using one (not a host bind path).
  if [ -z "$DATA" ]; then
    printf '\nvolumes:\n  whiskers-data:\n' >> "$COMPOSE"
  fi
fi

# ── pull + up (idempotent — same commands for install and update) ────────────
info "Pulling image ${IMAGE}…"
( cd "$DIR" && "${DC[@]}" pull )
info "Starting Whiskers…"
( cd "$DIR" && "${DC[@]}" up -d )

# ── wait for readiness (C11 /healthz) ────────────────────────────────────────
URL="http://${BIND}:${PORT}"
[ "$BIND" = "0.0.0.0" ] && URL="http://127.0.0.1:${PORT}"
info "Waiting for Whiskers to become healthy…"
ready=false
for _ in $(seq 1 60); do
  if curl -fsS -m 3 "${URL}/healthz" >/dev/null 2>&1; then ready=true; break; fi
  sleep 2
done

echo
if [ "$ready" = true ]; then
  ok "Whiskers is up: ${B}${URL}${N}"
else
  warn "Whiskers started but /healthz did not answer within ~2 min. Check logs: ( cd $DIR && ${DC[*]} logs -f )"
fi

cat <<EOF

${B}Next steps${N}
  • Open ${URL}
  • First sign-in needs an auth provider until the setup wizard ships (outOfTheBox W1):
      – Google or generic OIDC  → set the provider vars (see .env.example) and re-run, or
      – localhost-only trial    → add \`- Auth__Disabled=true\` under environment: in $COMPOSE, then
                                    ( cd $DIR && ${DC[*]} up -d )   ${Y}(never do this on a public bind)${N}
  • Update later:  bash install.sh            (re-run = pull + up)
  • Logs:          ( cd $DIR && ${DC[*]} logs -f )
  • Hardened/K8s:  see the README and docker-compose.hardened.yml
EOF

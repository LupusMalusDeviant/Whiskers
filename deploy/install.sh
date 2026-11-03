#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Whiskers installer (outOfTheBox W2, R1-46).
#
#   # The installer of a release (recommended: it installs exactly that release; check it against checksums.txt):
#   curl -fsSLO https://github.com/LupusMalusDeviant/Whiskers/releases/download/vX.Y.Z/install.sh
#   bash install.sh [--version X.Y.Z] [--port 5100] [--bind 127.0.0.1] [--data <path>] [--verify auto|require|off] [--yes]
#
# Installs one released, version-pinned image (never `latest`) and brings Whiskers up with a small compose file in
# ./whiskers/. On a new install it generates the vault key into ./whiskers/secrets/vault.key (0600, never printed) and
# hands it to Whiskers as a secret file. It verifies the image's cosign signature when cosign is installed. Re-running
# it with a newer --version (or the installer of a newer release) UPGRADES in place: only the image line changes, the
# previous compose file is kept as a copy, and a downgrade is refused. It never installs Docker and never exposes
# anything publicly without an explicit opt-in.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# The release this installer installs by default. The release pipeline refuses a tag whose version differs from it
# (release.yml, prepare), so the install.sh of release X.Y.Z installs X.Y.Z.
WHISKERS_RELEASE="0.15.0"
REPOSITORY="ghcr.io/lupusmalusdeviant/whiskers"
# The identity the release workflow signs with (keyless cosign, see README "Supply chain").
SIGNER_IDENTITY_RE='^https://github.com/LupusMalusDeviant/Whiskers/.github/workflows/release.yml@refs/tags/v'
SIGNER_ISSUER="https://token.actions.githubusercontent.com"
BACKUP_GUIDE="https://github.com/LupusMalusDeviant/Whiskers/blob/main/docs/operations/backup-and-recovery.md"

VERSION="${WHISKERS_VERSION:-$WHISKERS_RELEASE}"
IMAGE="${WHISKERS_IMAGE:-}"          # empty → ${REPOSITORY}:${VERSION}
PORT="${WHISKERS_PORT:-5100}"
BIND="${WHISKERS_BIND:-127.0.0.1}"
DATA="${WHISKERS_DATA:-}"            # empty → named docker volume `whiskers-data`
DIR="${WHISKERS_DIR:-./whiskers}"
VERIFY="${WHISKERS_VERIFY:-auto}"    # auto | require | off
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
Whiskers installer (installs release ${WHISKERS_RELEASE} unless told otherwise)

Usage: install.sh [options]
  --version <X.Y.Z> Release to install or upgrade to (default ${WHISKERS_RELEASE}). There is no "latest":
                    every install is pinned to one release.
  --image <ref>     Run another image instead (a fork, a mirror). It must name a tag other than "latest" or a
                    digest (@sha256:…). Its signature can only be verified for ${REPOSITORY}.
  --verify <mode>   Image signature check with cosign: auto (default: verify when cosign is installed, refuse a
                    bad signature), require (refuse to install without a verified signature), off.
  --port <n>        Host port (default 5100)
  --bind <addr>     Host bind address (default 127.0.0.1 — localhost only; put a
                    reverse proxy in front before exposing, or use --bind 0.0.0.0)
  --data <path>     Host directory for /app/data (default: named volume whiskers-data)
  --dir <path>      Where to write the compose project (default ./whiskers)
  --install-docker  Install Docker via get.docker.com if missing (explicit opt-in)
  --yes             Non-interactive; accept defaults, no prompts
  --help            This help

Env equivalents: WHISKERS_VERSION, WHISKERS_IMAGE, WHISKERS_VERIFY, WHISKERS_PORT, WHISKERS_BIND, WHISKERS_DATA,
WHISKERS_DIR, WHISKERS_YES=true
EOF
}

# ── args ─────────────────────────────────────────────────────────────────────
while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="${2:?}"; shift 2;;
    --image) IMAGE="${2:?}"; shift 2;;
    --verify) VERIFY="${2:?}"; shift 2;;
    --port) PORT="${2:?}"; shift 2;;
    --bind) BIND="${2:?}"; shift 2;;
    --data) DATA="${2:?}"; shift 2;;
    --dir)  DIR="${2:?}"; shift 2;;
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

# ── which image (pinned, never latest) ───────────────────────────────────────
SEMVER_RE='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'

# True when the reference names a digest, or a tag other than "latest".
image_pinned() {
  local ref="$1" name tag
  case "$ref" in *@sha256:*) return 0;; esac
  name="${ref##*/}"
  case "$name" in *:*) tag="${name##*:}";; *) return 1;; esac
  [ -n "$tag" ] && [ "$tag" != "latest" ]
}

# The release number of an image of $REPOSITORY (empty for anything else).
release_of() {
  local ref="$1" tag
  case "$ref" in "${REPOSITORY}:"*) ;; *) return 0;; esac
  tag="${ref#"${REPOSITORY}:"}"
  tag="${tag%%@*}"
  if [[ "$tag" =~ $SEMVER_RE ]]; then printf '%s' "$tag"; fi
}

# version_lt A B: true when release A is older than release B.
version_lt() {
  local IFS=. i
  local -a a b
  read -r -a a <<<"$1"
  read -r -a b <<<"$2"
  for i in 0 1 2; do
    if (( 10#${a[i]} < 10#${b[i]} )); then return 0; fi
    if (( 10#${a[i]} > 10#${b[i]} )); then return 1; fi
  done
  return 1
}

if [ -n "$IMAGE" ]; then
  image_pinned "$IMAGE" || die "--image '$IMAGE' is not pinned: name a release tag (not \"latest\") or a digest (@sha256:…)."
else
  [[ "$VERSION" =~ $SEMVER_RE ]] || die "--version must be a release number X.Y.Z (got '$VERSION'). \"latest\" is not offered: pick a release from https://github.com/LupusMalusDeviant/Whiskers/releases"
  IMAGE="${REPOSITORY}:${VERSION}"
fi
case "$VERIFY" in auto|require|off) ;; *) die "--verify must be auto, require or off (got '$VERIFY')";; esac
ok "Image: $IMAGE"

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

# ── signature (cosign, keyless; before anything is pulled or written) ────────
verify_signature() {
  local official=false out
  case "$IMAGE" in "${REPOSITORY}:"*|"${REPOSITORY}@"*) official=true;; esac
  if [ "$VERIFY" = off ]; then
    warn "Image signature NOT verified (--verify off)."
    return 0
  fi
  if [ "$official" != true ]; then
    [ "$VERIFY" = require ] && die "--verify require: only images of ${REPOSITORY} carry the release signature; $IMAGE cannot be verified."
    warn "Image signature NOT verified: $IMAGE is not an image of ${REPOSITORY}."
    return 0
  fi
  if ! command -v cosign >/dev/null 2>&1; then
    [ "$VERIFY" = require ] && die "--verify require: cosign is not installed (https://docs.sigstore.dev/cosign/system_config/installation/)."
    warn "cosign is not installed — the image signature is NOT verified. Install cosign and re-run, or check it by hand:"
    warn "  cosign verify $IMAGE --certificate-identity-regexp '$SIGNER_IDENTITY_RE' --certificate-oidc-issuer $SIGNER_ISSUER"
    return 0
  fi
  info "Verifying the signature of $IMAGE (cosign)…"
  if out="$(cosign verify "$IMAGE" --certificate-identity-regexp "$SIGNER_IDENTITY_RE" --certificate-oidc-issuer "$SIGNER_ISSUER" 2>&1)"; then
    ok "Signature verified: built and signed by the Whiskers release workflow."
  else
    printf '%s\n' "$out" >&2
    die "Signature verification FAILED for $IMAGE — nothing was installed or changed."
  fi
}
verify_signature

# ── interactive settings (skipped with --yes / no TTY) ───────────────────────
COMPOSE="$DIR/docker-compose.yml"
if [ ! -f "$COMPOSE" ] && [ "$ASSUME_YES" != true ] && [ -t 0 ]; then
  PORT="$(ask 'Host port' "$PORT")"
  BIND="$(ask 'Bind address (127.0.0.1 = localhost only)' "$BIND")"
  DATA="$(ask 'Data directory (empty = docker named volume)' "$DATA")"
fi
[ "$PORT" -gt 0 ] 2>/dev/null || die "Invalid port: $PORT"

# ── vault key: generated once, 0600, never printed ───────────────────────────
SECRETS_DIR="$DIR/secrets"
KEY_FILE="$SECRETS_DIR/vault.key"
new_vault_key() {
  if command -v openssl >/dev/null 2>&1; then openssl rand -hex 32
  else head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n'; fi
}
ensure_vault_key() {
  local key
  ( umask 077; mkdir -p "$SECRETS_DIR" )
  chmod 700 "$SECRETS_DIR"
  if [ -e "$KEY_FILE" ]; then
    [ -s "$KEY_FILE" ] || die "$KEY_FILE exists but is empty. Restore it from your backup (the vault and encrypted backups need exactly that key); remove it only if this is a fresh install with no data."
    info "Keeping the existing vault key $KEY_FILE"
  else
    key="$(new_vault_key)"
    [ "${#key}" -ge 64 ] || die "Could not generate a vault key (no openssl, no /dev/urandom)."
    # printf is a shell builtin: the key is never on a command line.
    ( umask 077; printf '%s' "$key" > "$KEY_FILE.tmp" )
    key=
    mv "$KEY_FILE.tmp" "$KEY_FILE"
    ok "Generated the vault key $KEY_FILE (mode 0600; not shown)"
  fi
  chmod 600 "$KEY_FILE"
}

# ── port availability (best-effort warning, not fatal) ───────────────────────
port_in_use() {
  if command -v ss >/dev/null 2>&1; then ss -ltn 2>/dev/null | grep -qE "[:.]${PORT}[[:space:]]"
  elif command -v netstat >/dev/null 2>&1; then netstat -ltn 2>/dev/null | grep -qE "[:.]${PORT}[[:space:]]"
  else return 1; fi
}

# ── write the compose project, or upgrade the existing one ───────────────────
UPGRADED_FROM=""
mkdir -p "$DIR"
if [ -f "$COMPOSE" ]; then
  info "Existing install found at $COMPOSE"
  grep -q '^# Generated by the Whiskers installer' "$COMPOSE" \
    || die "$COMPOSE was not written by this installer; change its image line by hand (pull + up), the installer leaves it alone."
  [ "$(grep -c '^    image: ' "$COMPOSE" || true)" = 1 ] \
    || die "$COMPOSE does not have exactly one 'image:' line of the Whiskers service; change it by hand."
  CURRENT="$(sed -n 's/^    image: *//p' "$COMPOSE")"
  if [ "$CURRENT" = "$IMAGE" ]; then
    info "Already on $IMAGE — pull + up only."
  else
    FROM_VER="$(release_of "$CURRENT")"
    TO_VER="$(release_of "$IMAGE")"
    if [ -n "$FROM_VER" ] && [ -n "$TO_VER" ] && version_lt "$TO_VER" "$FROM_VER"; then
      die "Refusing to downgrade from $FROM_VER to $TO_VER: a newer release may have migrated the database. A downgrade is the older release on the pre-upgrade backup — see $BACKUP_GUIDE"
    fi
    BACKUP_COMPOSE="$COMPOSE.bak-$(date +%Y%m%d%H%M%S)"
    cp -p "$COMPOSE" "$BACKUP_COMPOSE"
    sed "s#^    image: .*#    image: ${IMAGE}#" "$COMPOSE" > "$COMPOSE.tmp"
    mv "$COMPOSE.tmp" "$COMPOSE"
    UPGRADED_FROM="$CURRENT"
    ok "Image changed: $CURRENT → $IMAGE (previous compose file: $BACKUP_COMPOSE)"
  fi
  if ! grep -q 'VAULT_KEY' "$COMPOSE"; then
    warn "This install has no vault key: the secret vault and encrypted backups are off. To add one, see 'Vault key' in"
    warn "  https://github.com/LupusMalusDeviant/Whiskers/blob/main/deploy/README.md — the installer does not change it for you."
  fi
else
  if port_in_use; then
    warn "Port $PORT already appears to be in use — if bring-up fails, re-run with --port <free-port>."
  fi
  # ── data volume mapping ────────────────────────────────────────────────────
  if [ -n "$DATA" ]; then
    mkdir -p "$DATA"
    DATA_MOUNT="$(cd "$DATA" && pwd):/app/data"
    # Whiskers protects its own data (INV-59): tell it which host directory that is.
    DATA_ENV="      - WHISKERS_DATA_HOST_DIR=$(cd "$DATA" && pwd)"
  else
    DATA_MOUNT="whiskers-data:/app/data"
    DATA_ENV=""
  fi
  ensure_vault_key
  info "Writing $COMPOSE"
  cat > "$COMPOSE" <<EOF
# Generated by the Whiskers installer. Edit freely; re-running install.sh keeps this file and changes only the
# image line (upgrade). Full-management profile (manages this host). For the locked-down profile see
# docker-compose.hardened.yml.
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
    # The vault key (secrets/vault.key, 0600) as a secret file, never as an environment value.
    secrets:
      - whiskers_vault_key
    environment:
      - TZ=\${TZ:-Europe/Berlin}
      - VAULT_KEY_FILE=/run/secrets/whiskers_vault_key
${DATA_ENV}
    restart: unless-stopped
    # Whiskers lets running work finish for up to 30 s when it stops (45 s in all); give it that time.
    stop_grace_period: 60s

secrets:
  whiskers_vault_key:
    file: ./secrets/vault.key
EOF
  # Declare the named volume only when we are using one (not a host bind path).
  if [ -z "$DATA" ]; then
    printf '\nvolumes:\n  whiskers-data:\n' >> "$COMPOSE"
  fi
fi

# ── pull + up (idempotent — same commands for install and upgrade) ───────────
info "Pulling image ${IMAGE}…"
( cd "$DIR" && "${DC[@]}" pull )
info "Starting Whiskers…"
( cd "$DIR" && "${DC[@]}" up -d )

# ── wait for readiness (C11 /healthz) ────────────────────────────────────────
URL="http://${BIND}:${PORT}"
[ "$BIND" = "0.0.0.0" ] && URL="http://127.0.0.1:${PORT}"
info "Waiting for Whiskers to become healthy…"
ready=false
# An upgrade first takes a pre-upgrade backup and migrates the database (R1-34): up to ~5 min.
for _ in $(seq 1 150); do
  if curl -fsS -m 3 "${URL}/healthz" >/dev/null 2>&1; then ready=true; break; fi
  sleep 2
done

echo
if [ "$ready" = true ]; then
  ok "Whiskers is up: ${B}${URL}${N}"
else
  warn "Whiskers started but /healthz did not answer within ~5 min. Check logs: ( cd $DIR && ${DC[*]} logs -f )"
fi

# The first-run wizard asks for a one-time setup token so that reaching the port is not enough to become Admin.
SETUP_TOKEN="$(docker exec whiskers cat /app/data/setup-token.txt 2>/dev/null || true)"

cat <<EOF

${B}Next steps${N}
  • Open ${URL} — the setup wizard creates the administrator account.
EOF
if [ -n "$SETUP_TOKEN" ]; then
  cat <<EOF
  • Setup token (asked for on the first page; it stops working once an admin exists):
        ${B}${SETUP_TOKEN}${N}
EOF
else
  cat <<EOF
  • Setup token: docker exec whiskers cat /app/data/setup-token.txt
    (no file means setup is already complete)
EOF
fi
if [ -n "$UPGRADED_FROM" ]; then
  cat <<EOF
  • Upgraded from ${UPGRADED_FROM}. Whiskers took a pre-upgrade backup before migrating; to go back, follow
    ${BACKUP_GUIDE}
EOF
fi
if [ -s "$KEY_FILE" ]; then
  KEY_LINE="The vault key: $(cd "$SECRETS_DIR" && pwd)/vault.key"
elif grep -q 'VAULT_KEY' "$COMPOSE"; then
  KEY_LINE="The vault key that ${COMPOSE} names (VAULT_KEY / VAULT_KEY_FILE)."
else
  KEY_LINE="The vault key: this install has none yet (see the warning above)."
fi
cat <<EOF

${B}Back up — two things, kept apart, off this host${N}
  1. ${KEY_LINE}
     Copy it into a password manager or onto offline media. Without it the secret vault and every encrypted
     backup are unreadable; anyone who has it and a backup can read them. It is not shown here.
  2. The data: Settings → Backup & Restore (encrypted self-backup; schedule the SelfBackup task) and copy the
     archives off the host. Guide: ${BACKUP_GUIDE}

${B}Later${N}
  • Upgrade:  bash install.sh --version <X.Y.Z>   (or the install.sh of the newer release; downgrades are refused)
  • Logs:     ( cd $DIR && ${DC[*]} logs -f )
  • Hardened/K8s:  see the README, docker-compose.hardened.yml and the Helm chart
EOF

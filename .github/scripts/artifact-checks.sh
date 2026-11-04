#!/usr/bin/env bash
# R1-47 (TA-50, F-13): checks that run against the deliverable artifact — the built image — not against the source tree.
#
#   artifact-checks.sh hardened <image>
#       Starts <image> with docker-compose.hardened.yml (non-root, read-only root filesystem, no capabilities, socket proxy)
#       and checks the posture the profile promises, health and readiness, the anonymous surface, and a real local sign-in
#       with an Admin-only download, all on the read-only root filesystem.
#
#   artifact-checks.sh upgrade <image> <previous-image>
#       Upgrade and recovery with data: the previous release (N-1) creates its data directory (seeded administrator, vault
#       key, sign-in), <image> starts on it (pre-upgrade backup, migrations, the same administrator signs in), then the
#       documented way back: <image> stages the pre-upgrade backup with --restore-backup latest-pre-upgrade
#       --for-previous-release, the previous release starts on the restored data, and <image> upgrades it once more.
#
#   artifact-checks.sh previous-release [<version>]
#       Prints the image of the highest published release (GitHub releases, no drafts or pre-releases) below <version>,
#       or the highest one without a version. Needs gh with GH_TOKEN and GITHUB_REPOSITORY.
#
# Every wait is bounded (WAIT_SECONDS, default 300 — the image's own health-check start period); every check that cannot
# be made fails the run. Containers, volumes and the compose project are removed on exit; their logs are printed when a
# check failed.
set -euo pipefail

WAIT_SECONDS="${WAIT_SECONDS:-300}"
IMAGE_REPO="${IMAGE_REPO:-ghcr.io/lupusmalusdeviant/whiskers}"
ADMIN_EMAIL="admin@artifact-checks.test"
RUN_ID="whiskers-ci-$$"
WORK="$(mktemp -d)"
# Git Bash on Windows (local runs): native tools need a mixed path, not /tmp/...
if command -v cygpath >/dev/null 2>&1; then WORK="$(cygpath -m "$WORK")"; fi
CLEANUP=()
LOG_TARGETS=()
OK=0

fail() { echo "::error::$*" >&2; exit 1; }
step() { echo; echo "=== $*"; }

on_exit() {
    local rc=$?
    if [ "$OK" != 1 ]; then
        for target in "${LOG_TARGETS[@]+"${LOG_TARGETS[@]}"}"; do
            echo "----- logs: ${target}"; eval "$target" 2>&1 | tail -n 200 || true
        done
    fi
    for ((i=${#CLEANUP[@]}-1; i>=0; i--)); do eval "${CLEANUP[$i]}" >/dev/null 2>&1 || true; done
    rm -rf "$WORK"
    exit "$rc"
}
trap on_exit EXIT

require() { for tool in "$@"; do command -v "$tool" >/dev/null 2>&1 || fail "required tool '$tool' is not installed"; done; }

# A random value of hex characters (no shell or YAML metacharacters).
random_hex() { od -An -N"$1" -tx1 /dev/urandom | tr -d ' \n'; }

# A sign-in password that meets the Identity policy (upper and lower case, digit, symbol), without shell metacharacters.
test_password() { printf 'Ci-%s-Pw!' "$(random_hex 12)"; }

# Secrets volume: the administrator's password as a file the non-root user can read, written by the image itself.
make_secrets_volume() { # <volume> <image> <password>
    docker volume create "$1" >/dev/null
    CLEANUP+=("docker volume rm -f $1")
    docker run --rm --user 0 --entrypoint sh -e PW="$3" -v "$1:/secrets" "$2" \
        -c 'umask 022; printf %s "$PW" > /secrets/admin-password; chmod 0444 /secrets/admin-password' \
        || fail "could not write the test secrets with $2"
}

# Waits until <url>/healthz answers Healthy while <container> keeps running.
wait_healthy() { # <base-url> <container>
    local deadline=$((SECONDS + WAIT_SECONDS)) body
    while [ "$SECONDS" -lt "$deadline" ]; do
        if [ "$(docker inspect -f '{{.State.Running}}' "$2" 2>/dev/null)" != "true" ]; then
            fail "$2 stopped while starting (exit code $(docker inspect -f '{{.State.ExitCode}}' "$2" 2>/dev/null || echo '?'))"
        fi
        body="$(curl -fsS --max-time 5 "$1/healthz" 2>/dev/null || true)"
        if [ "$body" = "Healthy" ]; then echo "$2 healthy after ${SECONDS}s of the run"; return 0; fi
        sleep 3
    done
    fail "$2 did not become healthy within ${WAIT_SECONDS}s"
}

# Waits until /readyz answers Healthy (database, server list, maintenance, audit trail); prints the last answer otherwise.
wait_ready() { # <base-url>
    local deadline=$((SECONDS + WAIT_SECONDS)) code=""
    while [ "$SECONDS" -lt "$deadline" ]; do
        code="$(curl -sS -o "$WORK/readyz" -w '%{http_code}' --max-time 10 "$1/readyz" 2>/dev/null || true)"
        if [ "$code" = "200" ] && [ "$(cat "$WORK/readyz")" = "Healthy" ]; then echo "ok: /readyz Healthy"; return 0; fi
        sleep 3
    done
    fail "/readyz did not become Healthy within ${WAIT_SECONDS}s (last: HTTP ${code} '$(cat "$WORK/readyz" 2>/dev/null)')"
}

expect_status() { # <description> <expected-code> <curl args...>
    local what="$1" want="$2" got; shift 2
    got="$(curl -sS -o "$WORK/discard" -w '%{http_code}' --max-time 30 "$@")" || fail "$what: request failed"
    [ "$got" = "$want" ] || fail "$what: expected HTTP $want, got $got"
    echo "ok: $what (HTTP $got)"
}

# Signs in through /login-local with the antiforgery token of the sign-in page; the cookie jar keeps the session.
sign_in() { # <base-url> <cookie-jar> <email> <password>
    local html token headers code location
    html="$(curl -fsS --max-time 30 -c "$2" -b "$2" "$1/login")" || fail "the sign-in page did not load"
    token="$(printf '%s' "$html" | tr '\n' ' ' | sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p')"
    [ -n "$token" ] || fail "the sign-in page carries no antiforgery token"
    headers="$WORK/signin-headers"
    code="$(curl -sS --max-time 30 -o "$WORK/discard" -D "$headers" -w '%{http_code}' -c "$2" -b "$2" \
        --data-urlencode "email=$3" --data-urlencode "password=$4" --data-urlencode "__RequestVerificationToken=$token" \
        "$1/login-local")" || fail "the sign-in request failed"
    location="$(tr -d '\r' < "$headers" | sed -n 's/^[Ll]ocation: *//p' | tail -n1)"
    [ "$code" = "302" ] || fail "sign-in of $3 answered HTTP $code"
    case "$location" in *error*|*login*) fail "sign-in of $3 was refused (redirect to '$location')";; esac
    echo "ok: $3 signed in"
}

# Admin-only: the self-backup download of an archive that does not exist is a 404 for an Admin (a refusal otherwise).
expect_admin_session() { # <base-url> <cookie-jar>
    expect_status "Admin-only download as Admin (no such archive)" 404 -b "$2" -H 'Accept: text/html' "$1/backups/self/none/download"
}

check_anonymous_surface() { # <base-url>
    expect_status "sign-in page" 200 "$1/login"
    expect_status "anonymous page request is sent to sign in" 302 -H 'Accept: text/html' "$1/settings"
    expect_status "anonymous Admin-only download is challenged" 302 -H 'Accept: text/html' "$1/backups/self/none/download"
    local mcp
    mcp="$(curl -sS -o "$WORK/discard" -w '%{http_code}' --max-time 30 -X POST -H 'Content-Type: application/json' \
        -H 'Accept: application/json, text/event-stream' --data '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' "$1/mcp")" \
        || fail "MCP request failed"
    case "$mcp" in 401|302) echo "ok: anonymous MCP call refused (HTTP $mcp)";; *) fail "anonymous MCP call answered HTTP $mcp";; esac
    expect_status "metrics without a scrape token are disabled" 404 "$1/metrics"
}

# ----------------------------------------------------------------------------------------------------------- hardened

hardened() {
    local image="$1" project="${RUN_ID}-hardened" secrets="${RUN_ID}-hardened-secrets" base="http://127.0.0.1:8080"
    local password jar="$WORK/hardened.jar" override="$WORK/hardened.override.yml" app
    require docker curl
    docker image inspect "$image" >/dev/null 2>&1 || fail "image '$image' is not present locally"
    [ -f docker-compose.hardened.yml ] || fail "run from the repository root (docker-compose.hardened.yml not found)"
    password="$(test_password)"

    step "hardened profile: $image"
    make_secrets_volume "$secrets" "$image" "$password"
    # The shipped hardened compose file unchanged, plus: the image under test (never pulled or built) and the secrets volume.
    printf '%s\n' \
        "services:" \
        "  serverwatch:" \
        "    image: ${image}" \
        "    pull_policy: never" \
        "    volumes:" \
        "      - ci-secrets:/run/whiskers-ci:ro" \
        "volumes:" \
        "  ci-secrets:" \
        "    external: true" \
        "    name: ${secrets}" > "$override"
    local compose=(docker compose -p "$project" -f docker-compose.hardened.yml -f "$override")
    CLEANUP+=("${compose[*]} down -v --remove-orphans")
    LOG_TARGETS+=("${compose[*]} logs --no-color")
    # GOOGLE_ADMIN_EMAIL as .env.example describes it (the first Admin, seeds the whitelist). Left empty, the compose file's
    # GoogleAuth__AllowedEmails__0= seeds a whitelist that holds only an empty address and the local Admin is refused
    # (R1-47 finding, reported; the whitelist logic is outside this package).
    WHISKERS_ADMIN_EMAIL="$ADMIN_EMAIL" GOOGLE_ADMIN_EMAIL="$ADMIN_EMAIL" WHISKERS_ADMIN_PASSWORD_FILE=/run/whiskers-ci/admin-password \
        VAULT_KEY="$(random_hex 32)" "${compose[@]}" up -d --no-build

    app="$("${compose[@]}" ps -q serverwatch)"
    [ -n "$app" ] || fail "the hardened app container was not created"
    wait_healthy "$base" "$app"

    step "posture of the running container"
    [ "$(docker inspect -f '{{.Config.User}}' "$app")" = "10001:10001" ] || fail "the app does not run as 10001:10001"
    [ "$(docker exec "$app" id -u)" = "10001" ] || fail "the app process is not uid 10001"
    [ "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$app")" = "true" ] || fail "the root filesystem is writable"
    [ "$(docker inspect -f '{{.HostConfig.Privileged}}' "$app")" = "false" ] || fail "the app runs privileged"
    docker inspect -f '{{json .HostConfig.CapDrop}}' "$app" | grep -q '"ALL"' || fail "capabilities are not all dropped"
    docker inspect -f '{{json .HostConfig.SecurityOpt}}' "$app" | grep -q 'no-new-privileges:true' || fail "no-new-privileges is off"
    if docker exec "$app" sh -c 'touch /app/.write-probe' 2>/dev/null; then fail "the app could write to /app"; fi
    docker exec "$app" sh -c 'test -s /app/data/metrics.db' || fail "the database was not created in /app/data"
    echo "ok: uid 10001, read-only root, no capabilities, no-new-privileges, data on the volume"

    step "health, readiness and the HTTP surface"
    wait_ready "$base"
    check_anonymous_surface "$base"
    sign_in "$base" "$jar" "$ADMIN_EMAIL" "$password"
    expect_admin_session "$base" "$jar"
    expect_status "audit export as Admin" 200 -b "$jar" "$base/audit/export"
    [ "$(docker inspect -f '{{.RestartCount}}' "$app")" = "0" ] || fail "the app restarted during the checks"
    OK=1
    echo; echo "hardened profile checks passed"
}

# ----------------------------------------------------------------------------------------------------------- upgrade

upgrade() {
    local image="$1" previous="$2" data="${RUN_ID}-upgrade-data" secrets="${RUN_ID}-upgrade-secrets"
    local port=18080 base="http://127.0.0.1:18080" password vault jar name
    require docker curl
    docker image inspect "$image" >/dev/null 2>&1 || fail "image '$image' is not present locally"
    docker pull -q "$previous" >/dev/null 2>&1 || docker image inspect "$previous" >/dev/null 2>&1 \
        || fail "the previous release '$previous' can neither be pulled nor found locally"
    password="$(test_password)"
    vault="$(random_hex 32)"

    make_secrets_volume "$secrets" "$image" "$password"
    docker volume create "$data" >/dev/null
    CLEANUP+=("docker volume rm -f $data")

    local common=(-v "$data:/app/data" -v "$secrets:/run/whiskers-ci:ro"
        -e "WHISKERS_ADMIN_EMAIL=$ADMIN_EMAIL" -e "WHISKERS_ADMIN_PASSWORD_FILE=/run/whiskers-ci/admin-password"
        -e "VAULT_KEY=$vault" -e "VPN_PROVIDER=none" -e "Vpn__Provider=none" -e "WHISKERS_DISABLE_LOCAL_DOCKER=true")

    start() { # <name> <image>
        name="${RUN_ID}-$1"
        docker run -d --name "$name" -p "127.0.0.1:${port}:8080" "${common[@]}" "$2" >/dev/null
        CLEANUP+=("docker rm -f $name")
        LOG_TARGETS+=("docker logs $name")
        wait_healthy "$base" "$name"
    }
    stop() { docker stop -t 60 "$name" >/dev/null; }
    files() { docker run --rm --entrypoint sh -v "$data:/data" "$image" -c "$1"; }

    step "N-1: $previous creates the data"
    start previous "$previous"
    jar="$WORK/n1.jar"
    sign_in "$base" "$jar" "$ADMIN_EMAIL" "$password"
    stop

    step "upgrade: $image on the N-1 data"
    start upgraded "$image"
    # The start names the backup it took (R1-34); the archive must be on the volume, labelled as a pre-upgrade backup.
    local backup
    backup="$(docker logs "$name" 2>&1 | sed -n 's/.*Pre-upgrade backup \([0-9A-Za-z_-]*\) taken.*/\1/p' | tail -n1)"
    [ -n "$backup" ] || fail "the upgrade did not report a pre-upgrade backup"
    files "ls /data/backups/self" | grep -q "^${backup}" || fail "pre-upgrade backup ${backup} is not on the volume"
    files "grep -rl 'pre-upgrade-' /data/backups/self" | grep -q . || fail "no backup on the volume is labelled pre-upgrade"
    echo "ok: pre-upgrade backup ${backup}"
    wait_ready "$base"
    jar="$WORK/upgraded.jar"
    sign_in "$base" "$jar" "$ADMIN_EMAIL" "$password"
    expect_admin_session "$base" "$jar"
    expect_status "audit export as Admin after the upgrade" 200 -b "$jar" "$base/audit/export"
    stop

    step "recovery: back to N-1 on the pre-upgrade backup"
    timeout "$WAIT_SECONDS" docker run --rm "${common[@]}" "$image" --restore-backup latest-pre-upgrade --for-previous-release \
        || fail "staging the pre-upgrade backup failed"
    files 'test -e /data/.restore-pending' || fail "the restore was not staged"
    start recovered "$previous"
    if files 'test -e /data/.restore-pending'; then fail "the previous release did not apply the staged restore"; fi
    jar="$WORK/recovered.jar"
    sign_in "$base" "$jar" "$ADMIN_EMAIL" "$password"
    stop

    step "upgrade again after the recovery"
    start reupgraded "$image"
    # The restore really took the data back to N-1: this start is an upgrade again and takes a new pre-upgrade backup.
    docker logs "$name" 2>&1 | grep -q 'Pre-upgrade backup .* taken' \
        || fail "the start after the recovery was no upgrade: the data was not back on the previous release's schema"
    echo "ok: the recovered data was N-1 data (a new pre-upgrade backup was taken)"
    jar="$WORK/reupgraded.jar"
    sign_in "$base" "$jar" "$ADMIN_EMAIL" "$password"
    expect_admin_session "$base" "$jar"
    stop
    OK=1
    echo; echo "upgrade and recovery checks passed ($previous -> $image)"
}

# ----------------------------------------------------------------------------------------------------------- N-1

previous_release() {
    local current="${1:-}" releases chosen=""
    require gh
    [ -n "${GITHUB_REPOSITORY:-}" ] || fail "GITHUB_REPOSITORY is not set"
    releases="$(gh release list --repo "$GITHUB_REPOSITORY" --limit 1000 --exclude-drafts --exclude-pre-releases \
        --json tagName --jq '.[].tagName')" || fail "the published releases could not be listed"
    releases="$(printf '%s\n' "$releases" | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | sed 's/^v//' | sort -V || true)"
    if [ -n "$current" ]; then
        printf '%s' "$current" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || fail "'$current' is not a version X.Y.Z"
        while read -r v; do
            [ -n "$v" ] || continue
            [ "$v" = "$current" ] && continue
            if [ "$(printf '%s\n%s\n' "$v" "$current" | sort -V | head -n1)" = "$v" ]; then chosen="$v"; fi
        done <<< "$releases"
    else
        chosen="$(printf '%s\n' "$releases" | tail -n1)"
    fi
    [ -n "$chosen" ] || fail "no published release found${current:+ below $current}"
    OK=1
    printf '%s:%s\n' "$IMAGE_REPO" "$chosen"
}

case "${1:-}" in
    hardened) [ $# -eq 2 ] || fail "usage: $0 hardened <image>"; hardened "$2" ;;
    upgrade) [ $# -eq 3 ] || fail "usage: $0 upgrade <image> <previous-image>"; upgrade "$2" "$3" ;;
    previous-release) [ $# -le 2 ] || fail "usage: $0 previous-release [<version>]"; previous_release "${2:-}" ;;
    *) fail "usage: $0 hardened <image> | upgrade <image> <previous-image> | previous-release [<version>]" ;;
esac

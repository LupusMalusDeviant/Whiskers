#!/usr/bin/env bash
# R1-47 (TA-50, F-13): checks that run against the deliverable artifact — the built image — not against the source tree.
#
#   artifact-checks.sh hardened <image>
#       Starts <image> with docker-compose.hardened.yml (non-root, read-only root filesystem, no capabilities, socket proxy)
#       and checks the posture the profile promises, health and readiness, the anonymous surface, and a real local sign-in
#       with an Admin-only download, all on the read-only root filesystem.
#
#   artifact-checks.sh upgrade <image> <previous-image>
#       Upgrade and recovery with data, proven on the records themselves (artifact-data.mjs reads the stopped data
#       directory independently of the app), never on a log line or a sign-in alone:
#        1. The previous release (N-1, an older release than <image>) creates the data with bootstrap credentials once.
#           Then a second local account, its role, a metrics row and a legacy vault entry are seeded, and N-1 starts again
#           WITHOUT bootstrap credentials — nothing can be recreated from configuration from here on — reads them, signs
#           both accounts in and encrypts the vault entry itself (AES-GCM).
#        2. <image> upgrades it: a pre-upgrade backup, a genuine release change ("Upgrade from ... to ..."), exactly the
#           pending migrations added to both histories, every record unchanged and both accounts signed in.
#        3. <image> restarted on its own data is a clean no-op: no upgrade, no backup, nothing changed.
#        4. Every record is changed after the upgrade (account deleted, role, metrics value, vault value) and the app
#           sees it (the deleted account is refused).
#        5. Recovery: <image> stages the pre-upgrade backup (--restore-backup latest-pre-upgrade --for-previous-release),
#           N-1 starts on it: both migration histories are N-1's again, every record has its N-1 value, the audit rows
#           written under <image> are gone, both accounts sign in.
#        6. <image> upgrades the recovered data once more (a new pre-upgrade backup, its histories again).
#
#   artifact-checks.sh previous-release [<version>]
#       Prints the image of the highest published release (GitHub releases, no drafts or pre-releases) below <version>,
#       or the highest one without a version. Needs gh with GH_TOKEN and GITHUB_REPOSITORY.
#
#   artifact-checks.sh self-test
#       Disclosure self-test: plants random values in a probe container's logs, in streamed command output and in
#       malformed data (vault.json, a vault entry, metrics.db), runs the failure cleanup, run_quiet and the data tool on
#       them, and fails if any value, its SHA-256 fingerprint or an 8-character prefix of either reaches the output.
#
# Every wait is bounded (WAIT_SECONDS, default 300 — the image's own health-check start period); every check that cannot
# be made fails the run. Containers, volumes and the compose project are removed on exit; when a check failed, their logs
# are shown filtered (safe_log: level, category and event id only).
set -euo pipefail

WAIT_SECONDS="${WAIT_SECONDS:-300}"
IMAGE_REPO="${IMAGE_REPO:-ghcr.io/lupusmalusdeviant/whiskers}"
# The data tool's runtime (node:sqlite, node:crypto), pinned by digest.
NODE_IMAGE="${NODE_IMAGE:-node:24-alpine@sha256:8a32c8cf649a49915a66e81e7a609223c708d0adabf288ee3f4077d765542f85}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADMIN_EMAIL="admin@artifact-checks.test"
KEEPER_EMAIL="keeper@artifact-checks.test"
RUN_ID="whiskers-ci-$$"
WORK="$(mktemp -d)"
# Git Bash on Windows (local runs): native tools need a mixed path, not /tmp/...
if command -v cygpath >/dev/null 2>&1; then WORK="$(cygpath -m "$WORK")"; fi
CLEANUP=()
LOG_TARGETS=()
OK=0

fail() { echo "::error::$*" >&2; exit 1; }
step() { echo; echo "=== $*"; }

# Container output never reaches the CI log raw: a release that logs a secret, a password hash, a security stamp or a
# fingerprint would publish it there. safe_log passes only the header line of each log entry — level, category, event
# id ("fail: Whiskers.Services.Foo[12]") — and withholds every other line (messages, exceptions, entrypoint output),
# counting them. A category is shown only when it looks like a type name: one with three or more digits, or longer than
# 120 characters, is withheld too, so no value can travel in a header-shaped line.
safe_log() {
    tr -d '\r' | awk '
        /^(info|warn|fail|crit|dbug|trce): [A-Za-z_][A-Za-z0-9_.]*\[[0-9]+\]$/ {
            level = $0; sub(/:.*/, "", level)
            id = $0; sub(/^.*\[/, "", id); sub(/\]$/, "", id)
            category = $0; sub(/^[a-z]+: /, "", category); sub(/\[[0-9]+\]$/, "", category)
            if (category ~ /[0-9][^0-9]*[0-9][^0-9]*[0-9]/ || length(category) > 120) category = "(category withheld)"
            print level ": " category "[" id "]"
            next
        }
        { withheld++ }
        END { if (withheld) printf "(%d line(s) withheld: only the level, category and event id of log entries are shown)\n", withheld }'
}

# Runs a command whose output would otherwise go straight to the CI log, and shows only what safe_log lets through.
run_quiet() { # <command> [arguments...]
    local out rc=0
    out="$("$@" 2>&1)" || rc=$?
    printf '%s\n' "$out" | safe_log
    return "$rc"
}

on_exit() {
    local rc=$?
    if [ "$OK" != 1 ]; then
        for target in "${LOG_TARGETS[@]+"${LOG_TARGETS[@]}"}"; do
            echo "----- logs (filtered): ${target}"; eval "$target" 2>&1 | tail -n 400 | safe_log || true
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

# The SHA-256 fingerprint artifact-data.mjs reports for a sensitive value ("sha256:<hex>").
fingerprint() { printf 'sha256:%s' "$(printf '%s' "$1" | sha256sum | cut -d' ' -f1)"; }

# A sign-in password that meets the Identity policy (upper and lower case, digit, symbol), without shell metacharacters.
test_password() { printf 'Ci-%s-Pw!' "$(random_hex 12)"; }

# Secrets volume: the administrator's password as a file the non-root user can read, written by the image itself.
make_secrets_volume() { # <volume> <image> <password>
    docker volume create "$1" >/dev/null
    CLEANUP+=("docker volume rm -f $1")
    # shellcheck disable=SC2016 # expanded by the container shell, not here
    run_quiet docker run --rm --user 0 --entrypoint sh -e PW="$3" -v "$1:/secrets" "$2" \
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

# Posts the sign-in form through /login-local with the antiforgery token of the sign-in page; the cookie jar keeps the
# session. Prints "accepted" or "refused".
sign_in_attempt() { # <base-url> <cookie-jar> <email> <password>
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
    case "$location" in *error*|*login*) echo refused;; *) echo accepted;; esac
}

sign_in() { # <base-url> <cookie-jar> <email> <password>
    [ "$(sign_in_attempt "$@")" = accepted ] || fail "sign-in of $3 was refused"
    echo "ok: $3 signed in"
}

sign_in_refused() { # <base-url> <cookie-jar> <email> <password>
    [ "$(sign_in_attempt "$@")" = refused ] || fail "sign-in of $3 was accepted, but must be refused"
    echo "ok: $3 refused"
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
    LOG_TARGETS+=("${compose[*]} logs --no-color --no-log-prefix")
    # GOOGLE_ADMIN_EMAIL as .env.example describes it (the first Admin, seeds the whitelist). Left empty, the compose file's
    # GoogleAuth__AllowedEmails__0= seeds a whitelist that holds only an empty address and the local Admin is refused
    # (R1-47 finding, reported; the whitelist logic is outside this package).
    run_quiet env WHISKERS_ADMIN_EMAIL="$ADMIN_EMAIL" GOOGLE_ADMIN_EMAIL="$ADMIN_EMAIL" \
        WHISKERS_ADMIN_PASSWORD_FILE=/run/whiskers-ci/admin-password VAULT_KEY="$(random_hex 32)" \
        "${compose[@]}" up -d --no-build || fail "the hardened compose project did not start"

    app="$("${compose[@]}" ps -q serverwatch)"
    [ -n "$app" ] || fail "the hardened app container was not created"
    wait_healthy "$base" "$app"

    step "posture of the running container"
    [ "$(docker inspect -f '{{.Config.User}}' "$app")" = "10001:10001" ] || fail "the app does not run as 10001:10001"
    [ "$(docker exec "$app" id -u)" = "10001" ] || fail "the app process is not uid 10001"
    [ "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$app")" = "true" ] || fail "the root filesystem is writable"
    [ "$(docker inspect -f '{{.HostConfig.Privileged}}' "$app")" = "false" ] || fail "the app runs privileged"
    grep -q '"ALL"' <<< "$(docker inspect -f '{{json .HostConfig.CapDrop}}' "$app")" || fail "capabilities are not all dropped"
    grep -q 'no-new-privileges:true' <<< "$(docker inspect -f '{{json .HostConfig.SecurityOpt}}' "$app")" || fail "no-new-privileges is off"
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
    local port=18080 base="http://127.0.0.1:18080" password vault seeded changed jar name owner=""
    require docker curl sha256sum
    docker image inspect "$image" >/dev/null 2>&1 || fail "image '$image' is not present locally"
    docker pull -q "$previous" >/dev/null 2>&1 || docker image inspect "$previous" >/dev/null 2>&1 \
        || fail "the previous release '$previous' can neither be pulled nor found locally"
    docker pull -q "$NODE_IMAGE" >/dev/null 2>&1 || docker image inspect "$NODE_IMAGE" >/dev/null 2>&1 \
        || fail "the data tool's image '$NODE_IMAGE' can neither be pulled nor found locally"
    [ -f "$SCRIPT_DIR/artifact-data.mjs" ] || fail "artifact-data.mjs is missing beside $0"
    password="$(test_password)"
    vault="$(random_hex 32)"
    # What the vault entry holds before and after the change: random, so no other run's data can match.
    seeded="seeded-$(random_hex 12)"
    changed="changed-$(random_hex 12)"

    make_secrets_volume "$secrets" "$image" "$password"
    docker volume create "$data" >/dev/null
    CLEANUP+=("docker volume rm -f $data")

    # Every start: the data volume and the vault key. Only the very first start gets the bootstrap credentials — after
    # it, an account that is lost cannot be recreated from configuration, so a sign-in proves the stored account.
    local common=(-v "$data:/app/data" -e "VAULT_KEY=$vault" -e "VPN_PROVIDER=none" -e "Vpn__Provider=none"
        -e "WHISKERS_DISABLE_LOCAL_DOCKER=true")
    local bootstrap=(-v "$secrets:/run/whiskers-ci:ro" -e "WHISKERS_ADMIN_EMAIL=$ADMIN_EMAIL"
        -e "WHISKERS_ADMIN_PASSWORD_FILE=/run/whiskers-ci/admin-password")

    start() { # <name> <image> [docker run options...]
        name="${RUN_ID}-$1"
        local img="$2"; shift 2
        docker run -d --name "$name" -p "127.0.0.1:${port}:8080" "${common[@]}" "$@" "$img" >/dev/null
        CLEANUP+=("docker rm -f $name")
        LOG_TARGETS+=("docker logs $name")
        wait_healthy "$base" "$name"
    }
    stop() { docker stop -t 60 "$name" >/dev/null; }
    files() { docker run --rm --entrypoint sh -v "$data:/data" "$image" -c "$1"; }
    # artifact-data.mjs against the stopped data volume, as the volume's owner; the script goes in on stdin, so nothing
    # but the volume is mounted (the check also runs from inside a container that talks to the host's Docker).
    data_tool() { # <command> [argument]
        docker run --rm -i --network none --user "$owner" -v "$data:/data" -e "VAULT_KEY=$vault" -e "ADMIN_EMAIL=$ADMIN_EMAIL" \
            -e "MARKER_SEEDED=$seeded" -e "MARKER_CHANGED=$changed" -e "AUDIT_LINES=${AUDIT_LINES:-}" "$NODE_IMAGE" \
            sh -c 'cat > /tmp/artifact-data.mjs && exec node /tmp/artifact-data.mjs "$@"' sh "$@" < "$SCRIPT_DIR/artifact-data.mjs"
    }
    snapshot() { data_tool state > "$WORK/$1.state" || fail "the records of '$1' could not be read"; }
    field() { sed -n "s/^$2=//p" "$WORK/$1.state"; }
    # A failed comparison names the field and how it differs, never a value: the states hold fingerprints of password
    # hashes, security stamps and the vault value, and no value of them — not even a fingerprint or a prefix — is logged.
    mismatch() { # <actual> — "is absent" or "differs"
        if [ -z "$1" ] || [ "$1" = absent ]; then echo "is absent"; else echo "differs"; fi
    }
    expect_field() { # <state> <field> <value> <what>
        local actual
        actual="$(field "$1" "$2")"
        [ "$actual" = "$3" ] || fail "$4: $2 $(mismatch "$actual") in '$1'"
    }
    expect_same() { # <what> <state> <reference-state> <field>...
        local what="$1" a="$2" b="$3" f actual; shift 3
        for f in "$@"; do
            [ -n "$(field "$b" "$f")" ] || fail "$what: the reference state '$b' has no $f"
            actual="$(field "$a" "$f")"
            [ "$actual" = "$(field "$b" "$f")" ] || fail "$what: $f $(mismatch "$actual") in '$a' (compared with '$b')"
        done
        echo "ok: $what"
    }
    local records=(account.admin account.keeper role.keeper metric vault.value)
    local histories=(history.__EFMigrationsHistory history.__IdentityMigrationsHistory)

    step "N-1: $previous creates the data (bootstrap credentials, this start only)"
    start bootstrap "$previous" "${bootstrap[@]}"
    sign_in "$base" "$WORK/bootstrap.jar" "$ADMIN_EMAIL" "$password"
    stop
    owner="$(files 'stat -c %u:%g /data/metrics.db')" || fail "the data directory has no metrics.db"

    step "seed: a second account, its role, a metrics row and a legacy vault entry"
    data_tool seed-vault-legacy || fail "seeding the vault entry failed"
    data_tool seed || fail "seeding the records failed"

    step "N-1 again, without bootstrap credentials: it reads the seeded records and encrypts the vault entry itself"
    start previous "$previous"
    sign_in "$base" "$WORK/n1-admin.jar" "$ADMIN_EMAIL" "$password"
    sign_in "$base" "$WORK/n1-keeper.jar" "$KEEPER_EMAIL" "$password"
    stop
    snapshot n1
    expect_field n1 vault.format gcm "N-1 did not convert the legacy vault entry"
    expect_field n1 vault.value "$(fingerprint "$seeded")" "the vault entry N-1 encrypted"
    expect_field n1 metric 42.5 "the seeded metrics row"
    expect_field n1 role.keeper 0 "the seeded role"
    [ "$(field n1 account.keeper)" != absent ] && [ "$(field n1 account.admin)" != absent ] || fail "an account is missing"
    echo "ok: N-1 holds both accounts, the role, the metrics row and its own AES-GCM vault entry"

    step "upgrade: $image on the N-1 data"
    start upgraded "$image"
    local log upgrade_line from to pending_metrics pending_identity backup
    log="$(docker logs "$name" 2>&1)"
    # A genuine upgrade: another release than N-1's (the same release without pending migrations is rightly no upgrade).
    upgrade_line="$(printf '%s\n' "$log" | grep -o 'Upgrade from .* to [^:]*: [0-9]* metrics and [0-9]* identity migration(s) pending' | tail -n1 || true)"
    [ -n "$upgrade_line" ] || fail "the start on N-1 data reported no upgrade"
    from="$(printf '%s' "$upgrade_line" | sed -n 's/^Upgrade from \(.*\) to [^:]*: .*/\1/p')"
    to="$(printf '%s' "$upgrade_line" | sed -n 's/^Upgrade from .* to \([^:]*\): .*/\1/p')"
    pending_metrics="$(printf '%s' "$upgrade_line" | sed -n 's/.*: \([0-9]*\) metrics and .*/\1/p')"
    pending_identity="$(printf '%s' "$upgrade_line" | sed -n 's/.* and \([0-9]*\) identity migration.*/\1/p')"
    case "${previous##*:}" in
        "${to%%+*}") fail "N-1 ($previous) is the release under test (${to}): pick an older release (previous-release <version>)" ;;
    esac
    echo "ok: upgrade from ${from} to ${to}, ${pending_metrics} metrics and ${pending_identity} identity migration(s)"
    # The start names the backup it took (R1-34); the archive must be on the volume, labelled as a pre-upgrade backup.
    backup="$(printf '%s\n' "$log" | sed -n 's/.*Pre-upgrade backup \([0-9A-Za-z_-]*\) taken.*/\1/p' | tail -n1)"
    [ -n "$backup" ] || fail "the upgrade did not report a pre-upgrade backup"
    grep -q "^${backup}" <<< "$(files 'ls /data/backups/self')" || fail "pre-upgrade backup ${backup} is not on the volume"
    grep -q . <<< "$(files "grep -rl 'pre-upgrade-' /data/backups/self")" || fail "no backup on the volume is labelled pre-upgrade"
    echo "ok: pre-upgrade backup ${backup}"
    wait_ready "$base"
    jar="$WORK/upgraded.jar"
    sign_in "$base" "$jar" "$ADMIN_EMAIL" "$password"
    sign_in "$base" "$WORK/upgraded-keeper.jar" "$KEEPER_EMAIL" "$password"
    expect_admin_session "$base" "$jar"
    expect_status "audit export as Admin after the upgrade" 200 -b "$jar" "$base/audit/export"
    stop
    snapshot up
    expect_same "the upgrade kept every record" up n1 "${records[@]}"
    # Exactly the pending migrations were added: N-1's history is kept in front, the reported number follows it.
    local h before after
    for h in "${histories[@]}"; do
        before="$(field n1 "$h")"; after="$(field up "$h")"
        case "$after" in "$before"|"$before",*) ;; *) fail "the upgrade rewrote N-1's $h" ;; esac
    done
    [ $(( $(field up "${histories[0]}" | tr ',' '\n' | wc -l) - $(field n1 "${histories[0]}" | tr ',' '\n' | wc -l) )) -eq "$pending_metrics" ] \
        || fail "the metrics history did not grow by the ${pending_metrics} reported migration(s)"
    [ $(( $(field up "${histories[1]}" | tr ',' '\n' | wc -l) - $(field n1 "${histories[1]}" | tr ',' '\n' | wc -l) )) -eq "$pending_identity" ] \
        || fail "the identity history did not grow by the ${pending_identity} reported migration(s)"
    echo "ok: both migration histories are N-1's plus exactly the reported migrations"
    local upgraded_audit
    upgraded_audit="$(data_tool audit-since "$(field n1 audit.last)")" || fail "the audit rows could not be read"
    [ -n "$upgraded_audit" ] || fail "the upgraded release wrote no audit row: nothing to tell its data from N-1's"
    echo "ok: $(printf '%s\n' "$upgraded_audit" | wc -l | tr -d ' ') audit row(s) written under $image"

    step "the same release again: a restart is no upgrade"
    local backups
    backups="$(files 'ls /data/backups/self | sort')"
    start restarted "$image"
    # The log is read whole and searched through a here-string: under pipefail, `docker logs | grep -q` fails as soon as
    # grep stops reading early and the writer gets SIGPIPE (a passing check would read as a failure).
    log="$(docker logs "$name" 2>&1)"
    grep -q 'Database schema is current; no upgrade.' <<< "$log" || fail "a restart of the same release was taken for an upgrade"
    if grep -q 'Pre-upgrade backup' <<< "$log"; then fail "a restart of the same release took a pre-upgrade backup"; fi
    wait_ready "$base"
    sign_in "$base" "$WORK/restarted.jar" "$ADMIN_EMAIL" "$password"
    stop
    [ "$(files 'ls /data/backups/self | sort')" = "$backups" ] || fail "a restart of the same release changed the backups"
    snapshot restarted
    expect_same "a restart of the same release changed nothing" restarted up "${records[@]}" "${histories[@]}"

    step "changes after the upgrade: every record, and the app sees them"
    data_tool mutate || fail "changing the records failed"
    snapshot changed
    expect_field changed account.keeper absent "the account deleted after the upgrade"
    expect_field changed role.keeper 1 "the role changed after the upgrade"
    expect_field changed metric 7.25 "the metrics row changed after the upgrade"
    expect_field changed vault.value "$(fingerprint "$changed")" "the vault entry changed after the upgrade"
    start changed "$image"
    sign_in "$base" "$WORK/changed.jar" "$ADMIN_EMAIL" "$password"
    sign_in_refused "$base" "$WORK/changed-keeper.jar" "$KEEPER_EMAIL" "$password"
    stop

    step "recovery: back to N-1 on the pre-upgrade backup"
    run_quiet timeout "$WAIT_SECONDS" docker run --rm "${common[@]}" "$image" --restore-backup latest-pre-upgrade --for-previous-release \
        || fail "staging the pre-upgrade backup failed"
    files 'test -e /data/.restore-pending' || fail "the restore was not staged"
    start recovered "$previous"
    if files 'test -e /data/.restore-pending'; then fail "the previous release did not apply the staged restore"; fi
    sign_in "$base" "$WORK/recovered.jar" "$ADMIN_EMAIL" "$password"
    sign_in "$base" "$WORK/recovered-keeper.jar" "$KEEPER_EMAIL" "$password"
    stop
    snapshot recovered
    expect_same "the recovery brought back N-1's records and migration histories" recovered n1 "${records[@]}" "${histories[@]}"
    local survived
    survived="$(AUDIT_LINES="$upgraded_audit" data_tool audit-present)" || fail "the audit rows could not be read"
    [ "$survived" = 0 ] || fail "${survived} audit row(s) written under $image survived the recovery"
    echo "ok: none of the audit rows written under $image is left"

    step "upgrade again after the recovery"
    start reupgraded "$image"
    log="$(docker logs "$name" 2>&1)"
    grep -q 'Pre-upgrade backup .* taken' <<< "$log" \
        || fail "the start after the recovery was no upgrade: the data was not back on the previous release's schema"
    jar="$WORK/reupgraded.jar"
    sign_in "$base" "$jar" "$ADMIN_EMAIL" "$password"
    sign_in "$base" "$WORK/reupgraded-keeper.jar" "$KEEPER_EMAIL" "$password"
    expect_admin_session "$base" "$jar"
    stop
    snapshot reupgraded
    expect_same "the second upgrade reached the same migration histories" reupgraded up "${histories[@]}"
    expect_same "the second upgrade kept N-1's records" reupgraded n1 "${records[@]}"
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
            if [ "$(printf '%s\n%s\n' "$v" "$current" | sort -V | sed -n 1p)" = "$v" ]; then chosen="$v"; fi
        done <<< "$releases"
    else
        chosen="$(printf '%s\n' "$releases" | tail -n1)"
    fi
    [ -n "$chosen" ] || fail "no published release found${current:+ below $current}"
    OK=1
    printf '%s:%s\n' "$IMAGE_REPO" "$chosen"
}

# ----------------------------------------------------------------------------------------------------------- self-test

# The values a disclosure probe plants: random, handed to containers only through their environment (never on a
# command line this script prints), and searched for afterwards in full and as an 8-character prefix.
probe_values() { # prints: canary, stamp-like value, fingerprint of the canary
    local canary stamp
    canary="$(random_hex 16)"
    stamp="$(random_hex 16 | tr 'a-f' 'A-F')"
    printf '%s\n%s\n%s\n' "$canary" "$stamp" "$(fingerprint "$canary")"
}

# A container that prints the probe values in every shape a log can have: log headers with the value as category,
# message lines, a header with trailing text, entrypoint lines, a bare fingerprint — and one clean header that must pass.
# shellcheck disable=SC2016 # expanded by the probe container's shell, not here
probe_script='printf "%s\n" "info: Whiskers.Probe[1]" "      message $C" "$C" "fail: Whiskers.$S[2]" "warn: $C[3]" \
  "crit: Whiskers.Probe[4] $F" "[entrypoint] $S" "$F" "dbug: Whiskers.${F#sha256:}[5]" "      ${C%????????????????????????}"'

expect_no_disclosure() { # <what> <output-file> <value>...
    local what="$1" file="$2" value hex form; shift 2
    for value in "$@"; do
        hex="${value#sha256:}"
        for form in "$value" "${value:0:8}" "$hex" "${hex:0:8}"; do
            if grep -qiF -- "$form" "$file"; then fail "$what: the output holds a planted value, its fingerprint or an 8-character prefix"; fi
        done
    done
    echo "ok: $what — no planted value, prefix or fingerprint in $(wc -l < "$file" | tr -d ' ') line(s) of output"
}

# Disclosure self-test (R1-47): every path by which these checks put container or tool output into the CI log is run
# with planted values and searched for them — the failure cleanup's log dump, the streamed output of a command, and the
# data tool's failures on malformed or corrupt data. The checks themselves run the same code paths.
self_test() {
    require docker sha256sum
    docker pull -q "$NODE_IMAGE" >/dev/null 2>&1 || docker image inspect "$NODE_IMAGE" >/dev/null 2>&1 \
        || fail "the data tool's image '$NODE_IMAGE' can neither be pulled nor found locally"
    local values canary stamp print out="$WORK/self-test.out" probe="${RUN_ID}-probe" volume="${RUN_ID}-probe-data"
    mapfile -t values < <(probe_values)
    canary="${values[0]}"; stamp="${values[1]}"; print="${values[2]}"
    local planted=(-e "C=$canary" -e "S=$stamp" -e "F=$print")

    step "self-test: the failure cleanup shows filtered container logs only"
    docker run --name "$probe" "${planted[@]}" "$NODE_IMAGE" sh -c "$probe_script" >/dev/null 2>&1 || fail "the probe container did not run"
    CLEANUP+=("docker rm -f $probe")
    if bash "${BASH_SOURCE[0]}" _probe-cleanup "$probe" > "$out" 2>&1; then fail "the probe of the failure cleanup did not fail"; fi
    expect_no_disclosure "failure cleanup" "$out" "${values[@]}"
    grep -qx 'info: Whiskers.Probe\[1\]' "$out" || fail "the failure cleanup did not show the allowlisted log header"
    grep -q 'line(s) withheld' "$out" || fail "the failure cleanup did not report withheld lines"

    step "self-test: streamed command output is filtered"
    bash "${BASH_SOURCE[0]}" _probe-stream "${planted[@]}" > "$out" 2>&1 || fail "the probe of streamed output failed"
    expect_no_disclosure "streamed output" "$out" "${values[@]}"
    grep -qx 'info: Whiskers.Probe\[1\]' "$out" || fail "the streamed output did not show the allowlisted log header"

    step "self-test: the data tool reports failures on malformed data without their content"
    docker volume create "$volume" >/dev/null
    CLEANUP+=("docker volume rm -f $volume")
    local case
    # metrics-db: a database file with a valid header and the planted values as its corrupt body.
    for case in vault-json vault-entry vault-type metrics-db; do
        docker run --rm --network none -v "$volume:/data" "${planted[@]}" -e "CASE=$case" "$NODE_IMAGE" node -e '
            const fs = require("fs"); const { C, S, CASE } = process.env;
            for (const f of fs.readdirSync("/data")) fs.rmSync("/data/" + f, { recursive: true });
            const db = () => { const { DatabaseSync } = require("node:sqlite"); const d = new DatabaseSync("/data/metrics.db");
              d.exec("CREATE TABLE \"__EFMigrationsHistory\" (MigrationId TEXT); INSERT INTO \"__EFMigrationsHistory\" VALUES (\x27m1\x27);" +
                "CREATE TABLE \"__IdentityMigrationsHistory\" (MigrationId TEXT); INSERT INTO \"__IdentityMigrationsHistory\" VALUES (\x27i1\x27);" +
                "CREATE TABLE AspNetUsers (Id TEXT, NormalizedEmail TEXT, PasswordHash TEXT, SecurityStamp TEXT);" +
                "CREATE TABLE ServerMetrics (ServerId TEXT, CpuPercent REAL); CREATE TABLE AuditLog (Id INTEGER);"); d.close(); };
            fs.writeFileSync("/data/roles.json", JSON.stringify({ Roles: [] }));
            const entry = (value) => JSON.stringify({ KdfSalt: Buffer.from(S).toString("base64"), Entries: [{ Key: "artifact-checks-entry", EncryptedValue: value }] });
            // vault-json: invalid JSON around the value (the parser quotes the text near the error).
            if (CASE === "vault-json") { db(); fs.writeFileSync("/data/vault.json", "{\"Entries\": [{\"Key\": \"artifact-checks-entry\", \"EncryptedValue\": x" + C + S + "}]}"); }
            // vault-entry: an EncryptedValue that is not a sealed value but the planted value itself.
            if (CASE === "vault-entry") { db(); fs.writeFileSync("/data/vault.json", entry("g1:" + C + S)); }
            // vault-type: an EncryptedValue of the wrong type, holding the planted value.
            if (CASE === "vault-type") { db(); fs.writeFileSync("/data/vault.json", entry({ Value: C, Stamp: S })); }
            if (CASE === "metrics-db") { fs.writeFileSync("/data/metrics.db", Buffer.concat([Buffer.from("SQLite format 3\0"), Buffer.from(C.repeat(64) + S)]));
              fs.writeFileSync("/data/vault.json", entry("g1:")); }' >/dev/null 2>&1 || fail "the malformed data for '$case' could not be written"
        for command in state seed-vault-legacy seed mutate; do
            docker run --rm -i --network none -v "$volume:/data" -e "VAULT_KEY=$stamp" -e "ADMIN_EMAIL=$ADMIN_EMAIL" \
                -e "MARKER_SEEDED=$canary" -e "MARKER_CHANGED=$canary" "$NODE_IMAGE" \
                sh -c 'cat > /tmp/artifact-data.mjs && exec node /tmp/artifact-data.mjs "$@"' sh "$command" \
                < "$SCRIPT_DIR/artifact-data.mjs" >> "$out.$case" 2>&1 || true
        done
        expect_no_disclosure "data tool on malformed data ($case)" "$out.$case" "${values[@]}"
        grep -q '^artifact-data: ' "$out.$case" || fail "the data tool reported no failure for '$case'"
    done
    OK=1
    echo; echo "disclosure self-test passed"
}

# The two halves the self-test runs as child processes, so that the real exit trap and the real run_quiet do the work.
probe_cleanup() { # <container>: a check that fails after registering the container's logs, as every check does
    LOG_TARGETS+=("docker logs $1")
    fail "disclosure probe: failing on purpose so the cleanup shows the probe container's logs"
}
probe_stream() { # <docker run options...>: a command whose output is streamed through run_quiet, as the checks do
    run_quiet docker run --rm "$@" "$NODE_IMAGE" sh -c "$probe_script" || fail "the streamed probe did not run"
    OK=1
}

case "${1:-}" in
    hardened) [ $# -eq 2 ] || fail "usage: $0 hardened <image>"; hardened "$2" ;;
    upgrade) [ $# -eq 3 ] || fail "usage: $0 upgrade <image> <previous-image>"; upgrade "$2" "$3" ;;
    previous-release) [ $# -le 2 ] || fail "usage: $0 previous-release [<version>]"; previous_release "${2:-}" ;;
    self-test) [ $# -eq 1 ] || fail "usage: $0 self-test"; self_test ;;
    _probe-cleanup) probe_cleanup "$2" ;;
    _probe-stream) shift; probe_stream "$@" ;;
    *) fail "usage: $0 hardened <image> | upgrade <image> <previous-image> | previous-release [<version>] | self-test" ;;
esac

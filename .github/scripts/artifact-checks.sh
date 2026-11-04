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
#       Disclosure self-test: plants random values (hex, letters, a fingerprint and its prefix, a 32-digit number) in a
#       probe container's logs and output, in an HTTP answer, in tool errors, in upgrade log lines and in malformed data,
#       drives the real code paths of these checks with them and fails if any value, its SHA-256 fingerprint or an
#       8-character prefix of either reaches the output.
#
# Output: the CI log gets ONLY text this script writes itself — step names, fixed diagnostics, the arguments it was given,
# counts. Every command's own stdout and stderr (Docker, curl, compose, the containers, the data tool) goes to private
# files in a temporary directory removed on exit; nothing that originates in a container, a tool, an HTTP body or a log
# reaches the CI log, filtered or not. When a check fails, the logs of the containers it started are summarised as counts
# per log level. Every wait is bounded (WAIT_SECONDS, default 300 — the image's own health-check start period); every
# check that cannot be made fails the run. Containers, volumes and the compose project are removed on exit.
set -euo pipefail

WAIT_SECONDS="${WAIT_SECONDS:-300}"
IMAGE_REPO="${IMAGE_REPO:-ghcr.io/lupusmalusdeviant/whiskers}"
# The data tool's runtime (node:sqlite, node:crypto), pinned by digest.
NODE_IMAGE="${NODE_IMAGE:-node:24-alpine@sha256:8a32c8cf649a49915a66e81e7a609223c708d0adabf288ee3f4077d765542f85}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SELF="$SCRIPT_DIR/$(basename "${BASH_SOURCE[0]}")"
ADMIN_EMAIL="admin@artifact-checks.test"
KEEPER_EMAIL="keeper@artifact-checks.test"
RUN_ID="whiskers-ci-$$"
WORK="$(mktemp -d)"
# Git Bash on Windows (local runs): native tools need a mixed path, not /tmp/...
if command -v cygpath >/dev/null 2>&1; then WORK="$(cygpath -m "$WORK")"; fi
CLEANUP=()
LOG_TARGETS=()
OK=0
FAILED=0

# The CI log is fd 3 (diagnostics, errors) and fd 4 (results); fd 1 and fd 2 — where every command writes — are private
# files. Only say, step and fail write to the CI log.
exec 3>&2 4>&1 1>>"$WORK/tool.out" 2>>"$WORK/tool.err"
say() { printf '%s\n' "$*" >&4; }
step() { say ""; say "=== $*"; }
fail() { FAILED=1; printf '::error::%s\n' "$*" >&3; exit 1; }

# A summary of a log that shows none of its text: how many entries of each level, and how many other lines. The levels
# are counted against a fixed list and printed as fixed labels, so no category, event id or message can travel in it.
level_counts() {
    tr -d '\r' | awk '
        /^info: / { info++; next }  /^warn: / { warn++; next }  /^fail: / { failed++; next }
        /^crit: / { crit++; next }  /^dbug: / { dbug++; next }  /^trce: / { trce++; next }
        { other++ }
        END { printf "entries: info=%d warn=%d fail=%d crit=%d dbug=%d trce=%d; other lines=%d (content withheld)\n",
                     info, warn, failed, crit, dbug, trce, other }'
}

# Runs a command privately; when it fails, only the counts of its output are shown.
run_quiet() { # <command> [arguments...]
    local rc=0
    "$@" > "$WORK/quiet.out" 2>&1 || rc=$?
    if [ "$rc" != 0 ]; then say "command output: $(level_counts < "$WORK/quiet.out")"; fi
    return "$rc"
}

on_exit() {
    local rc=$?
    if [ "$OK" != 1 ]; then
        if [ "$rc" != 0 ] && [ "$FAILED" != 1 ]; then
            printf '::error::%s\n' "a command failed unexpectedly (its output is withheld)" >&3
        fi
        for target in "${LOG_TARGETS[@]+"${LOG_TARGETS[@]}"}"; do
            say "----- logs of '${target}': $(eval "$target" 2>/dev/null | tail -n 400 | level_counts)"
        done
    fi
    for ((i=${#CLEANUP[@]}-1; i>=0; i--)); do eval "${CLEANUP[$i]}" >/dev/null 2>&1 || true; done
    exec 1>&4 2>&3
    rm -rf "$WORK"
    exit "$rc"
}
trap on_exit EXIT

require() { for tool in "$@"; do command -v "$tool" >/dev/null 2>&1 || fail "required tool '$tool' is not installed"; done; }

# A random value of hex characters (no shell or YAML metacharacters).
random_hex() { od -An -N"$1" -tx1 /dev/urandom | tr -d ' \n'; }

# The SHA-256 fingerprint artifact-data.mjs reports for a sensitive value ("sha256:<hex>").
fingerprint() { printf 'sha256:%s' "$(printf '%s' "$1" | sha256sum | cut -d' ' -f1)"; }

# A number read from a tool, shown only when it is a plain small count; anything else is "?".
count_or_unknown() { if [[ "$1" =~ ^[0-9]{1,3}$ ]]; then printf '%s' "$1"; else printf '?'; fi; }

# A sign-in password that meets the Identity policy (upper and lower case, digit, symbol), without shell metacharacters.
test_password() { printf 'Ci-%s-Pw!' "$(random_hex 12)"; }

# Secrets volume: the administrator's password as a file the non-root user can read, written by the image itself.
make_secrets_volume() { # <volume> <image> <password>
    docker volume create "$1" >/dev/null || fail "could not create the secrets volume"
    CLEANUP+=("docker volume rm -f $1")
    # shellcheck disable=SC2016 # expanded by the container shell, not here
    run_quiet docker run --rm --user 0 --entrypoint sh -e PW="$3" -v "$1:/secrets" "$2" \
        -c 'umask 022; printf %s "$PW" > /secrets/admin-password; chmod 0444 /secrets/admin-password' \
        || fail "could not write the test secrets with $2"
}

# Waits until <url>/healthz answers Healthy while <container> keeps running.
wait_healthy() { # <base-url> <container> <label>
    local deadline=$((SECONDS + WAIT_SECONDS)) body
    while [ "$SECONDS" -lt "$deadline" ]; do
        if [ "$(docker inspect -f '{{.State.Running}}' "$2" 2>/dev/null)" != "true" ]; then
            fail "$3 stopped while starting (exit code $(count_or_unknown "$(docker inspect -f '{{.State.ExitCode}}' "$2" 2>/dev/null)"))"
        fi
        body="$(curl -fsS --max-time 5 "$1/healthz" 2>/dev/null || true)"
        if [ "$body" = "Healthy" ]; then say "$3 healthy after ${SECONDS}s of the run"; return 0; fi
        sleep 3
    done
    fail "$3 did not become healthy within ${WAIT_SECONDS}s"
}

# Waits until /readyz answers Healthy (database, server list, maintenance, audit trail). The answer's body is compared,
# never shown: a failure names the HTTP status only.
wait_ready() { # <base-url>
    local deadline=$((SECONDS + WAIT_SECONDS)) code=""
    while [ "$SECONDS" -lt "$deadline" ]; do
        code="$(curl -sS -o "$WORK/readyz" -w '%{http_code}' --max-time 10 "$1/readyz" 2>/dev/null || true)"
        if [ "$code" = "200" ] && [ "$(cat "$WORK/readyz")" = "Healthy" ]; then say "ok: /readyz Healthy"; return 0; fi
        sleep 3
    done
    fail "/readyz did not become Healthy within ${WAIT_SECONDS}s (last: HTTP $(count_or_unknown "$code"), body withheld)"
}

expect_status() { # <description> <expected-code> <curl args...>
    local what="$1" want="$2" got; shift 2
    got="$(curl -sS -o "$WORK/discard" -w '%{http_code}' --max-time 30 "$@")" || fail "$what: request failed"
    [ "$got" = "$want" ] || fail "$what: expected HTTP $want, got $(count_or_unknown "$got")"
    say "ok: $what (HTTP $got)"
}

# Posts the sign-in form through /login-local with the antiforgery token of the sign-in page; the cookie jar keeps the
# session. Prints "accepted" or "refused" (captured by the caller, never logged).
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
    [ "$code" = "302" ] || fail "sign-in of $3 answered HTTP $(count_or_unknown "$code")"
    case "$location" in *error*|*login*) echo refused;; *) echo accepted;; esac
}

sign_in() { # <base-url> <cookie-jar> <email> <password>
    [ "$(sign_in_attempt "$@")" = accepted ] || fail "sign-in of $3 was refused"
    say "ok: $3 signed in"
}

sign_in_refused() { # <base-url> <cookie-jar> <email> <password>
    [ "$(sign_in_attempt "$@")" = refused ] || fail "sign-in of $3 was accepted, but must be refused"
    say "ok: $3 refused"
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
    case "$mcp" in 401|302) say "ok: anonymous MCP call refused (HTTP $mcp)";; *) fail "anonymous MCP call answered HTTP $(count_or_unknown "$mcp")";; esac
    expect_status "metrics without a scrape token are disabled" 404 "$1/metrics"
}

# ---------------------------------------------------------------------------------------------- containers and data

# The state of the upgrade check, shared by its helpers (and set by the self-test's probes of the same helpers).
image="" data="" name="" owner="" vault="" seeded="" changed="" port=18080 base="http://127.0.0.1:18080"
common=()

start() { # <name> <image> [docker run options...]
    name="${RUN_ID}-$1"
    local label="$1" img="$2"; shift 2
    docker run -d --name "$name" -p "127.0.0.1:${port}:8080" "${common[@]+"${common[@]}"}" "$@" "$img" >/dev/null \
        || fail "the container '$label' did not start"
    CLEANUP+=("docker rm -f $name")
    LOG_TARGETS+=("docker logs $name")
    wait_healthy "$base" "$name" "$name"
}
stop() { docker stop -t 60 "$name" >/dev/null || fail "the container $name did not stop"; }
# A shell command in <image> on the data volume; its output is the caller's to capture, its errors are private.
files() { docker run --rm --entrypoint sh -v "$data:/data" "$image" -c "$1"; }
# artifact-data.mjs against the stopped data volume, as the volume's owner; the script goes in on stdin, so nothing
# but the volume is mounted (the check also runs from inside a container that talks to the host's Docker).
data_tool() { # <command> [argument]
    docker run --rm -i --network none --user "$owner" -v "$data:/data" -e "VAULT_KEY=$vault" -e "ADMIN_EMAIL=$ADMIN_EMAIL" \
        -e "MARKER_SEEDED=$seeded" -e "MARKER_CHANGED=$changed" -e "AUDIT_LINES=${AUDIT_LINES:-}" "$NODE_IMAGE" \
        sh -c 'cat > /tmp/artifact-data.mjs && exec node /tmp/artifact-data.mjs "$@"' sh "$@" < "$SCRIPT_DIR/artifact-data.mjs"
}

# Reads the upgrade a start reported in its log (<log-file>) and checks it against what the script knows: the target is
# a release version other than N-1's (<previous-image>), the pending counts are plain counts, a pre-upgrade backup is
# named. Sets up_metrics, up_identity, up_backup for the checks that follow; prints none of the values it read.
read_upgrade_log() { # <log-file> <previous-image>
    local line to
    line="$(grep -o 'Upgrade from .* to [^:]*: [0-9]* metrics and [0-9]* identity migration(s) pending' "$1" | tail -n1 || true)"
    [ -n "$line" ] || fail "the start on N-1 data reported no upgrade"
    to="$(sed -n 's/^Upgrade from .* to \([^:]*\): .*/\1/p' <<< "$line")"
    up_metrics="$(sed -n 's/.*: \([0-9]*\) metrics and .*/\1/p' <<< "$line")"
    up_identity="$(sed -n 's/.* and \([0-9]*\) identity migration.*/\1/p' <<< "$line")"
    [[ "$to" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+.-][0-9A-Za-z.+-]*)?$ ]] || fail "the upgrade names no release version as its target"
    [[ "$up_metrics" =~ ^[0-9]{1,3}$ && "$up_identity" =~ ^[0-9]{1,3}$ ]] \
        || fail "the upgrade reports no plausible number of pending migrations"
    # A genuine upgrade: another release than N-1's (the same release without pending migrations is rightly no upgrade).
    [ "${to%%+*}" != "${2##*:}" ] || fail "N-1 ($2) is the release under test: pick an older release (previous-release <version>)"
    up_backup="$(sed -n 's/.*Pre-upgrade backup \([0-9A-Za-z_-]*\) taken.*/\1/p' "$1" | tail -n1)"
    [[ "$up_backup" =~ ^[0-9A-Za-z_-]{1,128}$ ]] || fail "the upgrade did not report a pre-upgrade backup"
    say "ok: the start reported an upgrade from N-1 ($2) to another release, with a pre-upgrade backup"
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

    app="$("${compose[@]}" ps -q serverwatch)" || fail "the hardened compose project could not be listed"
    [ -n "$app" ] || fail "the hardened app container was not created"
    wait_healthy "$base" "$app" "the hardened app"

    step "posture of the running container"
    [ "$(docker inspect -f '{{.Config.User}}' "$app")" = "10001:10001" ] || fail "the app does not run as 10001:10001"
    [ "$(docker exec "$app" id -u)" = "10001" ] || fail "the app process is not uid 10001"
    [ "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$app")" = "true" ] || fail "the root filesystem is writable"
    [ "$(docker inspect -f '{{.HostConfig.Privileged}}' "$app")" = "false" ] || fail "the app runs privileged"
    grep -q '"ALL"' <<< "$(docker inspect -f '{{json .HostConfig.CapDrop}}' "$app")" || fail "capabilities are not all dropped"
    grep -q 'no-new-privileges:true' <<< "$(docker inspect -f '{{json .HostConfig.SecurityOpt}}' "$app")" || fail "no-new-privileges is off"
    if docker exec "$app" sh -c 'touch /app/.write-probe'; then fail "the app could write to /app"; fi
    docker exec "$app" sh -c 'test -s /app/data/metrics.db' || fail "the database was not created in /app/data"
    say "ok: uid 10001, read-only root, no capabilities, no-new-privileges, data on the volume"

    step "health, readiness and the HTTP surface"
    wait_ready "$base"
    check_anonymous_surface "$base"
    sign_in "$base" "$jar" "$ADMIN_EMAIL" "$password"
    expect_admin_session "$base" "$jar"
    expect_status "audit export as Admin" 200 -b "$jar" "$base/audit/export"
    [ "$(docker inspect -f '{{.RestartCount}}' "$app")" = "0" ] || fail "the app restarted during the checks"
    OK=1
    step "hardened profile checks passed"
}

# ----------------------------------------------------------------------------------------------------------- upgrade

upgrade() {
    image="$1"
    local previous="$2" secrets="${RUN_ID}-upgrade-secrets" password jar
    data="${RUN_ID}-upgrade-data"
    require docker curl sha256sum
    docker image inspect "$image" >/dev/null 2>&1 || fail "image '$image' is not present locally"
    docker pull -q "$previous" >/dev/null 2>&1 || docker image inspect "$previous" >/dev/null 2>&1 \
        || fail "the previous release '$previous' can neither be pulled nor found locally"
    docker pull -q "$NODE_IMAGE" >/dev/null 2>&1 || docker image inspect "$NODE_IMAGE" >/dev/null 2>&1 \
        || fail "the data tool's image '$NODE_IMAGE' can neither be pulled nor found locally"
    [ -f "$SCRIPT_DIR/artifact-data.mjs" ] || fail "artifact-data.mjs is missing beside $SELF"
    password="$(test_password)"
    vault="$(random_hex 32)"
    # What the vault entry holds before and after the change: random, so no other run's data can match.
    seeded="seeded-$(random_hex 12)"
    changed="changed-$(random_hex 12)"

    make_secrets_volume "$secrets" "$image" "$password"
    docker volume create "$data" >/dev/null || fail "could not create the data volume"
    CLEANUP+=("docker volume rm -f $data")

    # Every start: the data volume and the vault key. Only the very first start gets the bootstrap credentials — after
    # it, an account that is lost cannot be recreated from configuration, so a sign-in proves the stored account.
    common=(-v "$data:/app/data" -e "VAULT_KEY=$vault" -e "VPN_PROVIDER=none" -e "Vpn__Provider=none"
        -e "WHISKERS_DISABLE_LOCAL_DOCKER=true")
    local bootstrap=(-v "$secrets:/run/whiskers-ci:ro" -e "WHISKERS_ADMIN_EMAIL=$ADMIN_EMAIL"
        -e "WHISKERS_ADMIN_PASSWORD_FILE=/run/whiskers-ci/admin-password")

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
        say "ok: $what"
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
    say "ok: N-1 holds both accounts, the role, the metrics row and its own AES-GCM vault entry"

    step "upgrade: $image on the N-1 data"
    start upgraded "$image"
    docker logs "$name" > "$WORK/upgraded.log" 2>&1 || fail "the log of the upgraded start could not be read"
    read_upgrade_log "$WORK/upgraded.log" "$previous"
    # The start names the backup it took (R1-34); the archive must be on the volume, labelled as a pre-upgrade backup.
    grep -q "^${up_backup}" <<< "$(files 'ls /data/backups/self')" || fail "the reported pre-upgrade backup is not on the volume"
    grep -q . <<< "$(files "grep -rl 'pre-upgrade-' /data/backups/self")" || fail "no backup on the volume is labelled pre-upgrade"
    say "ok: the reported pre-upgrade backup is on the volume, labelled pre-upgrade"
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
    local h before after added_metrics added_identity
    for h in "${histories[@]}"; do
        before="$(field n1 "$h")"; after="$(field up "$h")"
        case "$after" in "$before"|"$before",*) ;; *) fail "the upgrade rewrote N-1's $h" ;; esac
    done
    added_metrics=$(( $(field up "${histories[0]}" | tr ',' '\n' | wc -l) - $(field n1 "${histories[0]}" | tr ',' '\n' | wc -l) ))
    added_identity=$(( $(field up "${histories[1]}" | tr ',' '\n' | wc -l) - $(field n1 "${histories[1]}" | tr ',' '\n' | wc -l) ))
    [ "$added_metrics" -eq "$up_metrics" ] || fail "the metrics history did not grow by the reported number of migrations"
    [ "$added_identity" -eq "$up_identity" ] || fail "the identity history did not grow by the reported number of migrations"
    say "ok: both migration histories are N-1's plus exactly the reported migrations ($added_metrics metrics, $added_identity identity)"
    local upgraded_audit
    upgraded_audit="$(data_tool audit-since "$(field n1 audit.last)")" || fail "the audit rows could not be read"
    [ -n "$upgraded_audit" ] || fail "the upgraded release wrote no audit row: nothing to tell its data from N-1's"
    say "ok: $(printf '%s\n' "$upgraded_audit" | wc -l | tr -d ' ') audit row(s) written under $image"

    step "the same release again: a restart is no upgrade"
    local backups
    backups="$(files 'ls /data/backups/self | sort')"
    start restarted "$image"
    # The log is read into a private file and searched there (`docker logs | grep -q` fails under pipefail as soon as grep
    # stops reading early and the writer gets SIGPIPE).
    docker logs "$name" > "$WORK/restarted.log" 2>&1 || fail "the log of the restarted start could not be read"
    grep -q 'Database schema is current; no upgrade.' "$WORK/restarted.log" || fail "a restart of the same release was taken for an upgrade"
    if grep -q 'Pre-upgrade backup' "$WORK/restarted.log"; then fail "a restart of the same release took a pre-upgrade backup"; fi
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
    [ "$survived" = 0 ] || fail "audit rows written under $image survived the recovery"
    say "ok: none of the audit rows written under $image is left"

    step "upgrade again after the recovery"
    start reupgraded "$image"
    docker logs "$name" > "$WORK/reupgraded.log" 2>&1 || fail "the log of the second upgrade could not be read"
    grep -q 'Pre-upgrade backup .* taken' "$WORK/reupgraded.log" \
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
    step "upgrade and recovery checks passed ($previous -> $image)"
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
    # The result (a version that matched X.Y.Z above) is this command's output, for the workflow to capture.
    printf '%s:%s\n' "$IMAGE_REPO" "$chosen" >&4
}

# ----------------------------------------------------------------------------------------------------------- self-test

# The planted values, exported for the probe containers and the child processes (never on a command line that is
# printed): C hex, S upper-case hex, A letters only, N a 32-digit number, F the fingerprint of C, H its hex.
plant_values() {
    C="$(random_hex 16)"
    S="$(random_hex 16 | tr 'a-f' 'A-F')"
    A="$(random_hex 16 | tr '0-9a-f' 'ghijklmnopqrstuv')"
    N="$(od -An -N32 -tu1 /dev/urandom | tr -d ' \n' | cut -c1-32)"
    F="$(fingerprint "$C")"
    H="${F#sha256:}"
    export C S A N F H
}

# A container that prints the planted values in every shape a log can have: as category (hex, letters, a fingerprint
# prefix), as event id (32 digits), in messages, after a header, as entrypoint lines and bare.
# shellcheck disable=SC2016 # expanded by the probe container's shell, not here
probe_script='printf "%s\n" "info: Whiskers.Probe[1]" "      message $C" "$C" "fail: Whiskers.$S[2]" "warn: $C[3]" \
  "crit: Whiskers.Probe[4] $F" "[entrypoint] $S" "$F" "dbug: Whiskers.$H[5]" "info: Whiskers.$A[6]" \
  "warn: Whiskers.$(printf %s "$H" | cut -c1-8)[7]" "info: Whiskers.Probe[$N]"'
# What level_counts must make of it: counts only.
probe_counts='entries: info=3 warn=2 fail=1 crit=1 dbug=1 trce=0; other lines=4 (content withheld)'
planted_env=(-e C -e S -e A -e N -e F -e H)

expect_no_disclosure() { # <what> <file>...
    local what="$1" value hex form; shift
    for value in "$C" "$S" "$A" "$N" "$F"; do
        hex="${value#sha256:}"
        for form in "$value" "${value:0:8}" "$hex" "${hex:0:8}"; do
            if grep -qiF -- "$form" "$@"; then fail "$what: the output holds a planted value, its fingerprint or an 8-character prefix"; fi
        done
    done
}

# Runs one probe as a child process (the real trap, the real helpers) and checks what it printed: the exit status, one
# exact line it must have printed, and none of the planted values.
probe() { # <what> <expected-exit: 0|fail> <expected-line> <probe-mode> [arguments...]
    local what="$1" want="$2" line="$3" rc=0 out="$WORK/probe.out"; shift 3
    WAIT_SECONDS=8 bash "$SELF" "$@" > "$out" 2>&1 || rc=$?
    expect_no_disclosure "$what" "$out"
    if [ "$want" = 0 ]; then [ "$rc" = 0 ] || fail "$what: the probe failed"; else [ "$rc" != 0 ] || fail "$what: the probe did not fail"; fi
    grep -qxF -- "$line" "$out" || fail "$what: the probe did not print its expected diagnostic"
    say "ok: $what — exit $rc, the expected diagnostic, no planted value, prefix or fingerprint in $(wc -l < "$out" | tr -d ' ') line(s)"
}

# Disclosure self-test (R1-47): every path by which these checks could put foreign text into the CI log is driven with
# planted values — container logs on failure, streamed command output, a container that does not start, file and data
# tool commands that fail, an HTTP answer, upgrade log lines — and the data tool on malformed data, command by command.
self_test() {
    require docker curl sha256sum
    docker pull -q "$NODE_IMAGE" >/dev/null 2>&1 || docker image inspect "$NODE_IMAGE" >/dev/null 2>&1 \
        || fail "the data tool's image '$NODE_IMAGE' can neither be pulled nor found locally"
    plant_values
    local logs="${RUN_ID}-probe-logs" http="${RUN_ID}-probe-http" volume="${RUN_ID}-probe-data" hport=18099

    step "self-test: container logs and streamed output show counts only"
    docker run --name "$logs" "${planted_env[@]}" "$NODE_IMAGE" sh -c "$probe_script" >/dev/null || fail "the log probe container did not run"
    CLEANUP+=("docker rm -f $logs")
    probe "failure cleanup" fail "----- logs of 'docker logs $logs': $probe_counts" _probe-cleanup "$logs"
    probe "streamed output of a failing command" fail "command output: $probe_counts" _probe-stream fail
    probe "streamed output of a passing command" 0 "streamed probe passed" _probe-stream pass

    step "self-test: tool errors stay private"
    docker volume create "$volume" >/dev/null || fail "could not create the probe volume"
    CLEANUP+=("docker volume rm -f $volume")
    probe "a container that does not start" fail "::error::the container 'probe' did not start" _probe-start
    probe "a failing file command" fail "::error::probe: the file command failed" _probe-files "$volume"
    probe "a failing data tool run" fail "::error::probe: the data tool failed" _probe-data-tool "$volume"

    step "self-test: HTTP answers are compared, never shown"
    # shellcheck disable=SC2016 # JavaScript, not shell
    docker run -d --name "$http" -p "127.0.0.1:${hport}:8080" "${planted_env[@]}" "$NODE_IMAGE" node -e '
        require("http").createServer((q, r) => { r.writeHead(503); r.end("Unhealthy " + process.env.C + " " + process.env.A); })
            .listen(8080)' >/dev/null || fail "the HTTP probe container did not start"
    CLEANUP+=("docker rm -f $http")
    probe "readiness failure" fail "::error::/readyz did not become Healthy within 8s (last: HTTP 503, body withheld)" \
        _probe-ready "http://127.0.0.1:${hport}"
    probe "an unexpected HTTP status" fail "::error::probe: expected HTTP 200, got 503" _probe-status "http://127.0.0.1:${hport}"
    probe "health failure" fail "::error::the probe server did not become healthy within 8s" \
        _probe-healthy "http://127.0.0.1:${hport}" "$http"

    step "self-test: upgrade log lines are checked, never reprinted"
    local good="ok: the start reported an upgrade from N-1 (probe:1.0.0) to another release, with a pre-upgrade backup"
    printf '%s\n' "info: Whiskers.Startup[1]" "      Upgrade from $C to 9.9.9+$S: 3 metrics and 1 identity migration(s) pending" \
        "      Pre-upgrade backup $A taken" > "$WORK/upgrade-ok.log"
    probe "an upgrade log with planted versions and backup id" 0 "$good" _probe-upgrade-log "$WORK/upgrade-ok.log" probe:1.0.0
    printf '%s\n' "      Upgrade from 1.0.0 to $C: 3 metrics and 1 identity migration(s) pending" "      Pre-upgrade backup x taken" \
        > "$WORK/upgrade-to.log"
    probe "an upgrade log whose target is no version" fail "::error::the upgrade names no release version as its target" \
        _probe-upgrade-log "$WORK/upgrade-to.log" probe:1.0.0
    printf '%s\n' "      Upgrade from 1.0.0 to 9.9.9: $N metrics and 1 identity migration(s) pending" "      Pre-upgrade backup x taken" \
        > "$WORK/upgrade-count.log"
    probe "an upgrade log with an implausible count" fail "::error::the upgrade reports no plausible number of pending migrations" \
        _probe-upgrade-log "$WORK/upgrade-count.log" probe:1.0.0

    step "self-test: the data tool fails on malformed data, command by command, without its content"
    data="$volume"; owner="0:0"; vault="$S"; seeded="$C"; changed="$C"
    local spec command arg fixture pattern rc
    # <command>|<argument>|<fixture>|<the fixed error that proves the command reached the malformed input>
    for spec in \
        "state||vault-json|state failed while reading vault\.json" \
        "state||vault-sealed|the vault entry does not open with VAULT_KEY" \
        "state||vault-type|state failed while reading vault\.json" \
        "state||roles-json|state failed while reading roles\.json" \
        "state||db-corrupt|state failed while (opening|reading) metrics\.db" \
        "seed-vault-legacy||vault-json|seed-vault-legacy failed while reading vault\.json" \
        "seed||roles-json|seed failed while reading roles\.json" \
        "seed||db-corrupt|seed failed while (opening|reading) metrics\.db" \
        "mutate||roles-json|mutate failed while reading roles\.json" \
        "mutate||vault-json|mutate failed while reading vault\.json" \
        "audit-since|0|db-corrupt|audit-since failed while (opening|reading) metrics\.db" \
        "audit-present||db-corrupt|audit-present failed while (opening|reading) metrics\.db"; do
        IFS='|' read -r command arg fixture pattern <<< "$spec"
        write_fixture "$volume" "$fixture"
        rc=0
        AUDIT_LINES="1|$S|$C" data_tool "$command" ${arg:+"$arg"} > "$WORK/dt.out" 2> "$WORK/dt.err" || rc=$?
        expect_no_disclosure "data tool $command on $fixture" "$WORK/dt.out" "$WORK/dt.err"
        [ "$rc" != 0 ] || fail "data tool $command on $fixture: it did not fail"
        [ ! -s "$WORK/dt.out" ] || fail "data tool $command on $fixture: it wrote output although it failed"
        if [ "$(wc -l < "$WORK/dt.err" | tr -d ' ')" != 1 ] \
            || ! grep -Eqx "artifact-data: ${pattern}( \(details withheld: they can contain stored values\))?" "$WORK/dt.err"; then
            fail "data tool $command on $fixture: not the one fixed error that shows it reached the malformed input"
        fi
        say "ok: data tool $command on $fixture — exit $rc, its own fixed error, nothing disclosed"
    done
    OK=1
    step "disclosure self-test passed"
}

# The data a malformed-data probe runs on: a complete, valid data directory (both histories, the Administrator and the
# keeper account, the metrics row, an audit row, the keeper's role, a vault without the entry) with ONE part replaced by
# malformed data that holds the planted values — so every command gets as far as the part under test.
write_fixture() { # <volume> <fixture>
    docker run --rm --network none -v "$1:/data" "${planted_env[@]}" -e "FIXTURE=$2" "$NODE_IMAGE" node -e '
        const fs = require("fs"); const { DatabaseSync } = require("node:sqlite"); const { C, S, FIXTURE } = process.env;
        for (const f of fs.readdirSync("/data")) fs.rmSync("/data/" + f, { recursive: true });
        const vault = (entries) => JSON.stringify({ KdfSalt: Buffer.from(S).toString("base64"), Entries: entries });
        const entry = (value) => [{ Key: "artifact-checks-entry", EncryptedValue: value }];
        if (FIXTURE === "db-corrupt")
            fs.writeFileSync("/data/metrics.db", Buffer.concat([Buffer.from("SQLite format 3\0"), Buffer.from(C.repeat(64) + S)]));
        else {
            const d = new DatabaseSync("/data/metrics.db");
            d.exec(`CREATE TABLE "__EFMigrationsHistory" (MigrationId TEXT); INSERT INTO "__EFMigrationsHistory" VALUES (\x27m1\x27);
                CREATE TABLE "__IdentityMigrationsHistory" (MigrationId TEXT); INSERT INTO "__IdentityMigrationsHistory" VALUES (\x27i1\x27);
                CREATE TABLE AspNetUsers (Id TEXT, UserName TEXT, NormalizedUserName TEXT, Email TEXT, NormalizedEmail TEXT,
                    PasswordHash TEXT, SecurityStamp TEXT, ConcurrencyStamp TEXT, AccessFailedCount INTEGER, LockoutEnd TEXT);
                CREATE TABLE ServerMetrics (ServerId TEXT, ServerName TEXT, Timestamp TEXT, CpuPercent REAL, MemoryUsedBytes INTEGER,
                    MemoryTotalBytes INTEGER, DiskUsedBytes INTEGER, DiskTotalBytes INTEGER);
                CREATE TABLE AuditLog (Id INTEGER, Timestamp TEXT, Action TEXT);`);
            const user = d.prepare("INSERT INTO AspNetUsers (Id, NormalizedEmail, PasswordHash, SecurityStamp) VALUES (?, ?, ?, ?)");
            user.run("admin-id", "ADMIN@ARTIFACT-CHECKS.TEST", C, S);
            user.run("keeper-id", "KEEPER@ARTIFACT-CHECKS.TEST", C, S);
            d.prepare("INSERT INTO ServerMetrics (ServerId, CpuPercent) VALUES (\x27artifact-checks-marker\x27, 42.5)").run();
            d.prepare("INSERT INTO AuditLog VALUES (1, ?, ?)").run(S, C);
            d.close();
        }
        fs.writeFileSync("/data/roles.json", FIXTURE === "roles-json" ? "{\"Roles\": [{\"Email\": x" + C + S + "}]}"
            : JSON.stringify({ Roles: [{ Email: "keeper@artifact-checks.test", Role: 0 }] }));
        fs.writeFileSync("/data/vault.json",
            FIXTURE === "vault-json" ? "{\"Entries\": [{\"Key\": \"artifact-checks-entry\", \"EncryptedValue\": x" + C + S + "}]}"
            : FIXTURE === "vault-sealed" ? vault(entry("g1:" + C + S))     // not a sealed value but the planted value itself
            : FIXTURE === "vault-type" ? vault(entry({ Value: C, Stamp: S }))  // the wrong type, holding the planted value
            : vault([]));' >/dev/null || fail "the fixture '$2' could not be written"
}

# The probes the self-test runs as child processes: each drives one real path of these checks with planted values.
probe_mode() {
    case "$1" in
        _probe-cleanup) # a check that fails after registering a container's logs, as every check does
            LOG_TARGETS+=("docker logs $2"); fail "disclosure probe: failing on purpose" ;;
        _probe-stream) # a command whose output run_quiet keeps private
            if [ "$2" = pass ]; then
                run_quiet docker run --rm "${planted_env[@]}" "$NODE_IMAGE" sh -c "$probe_script" || fail "the streamed probe failed"
                OK=1; say "streamed probe passed"
            else
                run_quiet docker run --rm "${planted_env[@]}" "$NODE_IMAGE" sh -c "$probe_script; exit 3" || fail "the streamed probe failed"
            fi ;;
        _probe-start) # docker run refuses the options: its error names the planted value
            port=18098; base="http://127.0.0.1:18098"; start probe "$NODE_IMAGE" --env-file "probe-$C.env" ;;
        _probe-files) # a file command that prints the planted values and fails
            image="$NODE_IMAGE"; data="$2"
            files "echo $C; echo $S >&2; exit 3" || fail "probe: the file command failed" ;;
        _probe-data-tool) # docker cannot run the data tool as this user: its error names the planted value
            data="$2"; owner="probe-$A"; vault="$C"; seeded="$S"; changed="$S"
            data_tool state > "$WORK/probe.state" || fail "probe: the data tool failed" ;;
        _probe-ready) wait_ready "$2" ;;
        _probe-status) expect_status "probe" 200 "$2/readyz" ;;
        _probe-healthy) wait_healthy "$2" "$3" "the probe server" ;;
        _probe-upgrade-log) read_upgrade_log "$2" "$3"; OK=1 ;;
    esac
}

case "${1:-}" in
    hardened) [ $# -eq 2 ] || fail "usage: $SELF hardened <image>"; hardened "$2" ;;
    upgrade) [ $# -eq 3 ] || fail "usage: $SELF upgrade <image> <previous-image>"; upgrade "$2" "$3" ;;
    previous-release) [ $# -le 2 ] || fail "usage: $SELF previous-release [<version>]"; previous_release "${2:-}" ;;
    self-test) [ $# -eq 1 ] || fail "usage: $SELF self-test"; self_test ;;
    _probe-*) probe_mode "$@" ;;
    *) fail "usage: $SELF hardened <image> | upgrade <image> <previous-image> | previous-release [<version>] | self-test" ;;
esac

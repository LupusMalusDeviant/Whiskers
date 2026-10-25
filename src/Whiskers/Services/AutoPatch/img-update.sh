#!/bin/bash
# Update one compose-managed container to the newest image under the SAME tag (a same-tag pull, or a
# newer image that is already local but was never rolled out). Recreates only this service through
# compose, verifies it, and rolls back to the previous image if the check fails.
#
# Shipped by Whiskers (Services/AutoPatch) and run on the host as root. Proven by hand on the whole
# fleet on 2026-10-25 before it was automated.
#
# Guards:
#   - major guard: the image version's major changes (e.g. caddy:latest 2 -> 3)          -> skip
#   - unknown major on a floating tag (latest/server/...) without a version label        -> skip
#   - compose drift: the compose file no longer matches the running container            -> abort,
#     so an update never silently ships unrelated compose changes. Asked of compose itself with a
#     dry-run `up` while the tag points at the running image (IGNORE_DRIFT=1 overrides; only for an
#     operator who has compared the configuration by hand - Whiskers never sets it)
#   - databases (postgres/mysql/mariadb): verified dump first, otherwise abort
# Env:   DRY_RUN=1  stop after the guards, put the old tag back, report UPD-WOULD-UPDATE
# Usage: img-update.sh <container>
# The last line starting with UPD- is the result; the whole log is /var/log/img-update-<c>.log.
C=$1
[[ "$C" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] || { echo "UPD-ABORT bad-container-name"; exit 2; }
TS=$(date -u +%Y%m%dT%H%M%SZ)
exec > "/var/log/img-update-$C.log" 2>&1
lbl() { docker inspect -f "{{index .Config.Labels \"$1\"}}" "$C"; }
DIR=$(lbl com.docker.compose.project.working_dir)
PROJ=$(lbl com.docker.compose.project)
SVC=$(lbl com.docker.compose.service)
IFS=, read -ra FS <<< "$(lbl com.docker.compose.project.config_files)"
CF=(); for f in "${FS[@]}"; do CF+=(-f "$f"); done
dc() { (cd "$DIR" && docker compose -p "$PROJ" "${CF[@]}" "$@"); }
IMG=$(docker inspect -f '{{.Config.Image}}' "$C")
OLD_ID=$(docker inspect -f '{{.Image}}' "$C")
REPO=${IMG%:*}; [ "$REPO" = "$IMG" ] && REPO=$IMG
TAG=${IMG##*:}; [ "$TAG" = "$IMG" ] && TAG=latest
# Per container: two services on one host often share a repo (two postgres:*), and a date-only tag
# would let the second backup overwrite the first.
BK="$REPO:pre-upd-${TS:0:8}-$C"
PRE_H=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$C")

# Version of an image: OCI label first, then the usual *_VERSION env of official images.
ver() {
    local v
    v=$(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$1" 2>/dev/null)
    [ -z "$v" ] || [ "$v" = "<no value>" ] && v=$(docker image inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$1" \
        | sed -n -E 's/^(PG_VERSION|MYSQL_VERSION|MARIADB_VERSION|NGINX_VERSION|CADDY_VERSION|NODE_VERSION)=//p' | head -1)
    [ "$v" = "<no value>" ] && v=
    echo "$v"
}
major() { echo "$1" | grep -oE '^[vV]?[0-9]+' | tr -d vV; }

echo "container=$C project=$PROJ service=$SVC image=$IMG old=$(echo "$OLD_ID" | cut -c8-19) health_before=$PRE_H"
[ -n "$SVC" ] && [ -n "$DIR" ] || { echo UPD-ABORT no-compose-labels; exit 1; }

# Drift guard: would `up` change anything about this container other than its image? Not by comparing
# the config-hash label with `config --hash`: for every service with an env_file the two never match,
# not even right after compose created the container (2026-10-25, all six env_file services on Badwolf).
# So compose is asked directly, with a dry run, while the tag points at the image that runs - then
# "Running" means the file still describes this container and anything else is drift.
TAG_BEFORE=$(docker image inspect -f '{{.Id}}' "$IMG" 2>/dev/null)
docker tag "$OLD_ID" "$IMG"
PLAN=$(dc --dry-run up -d --no-deps --no-build --pull never "$SVC" 2>&1)
[ -n "$TAG_BEFORE" ] && docker tag "$TAG_BEFORE" "$IMG"
if [ -z "$IGNORE_DRIFT" ] && ! grep -qE "Container $C +Running" <<< "$PLAN"; then
    echo "UPD-ABORT compose-drift $(grep -m1 -iE 'container|error' <<< "$PLAN" | tr -s ' ')"; exit 1
fi

docker tag "$OLD_ID" "$BK" && echo "backup_tag=$BK"
docker pull -q "$IMG" >/dev/null 2>&1 && echo pulled || echo "pull failed - using local tag"
NEW_ID=$(docker image inspect -f '{{.Id}}' "$IMG")
if [ "$NEW_ID" = "$OLD_ID" ]; then echo UPD-NOCHANGE; exit 0; fi

OLD_V=$(ver "$OLD_ID"); NEW_V=$(ver "$NEW_ID")
OLD_M=$(major "$OLD_V"); NEW_M=$(major "$NEW_V")
echo "version $OLD_V -> $NEW_V (major $OLD_M -> $NEW_M) new=$(echo "$NEW_ID" | cut -c8-19)"
TAG_PINS_MAJOR=no; [[ "$TAG" =~ ^v?[0-9] ]] && TAG_PINS_MAJOR=yes
if [ -n "$OLD_M" ] && [ -n "$NEW_M" ]; then
    if [ "$OLD_M" != "$NEW_M" ]; then docker tag "$OLD_ID" "$IMG"; echo "UPD-SKIP-MAJOR $OLD_V -> $NEW_V"; exit 0; fi
elif [ "$TAG_PINS_MAJOR" = no ]; then
    docker tag "$OLD_ID" "$IMG"; echo "UPD-SKIP-UNKNOWN-MAJOR tag=$TAG"; exit 0
fi

if [ -n "$DRY_RUN" ]; then
    docker tag "$OLD_ID" "$IMG"; echo "UPD-WOULD-UPDATE $OLD_V -> $NEW_V"; exit 0
fi

# Databases: a verified dump before anything is recreated. Keeps the last three per container.
KIND=other
case "$IMG" in *postgres*) KIND=postgres;; *mysql*|*mariadb*) KIND=mysql;; esac
if [ "$KIND" != other ]; then
    install -d -m 700 /root/.img-backup
    DUMP=/root/.img-backup/$C-$TS.sql.gz
    if [ "$KIND" = postgres ]; then
        docker exec "$C" sh -c 'pg_dumpall -U "${POSTGRES_USER:-postgres}"' | gzip > "$DUMP"
        MARK='PostgreSQL database cluster dump complete'
    else
        docker exec "$C" sh -c 'exec mysqldump -uroot -p"$MYSQL_ROOT_PASSWORD" --all-databases --single-transaction --routines --events --triggers 2>/dev/null' | gzip > "$DUMP"
        MARK='Dump completed'
    fi
    SIZE=$(stat -c %s "$DUMP")
    if gzip -t "$DUMP" && [ "$SIZE" -gt 1024 ] && zcat "$DUMP" | tail -5 | grep -q "$MARK"; then
        echo "db_dump=$DUMP size=$SIZE verified"
        ls -1t /root/.img-backup/"$C"-*.sql.gz 2>/dev/null | tail -n +4 | xargs -r rm -f
    else
        docker tag "$OLD_ID" "$IMG"; echo "UPD-ABORT db-dump-unverified size=$SIZE"; exit 1
    fi
fi

dbok() {
    case "$KIND" in
        postgres) docker exec "$C" sh -c 'pg_isready -U "${POSTGRES_USER:-postgres}"' >/dev/null 2>&1 ;;
        mysql) docker exec "$C" sh -c 'mysqladmin ping -uroot -p"$MYSQL_ROOT_PASSWORD" --silent' >/dev/null 2>&1 ;;
        *) true ;;
    esac
}
# Healthy: image healthcheck if it was healthy before; otherwise running for 30 s without restart.
check() {
    for i in $(seq 1 90); do
        read -r st h rc <<< "$(docker inspect -f '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}} {{.RestartCount}}' "$C" 2>/dev/null)"
        if [ "$PRE_H" = healthy ]; then
            [ "$h" = healthy ] && dbok && return 0
            [ "$h" = unhealthy ] && return 1
        elif [ "$i" -ge 15 ] && [ "$st" = running ] && [ "$rc" = 0 ] && dbok; then
            return 0
        fi
        [ "$st" = exited ] && return 1
        sleep 2
    done
    return 1
}

dc up -d --no-deps --no-build --pull never "$SVC"
if check && [ "$(docker inspect -f '{{.Image}}' "$C")" = "$NEW_ID" ]; then
    echo "UPD-OK $OLD_V -> $NEW_V"
else
    echo "--- logs"; docker logs --tail 20 "$C" 2>&1
    docker tag "$OLD_ID" "$IMG"
    dc up -d --no-deps --no-build --pull never "$SVC"
    if check; then echo "UPD-FAILED-ROLLBACK-VERIFIED"; else echo "UPD-FAILED-ROLLBACK-NOT-VERIFIED"; fi
fi
docker inspect -f '{{.Config.Image}} {{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}} {{.Image}}' "$C"

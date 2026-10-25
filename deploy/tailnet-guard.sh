#!/bin/bash
# tailnet-guard — keeps the Tailscale data path intact on this host.
#
# Why this exists: on 2026-08-20 a `docker compose up --build` (Whiskers deploy) produced a veth
# link-change storm, during which tailscale0 lost its IPv4/IPv6 addresses. tailscaled logged the loss
# ("LinkChange: major ... rebind-reason=[ips-changed]") but never restored them, so every 100.64/10
# route was gone: `tailscale ping` still worked (userspace disco) while ALL TCP traffic to the tailnet
# — Whiskers' mTLS Docker connections to five remote hosts — silently timed out via the default route.
#
# The check is deliberately end-to-end: an address on tailscale0 alone does not prove reachability.
#
#   --check-only   report and exit non-zero on breakage; never restart anything.
#
# Exit codes: 0 = healthy (or repaired), 1 = broken and repair failed / --check-only found breakage.
set -u

PROBE_PORT=${TAILNET_GUARD_PORT:-2376}
# Whiskers' servers.json on this host. The default is the maintainer's pre-rebrand install directory;
# override it with WHISKERS_SERVERS_JSON for any other layout.
SERVERS_JSON=${WHISKERS_SERVERS_JSON:-/opt/ServerWatch/data/servers.json}
CHECK_ONLY=0
[ "${1:-}" = "--check-only" ] && CHECK_ONLY=1

log() { echo "$*"; logger -t tailnet-guard -- "$*"; }

# One repair at a time. The timer and a deploy's postflight can fire together, and two concurrent
# `systemctl restart tailscaled` runs race each other into a worse state than the one being repaired.
# A read-only check needs no lock.
if [ "$CHECK_ONLY" = 0 ]; then
    exec 9>/run/tailnet-guard.lock || true
    if ! flock -n 9; then
        echo "another tailnet-guard run holds the lock — skipping"
        exit 0
    fi
fi

probe_targets() {
    # The fleet's own Docker endpoints are the most honest probe: that is the path that must work.
    python3 - "$SERVERS_JSON" <<'PY' 2>/dev/null
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(0)
items = d if isinstance(d, list) else d.get('Servers', [])
for s in items:
    if s.get('Enabled', True) and s.get('TcpHost'):
        print(s['TcpHost'])
PY
}

has_v4() { ip -4 addr show tailscale0 2>/dev/null | grep -q 'inet '; }

tcp_ok() {
    python3 - "$1" "$PROBE_PORT" <<'PY' 2>/dev/null
import socket, sys
s = socket.socket(); s.settimeout(5)
try:
    s.connect((sys.argv[1], int(sys.argv[2])))
except Exception:
    sys.exit(1)
finally:
    s.close()
PY
}

healthy() {
    has_v4 || { REASON="tailscale0 has no IPv4 address"; return 1; }
    local reached=0 target
    for target in $(probe_targets); do
        if tcp_ok "$target"; then reached=1; break; fi
    done
    # No configured TCP peers at all → the address check is all we can assert.
    [ -z "$(probe_targets)" ] && return 0
    [ "$reached" = 1 ] || { REASON="no tailnet peer answered on :$PROBE_PORT"; return 1; }
    return 0
}

REASON=""
if healthy; then
    [ "$CHECK_ONLY" = 1 ] && echo "tailnet OK"
    exit 0
fi

log "tailnet BROKEN: $REASON"
if [ "$CHECK_ONLY" = 1 ]; then exit 1; fi

log "restarting tailscaled"
systemctl restart tailscaled || { log "FAILED: systemctl restart tailscaled"; exit 1; }
sleep 8

if healthy; then
    log "tailnet restored after tailscaled restart"
    exit 0
fi

log "STILL BROKEN after restart: $REASON — needs a human"
exit 1

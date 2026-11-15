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
# Exit codes: 0 = healthy (or repaired), 1 = broken and repair failed / --check-only found breakage,
# or `ip` is missing.
#
# Tools (no Python — contract 0017 part I):
#   whole script: ip, grep (the address check) — without `ip` the script refuses to start, it cannot judge anything
#   repair:       systemctl, flock, logger (as before)
#   peer list:    jq — without it there are no probe targets and only the address is checked
#   TCP probe:    nc (-z -w5), else bash /dev/tcp under timeout 5 — without either only the address is checked
set -u

PROBE_PORT=${TAILNET_GUARD_PORT:-2376}
# Whiskers' servers.json on this host. The default is the maintainer's pre-rebrand install directory;
# override it with WHISKERS_SERVERS_JSON for any other layout.
SERVERS_JSON=${WHISKERS_SERVERS_JSON:-/opt/ServerWatch/data/servers.json}
CHECK_ONLY=0
[ "${1:-}" = "--check-only" ] && CHECK_ONLY=1

log() { echo "$*"; logger -t tailnet-guard -- "$*"; }

for tool in ip grep; do
    command -v "$tool" >/dev/null 2>&1 || { echo "tailnet-guard: missing tool: $tool" >&2; exit 1; }
done

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
    # Without jq (or with an unreadable file) there are no targets, and the address check is all we assert.
    command -v jq >/dev/null 2>&1 || return 0
    jq -r '(if type == "array" then . else (.Servers // []) end)[]
           | select(type == "object")
           | select((if has("Enabled") then .Enabled else true end) as $e | $e != null and $e != false and $e != 0 and $e != "")
           | select((.TcpHost // "") | type == "string" and length > 0)
           | .TcpHost' "$SERVERS_JSON" 2>/dev/null
}

has_v4() { ip -4 addr show tailscale0 2>/dev/null | grep -q 'inet '; }

# The TCP probe: `nc -z -w5`, else bash's /dev/tcp under `timeout 5`. Without either the probe cannot run, and
# the address check is all we assert (as without targets).
can_probe() { command -v nc >/dev/null 2>&1 || command -v timeout >/dev/null 2>&1; }

tcp_ok() {
    if command -v nc >/dev/null 2>&1; then
        nc -z -w5 "$1" "$PROBE_PORT" >/dev/null 2>&1
    else
        timeout 5 bash -c 'exec 3<>"/dev/tcp/$1/$2"' tcp-probe "$1" "$PROBE_PORT" >/dev/null 2>&1
    fi
}

healthy() {
    has_v4 || { REASON="tailscale0 has no IPv4 address"; return 1; }
    local reached=0 target targets
    targets=$(probe_targets)
    # No configured TCP peers at all, or no probe tool → the address check is all we can assert.
    [ -z "$targets" ] && return 0
    can_probe || return 0
    for target in $targets; do
        if tcp_ok "$target"; then reached=1; break; fi
    done
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

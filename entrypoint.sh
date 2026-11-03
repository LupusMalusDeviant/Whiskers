#!/bin/sh
set -e

# A normal start passes no arguments; the command-line tools (--restore-backup, --migrate-to-postgres) get theirs, after
# the same VPN bring-up (a database reachable only over the tailnet stays reachable for them).

# VPN bring-up is being moved behind the in-app IVpnProvider abstraction (Services/Vpn).
# When VPN_PROVIDER is set (Vpn__Provider), the app manages the VPN (or it runs on the host /
# a sidecar for "none"), so this shell skips its legacy Tailscale bring-up. With VPN_PROVIDER
# unset, existing deployments keep their current behavior unchanged.
if [ -n "$VPN_PROVIDER" ]; then
    echo "[entrypoint] VPN_PROVIDER='$VPN_PROVIDER' set — VPN handled in-app, skipping legacy bring-up."
    echo "[entrypoint] Starting Whiskers..."
    exec dotnet Whiskers.dll "$@"
fi

# With network_mode: host and Tailscale already running on the host, a second tailscaled here cannot open
# tailscale0 ("TUN device busy") and its start-up still disturbs the host daemon — on Badwolf every container
# restart cost 1-2 minutes of tailnet outage. If the interface exists, the VPN is someone else's job.
if [ -e /sys/class/net/tailscale0 ] && [ "${VPN_FORCE_LEGACY_TAILSCALE:-}" != "1" ]; then
    echo "[entrypoint] tailscale0 already exists (host-managed Tailscale) — skipping legacy bring-up."
    echo "[entrypoint] Starting Whiskers..."
    exec dotnet Whiskers.dll "$@"
fi

# --- Legacy Tailscale bring-up (used when VPN_PROVIDER is unset) ---
# This path is the host operator's container start, outside the app: it is not decided or audited by Whiskers'
# Mutation Gateway (the in-app provider is, R1-43). Start Tailscale daemon in background (state persisted in the
# data directory, WHISKERS_DATA_DIR, default /app/data).
ts_state_dir="${WHISKERS_DATA_DIR:-/app/data}/tailscale"
mkdir -p "$ts_state_dir"
if command -v tailscaled >/dev/null 2>&1; then
    echo "[entrypoint] Starting tailscaled..."
    tailscaled --state="$ts_state_dir/tailscaled.state" --socket=/var/run/tailscale/tailscaled.sock &

    # Wait for daemon to be ready
    sleep 2

    # Auto-connect if auth key is provided.
    # R1-43 (F-21): the key never goes on a command line — with pid: host every process on the host could read it
    # there. It is written by the shell's own printf (a builtin: no process, no argv) into a file of mode 0600 in a
    # directory of mode 0700, handed over as --auth-key=file:<path> (what `tailscale up` reads a key from), and the
    # directory is removed as soon as the call returns.
    if [ -n "${TAILSCALE_AUTHKEY:-}" ]; then
        echo "[entrypoint] Connecting to Tailscale with auth key..."
        ts_old_umask=$(umask)
        umask 077
        ts_key_dir=$(mktemp -d "${TMPDIR:-/tmp}/whiskers-ts.XXXXXX")
        trap 'rm -rf "$ts_key_dir"' INT TERM HUP
        printf '%s' "$TAILSCALE_AUTHKEY" > "$ts_key_dir/key"
        umask "$ts_old_umask"
        ts_status=0
        tailscale up --auth-key="file:$ts_key_dir/key" --accept-routes --hostname="${TAILSCALE_HOSTNAME:-serverwatch}" || ts_status=$?
        rm -rf "$ts_key_dir"
        trap - INT TERM HUP
        if [ "$ts_status" -ne 0 ]; then
            # Explicit, not swallowed: Whiskers still starts (as before), but the failure is on the console.
            echo "[entrypoint] ERROR: tailscale up failed with exit code $ts_status — the tailnet is NOT connected." >&2
        fi
    elif tailscale status >/dev/null 2>&1; then
        echo "[entrypoint] Tailscale already authenticated."
    else
        echo "[entrypoint] Tailscale daemon running but not authenticated. Use the Settings UI to connect."
    fi
else
    echo "[entrypoint] Tailscale not installed, skipping VPN setup."
fi

# The key is not needed any more: the application process does not inherit it.
unset TAILSCALE_AUTHKEY

# Start the .NET application
echo "[entrypoint] Starting Whiskers..."
exec dotnet Whiskers.dll "$@"

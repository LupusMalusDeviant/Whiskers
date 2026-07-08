#!/bin/sh
set -e

# VPN bring-up is being moved behind the in-app IVpnProvider abstraction (Services/Vpn).
# When VPN_PROVIDER is set (Vpn__Provider), the app manages the VPN (or it runs on the host /
# a sidecar for "none"), so this shell skips its legacy Tailscale bring-up. With VPN_PROVIDER
# unset, existing deployments keep their current behavior unchanged.
if [ -n "$VPN_PROVIDER" ]; then
    echo "[entrypoint] VPN_PROVIDER='$VPN_PROVIDER' set — VPN handled in-app, skipping legacy bring-up."
    echo "[entrypoint] Starting Whiskers..."
    exec dotnet Whiskers.dll
fi

# --- Legacy Tailscale bring-up (used when VPN_PROVIDER is unset) ---
# Start Tailscale daemon in background (state persisted in /app/data/tailscale)
mkdir -p /app/data/tailscale
if command -v tailscaled >/dev/null 2>&1; then
    echo "[entrypoint] Starting tailscaled..."
    tailscaled --state=/app/data/tailscale/tailscaled.state --socket=/var/run/tailscale/tailscaled.sock &

    # Wait for daemon to be ready
    sleep 2

    # Auto-connect if auth key is provided
    if [ -n "$TAILSCALE_AUTHKEY" ]; then
        echo "[entrypoint] Connecting to Tailscale with auth key..."
        tailscale up --authkey="$TAILSCALE_AUTHKEY" --accept-routes --hostname="${TAILSCALE_HOSTNAME:-serverwatch}" || true
    elif tailscale status >/dev/null 2>&1; then
        echo "[entrypoint] Tailscale already authenticated."
    else
        echo "[entrypoint] Tailscale daemon running but not authenticated. Use the Settings UI to connect."
    fi
else
    echo "[entrypoint] Tailscale not installed, skipping VPN setup."
fi

# Start the .NET application
echo "[entrypoint] Starting Whiskers..."
exec dotnet Whiskers.dll

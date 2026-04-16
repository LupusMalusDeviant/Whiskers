#!/bin/sh
set -e

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
echo "[entrypoint] Starting ServerWatch..."
exec dotnet ServerWatch.dll

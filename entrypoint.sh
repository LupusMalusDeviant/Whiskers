#!/bin/sh
set -e

# A normal start passes no arguments; the command-line tools (--restore-backup, --migrate-to-postgres) get theirs, after
# the same VPN bring-up (a database reachable only over the tailnet stays reachable for them).

# --- R1-43: VPN keys never on a command line, never in the application's environment ---
# With pid: host every host user can read a process's command line and its /proc/<pid>/environ. The application reads
# its keys from files (Vpn__Tailscale__AuthKeyFile, Vpn__Netbird__SetupKeyFile — a Docker secret is best); a key given
# as Vpn__Tailscale__AuthKey / Vpn__Netbird__SetupKey (what docker-compose.yml sets from TAILSCALE_AUTHKEY /
# NETBIRD_SETUP_KEY) is moved here into such a file, and the variable is dropped before the application starts.
# Files are written by the shell's own printf (a builtin: no process, no argv) under umask 077: a 0700 directory with a
# 0600 file, in memory where possible (WHISKERS_SECRET_DIR, else /dev/shm, else TMPDIR).
secret_parent() {
    if [ -n "${WHISKERS_SECRET_DIR:-}" ]; then
        printf '%s' "$WHISKERS_SECRET_DIR"
    elif [ -d /dev/shm ] && [ -w /dev/shm ]; then
        printf '%s' /dev/shm
    else
        printf '%s' "${TMPDIR:-/tmp}"
    fi
}

# Writes $1 into a new private file and prints its path.
key_to_file() {
    (
        umask 077
        kf_dir=$(mktemp -d "$(secret_parent)/whiskers-vpnkey.XXXXXX")
        printf '%s' "$1" > "$kf_dir/key"
        printf '%s' "$kf_dir/key"
    )
}

# Every key name leaves the exported environment before this script runs its first external command (rm, tr, mktemp,
# tailscaled, tailscale up …): an exported variable is inherited by each of them, and the long-lived tailscaled would
# carry it in /proc/<pid>/environ for the container's lifetime. The values the script needs are kept in shell
# variables that are not exported (assigning to a name that was not exported does not export it — so a variable of
# that name inherited from the container's environment is unset first, which also drops its export flag); the
# configuration names are also read behind the DOTNET_ / ASPNETCORE_ prefixes, in the order Whiskers' configuration
# lets them win (a bare name, then DOTNET_, then ASPNETCORE_ — measured on the real builder in SecretEnvironmentTests).
# What this script cannot see: a value given on Whiskers' command line ("$@", e.g. --Vpn:Provider=…) or in the data
# directory's JSON layers, which both win over the environment. Only the exact spelling is moved: a variant in other
# letter case still reaches Whiskers' environment block, where Whiskers drops it from what its own children inherit.
unset ws_ts_legacy_key ws_ts_key ws_nb_key ws_ts_keyfile ws_nb_keyfile ws_cfg_provider ws_provider ws_mirror ws_in_app ws_prefix
ws_ts_legacy_key=${TAILSCALE_AUTHKEY:-}
ws_ts_key=${Vpn__Tailscale__AuthKey:-${DOTNET_Vpn__Tailscale__AuthKey:-${ASPNETCORE_Vpn__Tailscale__AuthKey:-}}}
ws_nb_key=${Vpn__Netbird__SetupKey:-${DOTNET_Vpn__Netbird__SetupKey:-${ASPNETCORE_Vpn__Netbird__SetupKey:-}}}
ws_ts_keyfile=${Vpn__Tailscale__AuthKeyFile:-${DOTNET_Vpn__Tailscale__AuthKeyFile:-${ASPNETCORE_Vpn__Tailscale__AuthKeyFile:-}}}
ws_nb_keyfile=${Vpn__Netbird__SetupKeyFile:-${DOTNET_Vpn__Netbird__SetupKeyFile:-${ASPNETCORE_Vpn__Netbird__SetupKeyFile:-}}}
# The provider the application is configured with (Vpn:Provider, same precedence).
ws_cfg_provider=${Vpn__Provider:-${DOTNET_Vpn__Provider:-${ASPNETCORE_Vpn__Provider:-}}}
for ws_prefix in "" DOTNET_ ASPNETCORE_; do
    unset "${ws_prefix}TAILSCALE_AUTHKEY" "${ws_prefix}NETBIRD_SETUP_KEY" "${ws_prefix}TS_AUTHKEY" "${ws_prefix}NB_SETUP_KEY" \
        "${ws_prefix}Vpn__Tailscale__AuthKey" "${ws_prefix}Vpn__Netbird__SetupKey"
done

# Key directories that a crash or a kill left behind — this script's and the application's (they live only for one
# call, or for one run of the container). WHISKERS_SECRET_DIR, when set, is the only place they can be.
remove_stale_keys() {
    if [ -d "$1" ]; then
        for ws_stale in "$1"/whiskers-vpn-* "$1"/whiskers-ts.* "$1"/whiskers-vpnkey.*; do
            if [ -d "$ws_stale" ]; then rm -rf "$ws_stale"; fi
        done
    fi
}
if [ -n "${WHISKERS_SECRET_DIR:-}" ]; then
    remove_stale_keys "$WHISKERS_SECRET_DIR"
else
    remove_stale_keys /dev/shm
    remove_stale_keys "${TMPDIR:-/tmp}"
fi

# Who brings the VPN up — never both, never neither unless the provider says none (R1-43):
#   in-app (Whiskers, governed) when Whiskers' provider is tailscale or netbird;
#   the legacy bring-up below   when VPN_PROVIDER is unset and Whiskers' provider is not tailscale/netbird;
#   nobody                      when VPN_PROVIDER is set and Whiskers' provider is none (the VPN runs on the host).
# VPN_PROVIDER is the compose mirror of Vpn__Provider (docker-compose.yml sets Vpn__Provider=${VPN_PROVIDER:-none}, so
# compose with VPN_PROVIDER unset stays on the legacy path). Set without any Vpn__Provider in the environment, it is
# handed to Whiskers as Vpn__Provider, so this script and Whiskers decide on the same provider.
if [ -z "$ws_cfg_provider" ] && [ -n "${VPN_PROVIDER:-}" ]; then
    ws_cfg_provider=$VPN_PROVIDER
    Vpn__Provider=$VPN_PROVIDER
    export Vpn__Provider
fi
ws_provider=$(printf '%s' "$ws_cfg_provider" | tr '[:upper:]' '[:lower:]')
ws_mirror=$(printf '%s' "${VPN_PROVIDER:-}" | tr '[:upper:]' '[:lower:]')
if [ -n "$ws_mirror" ] && [ "$ws_mirror" != "$ws_provider" ]; then
    echo "[entrypoint] WARNING: VPN_PROVIDER='$ws_mirror' differs from Vpn__Provider='$ws_provider' — Whiskers uses '$ws_provider'; the legacy bring-up is skipped because VPN_PROVIDER is set." >&2
fi
case "$ws_provider" in
    tailscale|netbird) ws_in_app=1 ;;
    *) ws_in_app= ;;
esac
# Only the in-app provider of that name reads a key; a file named explicitly wins. A key no provider will read is
# dropped — said on the console, so a provider set only where this script cannot see it (appsettings) is noticed.
if [ -n "$ws_ts_key" ]; then
    if [ "$ws_provider" = "tailscale" ] && [ -z "$ws_ts_keyfile" ]; then
        Vpn__Tailscale__AuthKeyFile=$(key_to_file "$ws_ts_key")
        export Vpn__Tailscale__AuthKeyFile
    elif [ "$ws_provider" != "tailscale" ]; then
        echo "[entrypoint] WARNING: a Tailscale auth key (Vpn__Tailscale__AuthKey, possibly prefixed) is set but the VPN provider is '${ws_provider:-unset}' — the key is dropped. Set Vpn__Provider=tailscale (or VPN_PROVIDER) to use it." >&2
    fi
fi
if [ -n "$ws_nb_key" ]; then
    if [ "$ws_provider" = "netbird" ] && [ -z "$ws_nb_keyfile" ]; then
        Vpn__Netbird__SetupKeyFile=$(key_to_file "$ws_nb_key")
        export Vpn__Netbird__SetupKeyFile
    elif [ "$ws_provider" != "netbird" ]; then
        echo "[entrypoint] WARNING: a NetBird setup key (Vpn__Netbird__SetupKey, possibly prefixed) is set but the VPN provider is '${ws_provider:-unset}' — the key is dropped. Set Vpn__Provider=netbird (or VPN_PROVIDER) to use it." >&2
    fi
fi
ws_ts_key=
ws_nb_key=

# Every way into the application: no key variable (they were never exported again), no trap left behind.
start_app() {
    ws_ts_legacy_key=
    trap - EXIT INT TERM HUP
    echo "[entrypoint] Starting Whiskers..."
    exec dotnet Whiskers.dll "$@"
}

# The legacy bring-up runs only where the table above says so: not when VPN_PROVIDER is set (the app manages the VPN,
# or it runs on the host / a sidecar for "none"), and not when Whiskers' own provider is tailscale or netbird.
if [ -n "${VPN_PROVIDER:-}" ] || [ -n "$ws_in_app" ]; then
    echo "[entrypoint] VPN provider '${ws_provider:-none}' — VPN handled in-app or outside the container, skipping legacy bring-up."
    start_app "$@"
fi

# With network_mode: host and Tailscale already running on the host, a second tailscaled here cannot open
# tailscale0 ("TUN device busy") and its start-up still disturbs the host daemon — on Badwolf every container
# restart cost 1-2 minutes of tailnet outage. If the interface exists, the VPN is someone else's job.
if [ -e /sys/class/net/tailscale0 ] && [ "${VPN_FORCE_LEGACY_TAILSCALE:-}" != "1" ]; then
    echo "[entrypoint] tailscale0 already exists (host-managed Tailscale) — skipping legacy bring-up."
    start_app "$@"
fi

# --- Legacy Tailscale bring-up (VPN_PROVIDER unset and Whiskers' provider not tailscale/netbird) ---
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

    # Auto-connect if auth key is provided. TAILSCALE_AUTHKEY reaches this script only when it is set on the container
    # itself (docker run -e TAILSCALE_AUTHKEY=…): docker-compose.yml passes the .env value as Vpn__Tailscale__AuthKey,
    # which only the in-app provider reads.
    # R1-43 (F-21): the key never goes on a command line. It goes into a private file (see above), handed over as
    # --auth-key=file:<path> (what `tailscale up` reads a key from), and the directory is removed as soon as the call
    # returns — also when this script ends early (set -e, EXIT) or is stopped (the stop is passed on, not swallowed).
    if [ -n "$ws_ts_legacy_key" ]; then
        echo "[entrypoint] Connecting to Tailscale with auth key..."
        ts_old_umask=$(umask)
        umask 077
        ts_key_dir=$(mktemp -d "$(secret_parent)/whiskers-ts.XXXXXX")
        trap 'rm -rf "$ts_key_dir"' EXIT
        trap 'rm -rf "$ts_key_dir"; exit 130' INT
        trap 'rm -rf "$ts_key_dir"; exit 143' TERM
        trap 'rm -rf "$ts_key_dir"; exit 129' HUP
        printf '%s' "$ws_ts_legacy_key" > "$ts_key_dir/key"
        ws_ts_legacy_key=
        umask "$ts_old_umask"
        ts_status=0
        tailscale up --auth-key="file:$ts_key_dir/key" --accept-routes --hostname="${TAILSCALE_HOSTNAME:-serverwatch}" || ts_status=$?
        rm -rf "$ts_key_dir"
        trap - EXIT INT TERM HUP
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

# Start the .NET application
start_app "$@"

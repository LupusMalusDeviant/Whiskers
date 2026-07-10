FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Copy every csproj first so `dotnet restore` layer-caches. Whiskers references Whiskers.Data and the two
# per-provider migration assemblies (ADR-0004), and both migration projects reference Whiskers.Data — all four
# must be present or restore/publish can't resolve the moved entities (e.g. McpToolCallEntity, WebhookEntity).
COPY src/Whiskers/Whiskers.csproj ./Whiskers/
COPY src/Whiskers.Data/Whiskers.Data.csproj ./Whiskers.Data/
COPY src/Whiskers.Migrations.Sqlite/Whiskers.Migrations.Sqlite.csproj ./Whiskers.Migrations.Sqlite/
COPY src/Whiskers.Migrations.Postgres/Whiskers.Migrations.Postgres.csproj ./Whiskers.Migrations.Postgres/
RUN dotnet restore ./Whiskers/Whiskers.csproj
COPY src/Whiskers/ ./Whiskers/
COPY src/Whiskers.Data/ ./Whiskers.Data/
COPY src/Whiskers.Migrations.Sqlite/ ./Whiskers.Migrations.Sqlite/
COPY src/Whiskers.Migrations.Postgres/ ./Whiskers.Migrations.Postgres/
WORKDIR /src/Whiskers
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install docker CLI, SSH client, and Tailscale
# Detect base-image distro codename so Tailscale URLs work whether the
# .NET runtime image is Debian or Ubuntu (currently mcr.../aspnet:10.0 is noble).
RUN . /etc/os-release \
    && echo "Base image: $ID $VERSION_CODENAME" \
    && apt-get update -o Acquire::Retries=3 \
    && apt-get install -y --no-install-recommends \
        docker.io \
        openssh-client \
        sshpass \
        curl \
        gnupg \
        ca-certificates \
    && curl -fsSL "https://pkgs.tailscale.com/stable/${ID}/${VERSION_CODENAME}.noarmor.gpg" \
        | tee /usr/share/keyrings/tailscale-archive-keyring.gpg >/dev/null \
    && curl -fsSL "https://pkgs.tailscale.com/stable/${ID}/${VERSION_CODENAME}.tailscale-keyring.list" \
        | tee /etc/apt/sources.list.d/tailscale.list \
    && apt-get update -o Acquire::Retries=3 \
    && apt-get install -y --no-install-recommends tailscale \
    # gnupg is build-only (fetching the Tailscale repo key) — purge it. curl STAYS: it is the
    # container HEALTHCHECK probe (see below) and is small. docker.io (local container web-terminal
    # `docker exec`), openssh-client/sshpass (Tailscale-SSH terminal + SSH fallback) and tailscale
    # (legacy VPN bring-up) ARE runtime deps of the full profile and stay. ca-certificates stays (TLS).
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Non-root user (uid 10001) for the hardened/remote deployment posture. The image DEFAULT stays
# root because the "full" Local-management mode needs root for nsenter/host-shell and the
# in-container VPN daemon. Hardened deployments opt in with `user: "10001:10001"` in compose
# (see docker-compose.hardened.yml). A fresh named data volume inherits this ownership; bind-mounts
# must be chowned by the operator. We chown /app so the app can write /app/data when run as 10001.
RUN groupadd -g 10001 serverwatch 2>/dev/null || true \
    && useradd -u 10001 -g 10001 -M -s /usr/sbin/nologin serverwatch 2>/dev/null || true \
    && mkdir -p /app/data \
    && chown -R 10001:10001 /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Container liveness probe: curl the anonymous /healthz endpoint (status word only). start-period
# covers first-boot init + DB migration. Orchestrators like K8s use their own httpGet probes instead.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["/app/entrypoint.sh"]

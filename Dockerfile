FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ServerWatch/ServerWatch.csproj ./ServerWatch/
RUN dotnet restore ./ServerWatch/ServerWatch.csproj
COPY src/ServerWatch/ ./ServerWatch/
WORKDIR /src/ServerWatch
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
    # curl + gnupg are build-only (fetching the Tailscale repo key) — purge so they don't linger in
    # the runtime image. docker.io (local container web-terminal `docker exec`), openssh-client/sshpass
    # (Tailscale-SSH terminal + SSH fallback) and tailscale (legacy VPN bring-up) ARE runtime deps of
    # the full profile and stay. ca-certificates stays (TLS).
    && apt-get purge -y curl gnupg \
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

ENTRYPOINT ["/app/entrypoint.sh"]

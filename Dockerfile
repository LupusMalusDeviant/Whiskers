# R1-52: every base image is pinned by digest (tag@sha256:…), so the same Dockerfile builds from the same bytes until
# the pin is moved on purpose. The tag says what the digest is; Dependabot (package-ecosystem "docker",
# .github/dependabot.yml) proposes the new digest when the image behind the tag is rebuilt (OS and runtime fixes) and a
# new tag when one is released; the PR runs CI like any other. To move a pin by hand:
#   docker buildx imagetools inspect <image:tag>   (the "Digest:" line is the multi-arch index digest)
# BuildReproducibilityTests refuses an unpinned FROM.

# The docker CLI only (a static binary): the local web terminal's `docker exec` (TerminalLaunch.Local). Whiskers talks
# to the Docker API itself (Docker.DotNet) and runs `docker compose` on the managed host (nsenter / SSH), so the image
# carries no daemon, containerd or runc.
FROM docker:29.9.3-cli@sha256:4d742ab934c2149b628bf26369ac3ec51cb91e2e28fe6722a0e86a6d6efc4bd5 AS docker-cli

# The SDK is the exact version of global.json (BuildReproducibilityTests): the SDK adds its own framework packages to
# packages.lock.json, so a different SDK patch would not restore the lock files in locked mode. Move both together.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src
# Copy every csproj (with its packages.lock.json) first so `dotnet restore` layer-caches. Whiskers references
# Whiskers.Data and the two per-provider migration assemblies (ADR-0004), and both migration projects reference
# Whiskers.Data — all four must be present or restore/publish can't resolve the moved entities (e.g.
# McpToolCallEntity, WebhookEntity). Directory.Build.props turns the lock files on.
COPY Directory.Build.props ./
COPY src/Whiskers/Whiskers.csproj src/Whiskers/packages.lock.json ./Whiskers/
COPY src/Whiskers.Data/Whiskers.Data.csproj src/Whiskers.Data/packages.lock.json ./Whiskers.Data/
COPY src/Whiskers.Migrations.Sqlite/Whiskers.Migrations.Sqlite.csproj src/Whiskers.Migrations.Sqlite/packages.lock.json ./Whiskers.Migrations.Sqlite/
COPY src/Whiskers.Migrations.Postgres/Whiskers.Migrations.Postgres.csproj src/Whiskers.Migrations.Postgres/packages.lock.json ./Whiskers.Migrations.Postgres/
# R1-52: locked restore — exactly the packages (and content hashes) in the lock files, or the build fails.
RUN dotnet restore ./Whiskers/Whiskers.csproj --locked-mode
COPY src/Whiskers/ ./Whiskers/
COPY src/Whiskers.Data/ ./Whiskers.Data/
COPY src/Whiskers.Migrations.Sqlite/ ./Whiskers.Migrations.Sqlite/
COPY src/Whiskers.Migrations.Postgres/ ./Whiskers.Migrations.Postgres/
WORKDIR /src/Whiskers
RUN dotnet publish -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
WORKDIR /app

# Ownership proof for the official MCP Registry: must equal "name" in server.json (ServerJsonManifestTests).
LABEL io.modelcontextprotocol.server.name="io.github.LupusMalusDeviant/whiskers"

# Runtime tools of the full profile — exactly what Whiskers starts as a process (ImageRuntimeToolsTests checks the
# source against this list and the built image against the source):
#   docker              local web terminal `docker exec` — the static CLI from the docker-cli stage, no engine (R1-52)
#   nsenter (base)      local host shell and host commands (util-linux, part of the base image)
#   ssh, sshpass        SSH terminal, SSH host commands and the SSH Docker tunnel
#   tailscale(d)        in-app VPN provider and the legacy entrypoint bring-up; iptables for tailscaled's netfilter
#                       rules (also a dependency of the tailscale package; named here because Whiskers relies on
#                       it — ImageRuntimeToolsTests checks tailscaled comes up in iptables mode)
#   pg_dump/pg_restore/psql  PostgreSQL self-backup (R1-33)
#   curl                container HEALTHCHECK
# Detect base-image distro codename so Tailscale URLs work whether the
# .NET runtime image is Debian or Ubuntu (currently mcr.../aspnet:10.0 is noble).
RUN . /etc/os-release \
    && echo "Base image: $ID $VERSION_CODENAME" \
    && apt-get update -o Acquire::Retries=3 \
    && apt-get install -y --no-install-recommends \
        openssh-client \
        sshpass \
        iptables \
        curl \
        gnupg \
        ca-certificates \
    && curl -fsSL "https://pkgs.tailscale.com/stable/${ID}/${VERSION_CODENAME}.noarmor.gpg" \
        | tee /usr/share/keyrings/tailscale-archive-keyring.gpg >/dev/null \
    && curl -fsSL "https://pkgs.tailscale.com/stable/${ID}/${VERSION_CODENAME}.tailscale-keyring.list" \
        | tee /etc/apt/sources.list.d/tailscale.list \
    # R1-33: PostgreSQL client tools (pg_dump/pg_restore/psql) for the self-backup of a PostgreSQL database. From the
    # PostgreSQL project's repository: the newest client dumps every older server, the distribution's may be older
    # than the server it has to dump.
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        | gpg --dearmor -o /usr/share/keyrings/postgresql-archive-keyring.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/postgresql-archive-keyring.gpg] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update -o Acquire::Retries=3 \
    && apt-get install -y --no-install-recommends tailscale postgresql-client-18 \
    # gnupg is build-only (the Tailscale and PostgreSQL repository keys) — purge it. curl STAYS: it is the
    # container HEALTHCHECK probe (see below) and is small. openssh-client/sshpass (Tailscale-SSH terminal + SSH
    # fallback), iptables and tailscale (VPN) ARE runtime deps of the full profile and stay. ca-certificates stays (TLS).
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker

COPY --from=build /app/publish .
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Non-root user (uid 10001) for the hardened/remote deployment posture. The image DEFAULT stays
# root because the "full" Local-management mode needs root for nsenter/host-shell and the
# in-container VPN daemon. Hardened deployments opt in with `user: "10001:10001"` in compose
# (see docker-compose.hardened.yml). A fresh named data volume inherits this ownership; bind-mounts
# must be chowned by the operator. We chown /app so the app can write /app/data when run as 10001.
RUN groupadd -g 10001 whiskers 2>/dev/null || true \
    && useradd -u 10001 -g 10001 -M -s /usr/sbin/nologin whiskers 2>/dev/null || true \
    && mkdir -p /app/data \
    && chown -R 10001:10001 /app

ENV ASPNETCORE_URLS=http://+:8080
# R1-33: the client tools by path, not through Debian's pg_wrapper (which picks a version from the environment):
# the same binaries CI tests with.
ENV WHISKERS_PG_BIN_DIR=/usr/lib/postgresql/18/bin
EXPOSE 8080

# Container liveness probe: curl the anonymous /healthz endpoint (status word only). start-period
# covers first-boot init and an upgrade's pre-upgrade backup + DB migrations (R1-34): Whiskers answers no
# request until they are done. A success ends it early, so a normal start is healthy within seconds.
# Orchestrators like K8s use their own httpGet probes instead (Helm: values.probes).
HEALTHCHECK --interval=30s --timeout=5s --start-period=5m --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

ENTRYPOINT ["/app/entrypoint.sh"]

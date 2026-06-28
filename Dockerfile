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
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["/app/entrypoint.sh"]

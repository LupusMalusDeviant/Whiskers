using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.ServerConfig;

public class ServerConfigService : IServerConfigService, IInitializable
{
    private readonly JsonFileStore<ServerConfigData> _store;
    private readonly IOptions<DockerSettings> _dockerSettings;
    private readonly ILogger<ServerConfigService> _logger;
    private ServerConfigData _cached = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private readonly string _sshKeysBasePath;

    public ServerConfigService(IOptions<DockerSettings> dockerSettings, ILogger<ServerConfigService> logger, string? storePath = null, DataPathOptions? dataPaths = null)
    {
        var paths = dataPaths ?? DataPathOptions.Default;
        _store = new JsonFileStore<ServerConfigData>(storePath ?? paths.ServersJson);
        _sshKeysBasePath = paths.SshKeysDir;
        _dockerSettings = dockerSettings;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsInitialized { get; private set; }

    public int Order => 50;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_store.Exists())
        {
            _cached = await _store.LoadAsync();
            _logger.LogInformation("Loaded {Count} server configs", _cached.Servers.Count);
        }
        else if (ShouldCreateDefaultLocalServer())
        {
            // Create default local server
            _cached = new ServerConfigData
            {
                Servers = new List<Models.ServerConfig>
                {
                    new()
                    {
                        Id = "local",
                        Name = "Local",
                        ConnectionType = ConnectionType.Local,
                        SocketPath = _dockerSettings.Value.SocketPath,
                        IsDefault = true,
                        Enabled = true
                    }
                }
            };
            await _store.SaveAsync(_cached);
            _logger.LogInformation("Created default local server config");
        }
        else
        {
            // No local Docker (e.g. Whiskers running on Kubernetes, Track A/V4): start with an
            // empty fleet — the dashboard shows the first-server guide instead.
            _cached = new ServerConfigData();
            await _store.SaveAsync(_cached);
            _logger.LogInformation("No local Docker socket — starting without a default local server");
        }

        IsInitialized = true;
    }

    /// <summary>V4 (kubernetesImplement Track A): whether a fresh install should seed the default
    /// "local" Docker server. On Kubernetes there is no meaningful local Docker host — the socket
    /// isn't mounted — so a first boot without one starts with an empty fleet instead of a
    /// permanently-unreachable entry. <c>WHISKERS_DISABLE_LOCAL_DOCKER=true</c> forces it off.</summary>
    private bool ShouldCreateDefaultLocalServer()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("WHISKERS_DISABLE_LOCAL_DOCKER"), "true",
                StringComparison.OrdinalIgnoreCase))
            return false;

        var socketPath = _dockerSettings.Value.SocketPath ?? "";
        if (socketPath.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
            return File.Exists(socketPath["unix://".Length..]);

        // npipe:// (Windows dev) or tcp:// values: keep the historical behaviour — create the
        // entry; reachability is probed at runtime anyway.
        return true;
    }

    public List<Models.ServerConfig> GetServers()
    {
        return _cached.Servers.ToList();
    }

    public List<Models.ServerConfig> GetEnabledServers()
    {
        return _cached.Servers.Where(s => s.Enabled).ToList();
    }

    /// <summary>Resolves a server by id, falling back to its display name. Ids win, so a name that happens
    /// to equal another server's id cannot shadow it. The fallback exists because everything a human or an
    /// agent reads — dashboards, notifications, log lines — shows the NAME ("Rabenhof (Hetzner)"), while the
    /// tools want the id ("rabenhof"); an agent acting on a fleet-wide alert otherwise fails with
    /// "Server not found" and concludes the container does not exist.</summary>
    public Models.ServerConfig? GetServer(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId)) return null;

        return _cached.Servers.FirstOrDefault(s => s.Id == serverId)
            ?? _cached.Servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase))
            ?? _cached.Servers.FirstOrDefault(s => string.Equals(s.Name, serverId, StringComparison.OrdinalIgnoreCase));
    }

    public Models.ServerConfig? GetDefaultServer()
    {
        return _cached.Servers.FirstOrDefault(s => s.IsDefault)
               ?? _cached.Servers.FirstOrDefault();
    }

    // Whether the interactive web terminal is usable for a server: Local (nsenter) and SSH always, and
    // TCP/mTLS only when Tailscale SSH (keyless tailnet shell) is enabled — the mTLS Docker proxy can't
    // carry an interactive attach stream. Single source of truth for the Dashboard + ContainerDetail UI.
    public bool SupportsTerminal(string? serverId)
    {
        var s = serverId == null ? GetDefaultServer() : GetServer(serverId);
        return s != null && (s.ConnectionType != ConnectionType.TCP || s.TailscaleSsh);
    }

    public async Task AddServerAsync(Models.ServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        await _lock.WaitAsync();
        try
        {
            // Ids must be unique: a duplicate would make every id lookup ambiguous (GetServer also
            // matches case-insensitively, so case variants count as duplicates too).
            if (_cached.Servers.Any(s => string.Equals(s.Id, server.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A server with the id '{server.Id}' already exists.");

            // Copy-on-write: build a new list + data object and swap the reference atomically, so the
            // lock-free readers always see a fully-consistent immutable snapshot.
            var servers = new List<Models.ServerConfig>(_cached.Servers) { server };
            var newData = new ServerConfigData { Servers = servers };
            await _store.SaveAsync(newData);
            _cached = newData;
            Whiskers.Services.Mutations.ConfigChange.Record($"server:{server.Id}", null, server);   // R1-22
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Updates a server. Its kubeconfig reference always stays as stored: only the overload with a new
    /// reference changes it, so no caller holding an older copy (onboarding, SSH-key cleanup) can point it back.</summary>
    public Task UpdateServerAsync(Models.ServerConfig server) => UpdateServerAsync(server, newKubeconfigRef: null);

    /// <inheritdoc />
    public async Task<Models.ServerConfig?> UpdateServerAsync(Models.ServerConfig server, string? newKubeconfigRef)
    {
        await _lock.WaitAsync();
        try
        {
            var index = _cached.Servers.FindIndex(s => s.Id == server.Id);
            if (index < 0) return null;
            var current = _cached.Servers[index];
            // Decided under the lock: a save that queued behind a kubeconfig rotation keeps the rotated reference.
            server.KubeconfigRef = newKubeconfigRef ?? current.KubeconfigRef;
            var servers = new List<Models.ServerConfig>(_cached.Servers);
            servers[index] = server;
            var newData = new ServerConfigData { Servers = servers };
            await _store.SaveAsync(newData);
            _cached = newData;
            Whiskers.Services.Mutations.ConfigChange.Record($"server:{server.Id}", current, server);   // R1-22
            return current;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task RemoveServerAsync(string serverId) => TakeServerAsync(serverId);

    /// <inheritdoc />
    public async Task<Models.ServerConfig?> TakeServerAsync(string serverId)
    {
        await _lock.WaitAsync();
        try
        {
            var server = _cached.Servers.FirstOrDefault(s => s.Id == serverId);
            if (server is { IsDefault: true })
                throw new InvalidOperationException("Cannot remove the default server");

            var servers = _cached.Servers.Where(s => s.Id != serverId).ToList();
            var newData = new ServerConfigData { Servers = servers };
            await _store.SaveAsync(newData);
            _cached = newData;

            // Clean up SSH keys
            var keyDir = Path.Combine(_sshKeysBasePath, serverId);
            if (Directory.Exists(keyDir))
                Directory.Delete(keyDir, true);
            return server;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveSshKeyAsync(string serverId, string fileName, byte[] keyData)
    {
        // Strip any directory components to prevent path traversal (e.g. "../../etc/passwd").
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Ungültiger Dateiname");

        var keyDir = Path.Combine(_sshKeysBasePath, serverId);
        Directory.CreateDirectory(keyDir);

        var keyPath = Path.Combine(keyDir, fileName);
        await File.WriteAllBytesAsync(keyPath, keyData);

        // Set restrictive permissions (SSH requires this). Linux-only API — guarded so the analyzer
        // and any non-Linux dev build are satisfied; the app itself runs in Linux containers.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Update server config on a clone — never mutate the cached live object the lock-free readers see.
        var server = GetServer(serverId)?.Clone();
        if (server != null)
        {
            server.SshKeyFileName = fileName;
            await UpdateServerAsync(server);
        }
    }

    public string? GetSshKeyPath(Models.ServerConfig server)
    {
        if (string.IsNullOrEmpty(server.SshKeyFileName))
            return null;

        var path = Path.Combine(_sshKeysBasePath, server.Id, server.SshKeyFileName);
        return File.Exists(path) ? path : null;
    }

    public async Task DeleteSshKeyAsync(string serverId)
    {
        var keyDir = Path.Combine(_sshKeysBasePath, serverId);
        try { if (Directory.Exists(keyDir)) Directory.Delete(keyDir, true); }
        catch { /* best effort */ }

        var server = GetServer(serverId)?.Clone();
        if (server != null && server.SshKeyFileName != null)
        {
            server.SshKeyFileName = null;
            await UpdateServerAsync(server);
        }
    }
}

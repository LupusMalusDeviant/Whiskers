using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.ServerConfig;

public class ServerConfigService : IServerConfigService
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

    public async Task InitializeAsync()
    {
        if (_store.Exists())
        {
            _cached = await _store.LoadAsync();
            _logger.LogInformation("Loaded {Count} server configs", _cached.Servers.Count);
        }
        else
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

        IsInitialized = true;
    }

    public List<Models.ServerConfig> GetServers()
    {
        return _cached.Servers.ToList();
    }

    public List<Models.ServerConfig> GetEnabledServers()
    {
        return _cached.Servers.Where(s => s.Enabled).ToList();
    }

    public Models.ServerConfig? GetServer(string serverId)
    {
        return _cached.Servers.FirstOrDefault(s => s.Id == serverId);
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
        await _lock.WaitAsync();
        try
        {
            // Copy-on-write: build a new list + data object and swap the reference atomically, so the
            // lock-free readers always see a fully-consistent immutable snapshot.
            var servers = new List<Models.ServerConfig>(_cached.Servers) { server };
            var newData = new ServerConfigData { Servers = servers };
            await _store.SaveAsync(newData);
            _cached = newData;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateServerAsync(Models.ServerConfig server)
    {
        await _lock.WaitAsync();
        try
        {
            var index = _cached.Servers.FindIndex(s => s.Id == server.Id);
            if (index >= 0)
            {
                var servers = new List<Models.ServerConfig>(_cached.Servers);
                servers[index] = server;
                var newData = new ServerConfigData { Servers = servers };
                await _store.SaveAsync(newData);
                _cached = newData;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveServerAsync(string serverId)
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

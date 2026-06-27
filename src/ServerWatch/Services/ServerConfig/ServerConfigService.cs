using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.ServerConfig;

public class ServerConfigService
{
    private readonly JsonFileStore<ServerConfigData> _store;
    private readonly IOptions<DockerSettings> _dockerSettings;
    private readonly ILogger<ServerConfigService> _logger;
    private ServerConfigData _cached = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private const string SshKeysBasePath = "/app/data/ssh-keys";

    public ServerConfigService(IOptions<DockerSettings> dockerSettings, ILogger<ServerConfigService> logger)
    {
        _store = new JsonFileStore<ServerConfigData>("/app/data/servers.json");
        _dockerSettings = dockerSettings;
        _logger = logger;
    }

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
            _cached.Servers.Add(server);
            await _store.SaveAsync(_cached);
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
                _cached.Servers[index] = server;
                await _store.SaveAsync(_cached);
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

            _cached.Servers.RemoveAll(s => s.Id == serverId);
            await _store.SaveAsync(_cached);

            // Clean up SSH keys
            var keyDir = Path.Combine(SshKeysBasePath, serverId);
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

        var keyDir = Path.Combine(SshKeysBasePath, serverId);
        Directory.CreateDirectory(keyDir);

        var keyPath = Path.Combine(keyDir, fileName);
        await File.WriteAllBytesAsync(keyPath, keyData);

        // Set restrictive permissions (SSH requires this). Linux-only API — guarded so the analyzer
        // and any non-Linux dev build are satisfied; the app itself runs in Linux containers.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Update server config
        var server = GetServer(serverId);
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

        var path = Path.Combine(SshKeysBasePath, server.Id, server.SshKeyFileName);
        return File.Exists(path) ? path : null;
    }
}

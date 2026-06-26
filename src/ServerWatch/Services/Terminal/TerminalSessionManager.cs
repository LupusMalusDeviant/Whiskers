using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Terminal;

public class TerminalSessionManager : ITerminalSessionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private readonly TerminalSettings _settings;
    private readonly ServerConfigService _serverConfigService;
    private readonly ILogger<TerminalSessionManager> _logger;
    private readonly Timer _cleanupTimer;

    public TerminalSessionManager(
        IOptions<TerminalSettings> settings,
        ServerConfigService serverConfigService,
        ILogger<TerminalSessionManager> logger)
    {
        _settings = settings.Value;
        _serverConfigService = serverConfigService;
        _logger = logger;
        _cleanupTimer = new Timer(CleanupIdleSessions, null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public TerminalSession CreateSession(string? containerId = null, string? serverId = null)
    {
        if (!_settings.Enabled)
            throw new InvalidOperationException("Terminal is disabled");

        if (_sessions.Count >= _settings.MaxSessions)
            throw new InvalidOperationException($"Maximum terminal sessions ({_settings.MaxSessions}) reached");

        // For remote servers, use SSH + docker exec
        if (serverId != null)
        {
            var server = _serverConfigService.GetServer(serverId);
            if (server != null && server.ConnectionType == ConnectionType.SSH && containerId != null)
            {
                var session = new TerminalSession { ContainerId = containerId };
                var keyPath = _serverConfigService.GetSshKeyPath(server);
                session.StartSshDockerExec(
                    server.SshHost ?? throw new InvalidOperationException("SSH host not configured"),
                    server.SshPort,
                    server.SshUser ?? "root",
                    keyPath,
                    containerId);
                _sessions[session.SessionId] = session;
                _logger.LogInformation("Terminal session {SessionId} created (SSH docker exec, container: {ContainerId})",
                    session.SessionId, containerId);
                return session;
            }

            // TCP+mTLS server: a web terminal can't run over the mTLS Docker proxy — HAProxy proxies
            // request/response but not Docker's interactive attach/hijack stream (so the session
            // would connect but show nothing). Fail clearly instead of hanging.
            if (server != null && server.ConnectionType == ConnectionType.TCP)
                throw new InvalidOperationException(
                    "Web-Terminal über mTLS ist noch nicht verfügbar (der Docker-Proxy leitet keinen interaktiven Attach-Stream weiter). Nutze 'execute_command' für einzelne Befehle.");
        }

        var localSession = new TerminalSession { ContainerId = containerId };
        localSession.Start(_settings.DefaultShell, containerId);
        _sessions[localSession.SessionId] = localSession;

        _logger.LogInformation("Terminal session {SessionId} created (container: {ContainerId})",
            localSession.SessionId, containerId ?? "host");

        return localSession;
    }

    public TerminalSession CreateSshSession(ServerWatch.Models.ServerConfig server)
    {
        if (!_settings.Enabled)
            throw new InvalidOperationException("Terminal is disabled");

        if (_sessions.Count >= _settings.MaxSessions)
            throw new InvalidOperationException($"Maximum terminal sessions ({_settings.MaxSessions}) reached");

        var keyPath = _serverConfigService.GetSshKeyPath(server);

        var session = new TerminalSession();

        if (server.ConnectionType == ConnectionType.SSH)
        {
            session.StartSsh(
                server.SshHost ?? throw new InvalidOperationException("SSH host not configured"),
                server.SshPort,
                server.SshUser ?? "root",
                keyPath);
        }
        else if (server.ConnectionType == ConnectionType.Local)
        {
            session.Start(_settings.DefaultShell, null);
        }
        else if (server.ConnectionType == ConnectionType.TCP)
        {
            // See CreateSession: the mTLS Docker proxy doesn't carry an interactive attach stream.
            throw new InvalidOperationException(
                "Web-Terminal über mTLS ist noch nicht verfügbar (der Docker-Proxy leitet keinen interaktiven Attach-Stream weiter). Nutze 'execute_command' für einzelne Befehle.");
        }
        else
        {
            throw new InvalidOperationException($"Terminal not supported for connection type {server.ConnectionType}");
        }

        _sessions[session.SessionId] = session;
        _logger.LogInformation("SSH terminal session {SessionId} created for server {ServerName}",
            session.SessionId, server.Name);

        return session;
    }

    public TerminalSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public async Task DestroySession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync();
            _logger.LogInformation("Terminal session {SessionId} destroyed", sessionId);
        }
    }

    public IReadOnlyList<TerminalSession> GetActiveSessions()
    {
        return _sessions.Values.Where(s => s.IsRunning).ToList();
    }

    private void CleanupIdleSessions(object? state)
    {
        var timeout = TimeSpan.FromMinutes(_settings.IdleTimeoutMinutes);
        foreach (var (id, session) in _sessions)
        {
            if (DateTime.UtcNow - session.LastActivityAt > timeout || !session.IsRunning)
            {
                _ = DestroySession(id);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _sessions.Clear();
        GC.SuppressFinalize(this);
    }
}

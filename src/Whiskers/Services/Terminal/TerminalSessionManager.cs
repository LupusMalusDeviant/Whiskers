using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Mutations;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Terminal;

public class TerminalSessionManager : ITerminalSessionManager, IDisposable
{
    /// <summary>How often running sessions are checked (idle, ended, revoked, kill switch): well inside INV-09's 60 s.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    private const string MtlsUnavailable =
        "Web-Terminal über mTLS ist nicht verfügbar (der Docker-Proxy leitet keinen interaktiven Attach-Stream weiter). Aktiviere 'Tailscale SSH' für diesen Server oder nutze 'execute_command'.";

    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    // Serializes the session-cap check with the insert (see RegisterSession).
    private readonly object _capLock = new();
    // IOptionsMonitor (not IOptions) so UI-edited Terminal settings apply live (new sessions/cleanup
    // pick up CurrentValue; existing sessions keep their shell). See Settings.razor.
    private readonly IOptionsMonitor<TerminalSettings> _settings;
    private readonly IServerConfigService _serverConfigService;
    private readonly ITerminalLauncher _launcher;
    private readonly IAuditLogService _audit;
    private readonly ITerminalAuthority _authority;
    private readonly TimeProvider _time;
    private readonly ILogger<TerminalSessionManager> _logger;
    private readonly Timer _sweepTimer;
    // Contract 0015 (D2): a stored SSH key is sealed; the shell's ssh reads it from a lease that ends with the session.
    private readonly Whiskers.Services.Server.ISshKeyLeases? _keyLeases;

    public TerminalSessionManager(
        IOptionsMonitor<TerminalSettings> settings,
        IServerConfigService serverConfigService,
        ITerminalLauncher launcher,
        IAuditLogService audit,
        ILogger<TerminalSessionManager> logger,
        ITerminalAuthority authority,
        TimeProvider? time = null,
        Whiskers.Services.Server.ISshKeyLeases? keyLeases = null)
    {
        _keyLeases = keyLeases;
        _settings = settings;
        _serverConfigService = serverConfigService;
        _launcher = launcher;
        _audit = audit;
        // Required: without it a demoted owner or the kill switch would not end a running session (INV-09/21).
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _sweepTimer = new Timer(_ => _ = SweepAsync(), null, SweepInterval, SweepInterval);
    }

    public async Task<TerminalSession> OpenContainerShellAsync(TerminalOpenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = DecidedScope("terminal.container_open");
        if (string.IsNullOrWhiteSpace(request.ContainerId))
            throw new InvalidOperationException("A container shell needs a container.");
        var server = ServerOf(request);
        var containerId = request.ContainerId;
        TerminalLaunch launch;
        Whiskers.Services.Server.SshKeyLease? lease = null;
        if (server.ConnectionType == ConnectionType.Local)
            launch = TerminalLaunch.Local(_settings.CurrentValue.DefaultShell, containerId);
        else if (server.ConnectionType == ConnectionType.SSH)
        {
            var host = server.SshHost ?? throw new InvalidOperationException("SSH host not configured");
            lease = await LeaseAsync(server, ct);
            launch = TerminalLaunch.SshDockerExec(host, server.SshPort, server.SshUser ?? "root", lease?.Path, containerId);
        }
        // Tailscale SSH: keyless tailnet-identity shell (no standing SSH key). This is the path for the SSH-free
        // TCP/mTLS hosts — the Docker proxy can't carry an attach stream, but Tailscale SSH gives a real PTY.
        else if (server.TailscaleSsh)
            launch = TerminalLaunch.SshDockerExec(TailscaleHost(server), 22, "root", null, containerId);
        // TCP+mTLS server without Tailscale SSH: HAProxy proxies request/response but not Docker's interactive
        // attach stream (the session would connect but show nothing). Fail clearly instead of hanging.
        else if (server.ConnectionType == ConnectionType.TCP)
            throw new InvalidOperationException(MtlsUnavailable);
        else
            throw new InvalidOperationException($"Terminal not supported for connection type {server.ConnectionType}");
        return await StartWithLeaseAsync(request, scope, new TerminalTarget(server.Id, containerId, containerId), launch, lease);
    }

    public async Task<TerminalSession> OpenHostShellAsync(TerminalOpenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = DecidedScope("terminal.host_open");
        var server = ServerOf(request);
        TerminalLaunch launch;
        Whiskers.Services.Server.SshKeyLease? lease = null;
        // Keyless tailnet-identity shell first: the primary path for the SSH-free TCP/mTLS hosts (root, ACL-gated).
        if (server.TailscaleSsh)
            launch = TerminalLaunch.Ssh(TailscaleHost(server), 22, "root", null);
        else if (server.ConnectionType == ConnectionType.SSH)
        {
            var host = server.SshHost ?? throw new InvalidOperationException("SSH host not configured");
            lease = await LeaseAsync(server, ct);
            launch = TerminalLaunch.Ssh(host, server.SshPort, server.SshUser ?? "root", lease?.Path);
        }
        else if (server.ConnectionType == ConnectionType.Local)
            launch = TerminalLaunch.Local(_settings.CurrentValue.DefaultShell, null);
        else if (server.ConnectionType == ConnectionType.TCP)
            throw new InvalidOperationException(MtlsUnavailable);
        else
            throw new InvalidOperationException($"Terminal not supported for connection type {server.ConnectionType}");
        return await StartWithLeaseAsync(request, scope, new TerminalTarget(server.Id, null, server.Name), launch, lease);
    }

    /// <summary>The server's stored key as a lease (contract 0015), or null when it has none.</summary>
    private async Task<Whiskers.Services.Server.SshKeyLease?> LeaseAsync(Models.ServerConfig server, CancellationToken ct)
    {
        if (_serverConfigService.GetSshKeyPath(server) is null) return null;
        if (_keyLeases is null) throw new InvalidOperationException("SSH keys are sealed at rest and no key lease service is configured.");
        return await _keyLeases.AcquireAsync(server, ct);
    }

    /// <summary>Starts the session with the lease bound to it: the lease ends with the session, or now when it does not start.</summary>
    private async Task<TerminalSession> StartWithLeaseAsync(TerminalOpenRequest request, MutationScope scope, TerminalTarget target,
        TerminalLaunch launch, Whiskers.Services.Server.SshKeyLease? lease)
    {
        try
        {
            return Start(request, scope, target, launch, lease);
        }
        catch
        {
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
    }

    /// <summary>The gateway's decision this open runs in: its principal owns the session and its operation id links the
    /// open to the close. Outside one — a caller that bypassed the gateway — nothing opens (fail closed).</summary>
    private static MutationScope DecidedScope(string capabilityId)
    {
        var scope = MutationScope.Current;
        if (scope is null || scope.CapabilityId != capabilityId || scope.Principal is null)
            throw new InvalidOperationException("A terminal opens only through the gateway's decision for it.");
        return scope;
    }

    /// <summary>The configured server the session runs on. An id that names none is refused — never a local shell
    /// in its place (R1-17).</summary>
    private Models.ServerConfig ServerOf(TerminalOpenRequest request)
    {
        if (!_settings.CurrentValue.Enabled)
            throw new InvalidOperationException("Terminal is disabled");
        if (string.IsNullOrWhiteSpace(request.SessionId) || _sessions.ContainsKey(request.SessionId))
            throw new InvalidOperationException("A terminal session needs a new session id.");
        if (_sessions.Count >= _settings.CurrentValue.MaxSessions)
            throw new InvalidOperationException($"Maximum terminal sessions ({_settings.CurrentValue.MaxSessions}) reached");
        var server = string.IsNullOrWhiteSpace(request.ServerId)
            ? _serverConfigService.GetDefaultServer()
            : _serverConfigService.GetServer(request.ServerId);
        return server ?? throw new InvalidOperationException("The server of this terminal is not configured.");
    }

    private static string TailscaleHost(Models.ServerConfig server)
    {
        var host = !string.IsNullOrEmpty(server.TcpHost) ? server.TcpHost : server.SshHost;
        return string.IsNullOrEmpty(host)
            ? throw new InvalidOperationException("Tailscale SSH: kein Host (TcpHost/SshHost) konfiguriert.")
            : host;
    }

    private TerminalSession Start(TerminalOpenRequest request, MutationScope scope, TerminalTarget target,
        TerminalLaunch launch, IAsyncDisposable? keyLease = null)
    {
        // The shell opens on exactly what the gateway decided: the server, and the container or the host.
        if (!scope.Covers(target.ServerId, target.ContainerId ?? target.ServerId))
            throw new InvalidOperationException("This terminal is not the one the gateway decided on.");
        var owner = new TerminalOwner(scope.Principal!.Id, scope.Principal.DisplayName) { Principal = scope.Principal };
        var session = new TerminalSession(request.SessionId, owner, target, _launcher.Start(launch),
            _time.GetUtcNow().UtcDateTime, scope.OperationId, _time, keyLease);
        RegisterSession(session);
        _logger.LogInformation("Terminal session {SessionId} opened ({Kind} {Target} on {ServerId})",
            session.SessionId, target.Kind, target.Name, target.ServerId);
        return session;
    }

    // Atomically enforce MaxSessions: the cap check and the insert must be a single critical section,
    // otherwise two concurrent opens can both pass a Count check and overshoot the cap (TOCTOU). The early
    // Count check in ServerOf stays only as a cheap fast-fail.
    private void RegisterSession(TerminalSession session)
    {
        lock (_capLock)
        {
            if (_sessions.Count < _settings.CurrentValue.MaxSessions && _sessions.TryAdd(session.SessionId, session))
                return;
        }
        // Over cap (lost an open race) — tear down the just-started process and reject; it never was a session.
        _ = session.DisposeAsync();
        throw new InvalidOperationException($"Maximum terminal sessions ({_settings.CurrentValue.MaxSessions}) reached");
    }

    public TerminalSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public async Task CloseAsync(string sessionId, TerminalCloseReason reason = TerminalCloseReason.Closed)
    {
        // Removing it is what makes this the one close: a second call, the sweep or the page, finds nothing.
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        try
        {
            await session.DisposeAsync();
        }
        finally
        {
            await RecordCloseAsync(session, reason);
        }
    }

    /// <summary>The close event (INV-47): owner, target, duration and reason — never what was typed (PC-D2).</summary>
    private async Task RecordCloseAsync(TerminalSession session, TerminalCloseReason reason)
    {
        var duration = _time.GetUtcNow().UtcDateTime - session.CreatedAt;
        _logger.LogInformation("Terminal session {SessionId} closed ({Reason}) after {Duration}",
            session.SessionId, reason, duration);
        var details = JsonSerializer.Serialize(new
        {
            sessionId = session.SessionId,
            // The open's audit row carries the same operation id.
            operationId = session.OperationId,
            principal = session.Owner.Id,
            reason = reason.ToString(),
            openedAt = session.CreatedAt,
            durationMs = (long)Math.Max(0, duration.TotalMilliseconds),
        });
        var target = session.Target;
        // INV-01 (contract 0014): the owner's principal, not only its display name.
        var principal = session.Owner.Principal
                        ?? new Whiskers.Services.Mutations.MutationPrincipal(
                            Whiskers.Services.Mutations.PrincipalIds.KindOf(session.Owner.Id) ?? Whiskers.Services.Mutations.MutationPrincipalKind.Human,
                            session.Owner.Id, session.Owner.DisplayName, Whiskers.Models.McpPermissionLevels.None);
        await _audit.TryLogAsync(principal, "terminal.close", target.Kind,
            target.ContainerId ?? target.ServerId, target.Name, details, target.ServerId);
    }

    public IReadOnlyList<TerminalSession> GetActiveSessions() => _sessions.Values.Where(s => s.IsRunning).ToList();

    /// <summary>Ends what must end: a shell that exited, an idle one, every one while terminals are switched off,
    /// and one whose owner lost the right to it or that the kill switch stops (<see cref="ITerminalAuthority"/>).</summary>
    public async Task SweepAsync()
    {
        var settings = _settings.CurrentValue;
        var timeout = TimeSpan.FromMinutes(settings.IdleTimeoutMinutes);

        foreach (var session in _sessions.Values)
        {
            TerminalCloseReason? reason;
            try
            {
                reason = !session.IsRunning ? TerminalCloseReason.Exited
                    : !settings.Enabled ? TerminalCloseReason.Disabled
                    : _authority.MustEnd(session)
                      ?? (_time.GetUtcNow().UtcDateTime - session.LastActivityAt > timeout ? TerminalCloseReason.Idle : null);
            }
            catch (Exception ex)
            {
                // Whether the owner may go on cannot be told: the session ends (fail closed).
                _logger.LogWarning(ex, "Terminal session {SessionId}: authority check failed", session.SessionId);
                reason = TerminalCloseReason.Revoked;
            }
            if (reason is { } why)
            {
                try { await CloseAsync(session.SessionId, why); }
                catch (Exception ex) { _logger.LogWarning(ex, "Terminal session {SessionId}: close failed", session.SessionId); }
            }
        }
    }

    public void Dispose()
    {
        _sweepTimer.Dispose();
        foreach (var id in _sessions.Keys)
        {
            try { CloseAsync(id, TerminalCloseReason.Shutdown).GetAwaiter().GetResult(); }
            catch { /* shutting down */ }
        }
        GC.SuppressFinalize(this);
    }
}

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ServerWatch.Services.Terminal;

namespace ServerWatch.Hubs;

[Authorize]
public class TerminalHub : Hub
{
    private readonly ITerminalSessionManager _sessions;
    private readonly ILogger<TerminalHub> _logger;

    private static readonly ConcurrentDictionary<string, string> _connectionSessions = new();

    public TerminalHub(ITerminalSessionManager sessions, ILogger<TerminalHub> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public async Task CreateSession(string? containerId = null)
    {
        try
        {
            var session = _sessions.CreateSession(containerId);
            _connectionSessions[Context.ConnectionId] = session.SessionId;

            _ = Task.Run(async () =>
            {
                try
                {
                    var buffer = new char[4096];
                    var stdout = session.Process!.StandardOutput;
                    while (session.IsRunning)
                    {
                        var read = await stdout.ReadAsync(buffer);
                        if (read > 0)
                        {
                            await Clients.Caller.SendAsync("TerminalOutput",
                                session.SessionId, new string(buffer, 0, read));
                        }
                        else
                        {
                            await Task.Delay(10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Terminal stdout reader ended for {SessionId}", session.SessionId);
                }
                await Clients.Caller.SendAsync("SessionEnded", session.SessionId);
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var buffer = new char[4096];
                    var stderr = session.Process!.StandardError;
                    while (session.IsRunning)
                    {
                        var read = await stderr.ReadAsync(buffer);
                        if (read > 0)
                        {
                            await Clients.Caller.SendAsync("TerminalOutput",
                                session.SessionId, new string(buffer, 0, read));
                        }
                        else
                        {
                            await Task.Delay(10);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Terminal stderr reader ended for {SessionId}", session.SessionId);
                }
            });

            await Clients.Caller.SendAsync("SessionCreated", session.SessionId);
        }
        catch (Exception ex)
        {
            await Clients.Caller.SendAsync("SessionError", ex.Message);
        }
    }

    public async Task SendInput(string sessionId, string data)
    {
        var session = _sessions.GetSession(sessionId);
        if (session != null)
        {
            await session.WriteAsync(data);
        }
    }

    public async Task DestroySession(string sessionId)
    {
        await _sessions.DestroySession(sessionId);
        _connectionSessions.TryRemove(Context.ConnectionId, out _);
        await Clients.Caller.SendAsync("SessionDestroyed", sessionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connectionSessions.TryGetValue(Context.ConnectionId, out var sessionId))
        {
            await _sessions.DestroySession(sessionId);
            _connectionSessions.TryRemove(Context.ConnectionId, out _);
        }
        await base.OnDisconnectedAsync(exception);
    }
}

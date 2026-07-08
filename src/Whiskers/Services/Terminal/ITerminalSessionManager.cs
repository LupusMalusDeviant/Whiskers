namespace Whiskers.Services.Terminal;

public interface ITerminalSessionManager
{
    TerminalSession CreateSession(string? containerId = null, string? serverId = null);
    TerminalSession CreateSshSession(Whiskers.Models.ServerConfig server);
    TerminalSession? GetSession(string sessionId);
    Task DestroySession(string sessionId);
    IReadOnlyList<TerminalSession> GetActiveSessions();
}

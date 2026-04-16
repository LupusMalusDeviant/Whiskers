namespace ServerWatch.Services.Terminal;

public interface ITerminalSessionManager
{
    TerminalSession CreateSession(string? containerId = null, string? serverId = null);
    TerminalSession CreateSshSession(ServerWatch.Models.ServerConfig server);
    TerminalSession? GetSession(string sessionId);
    Task DestroySession(string sessionId);
    IReadOnlyList<TerminalSession> GetActiveSessions();
}

using Whiskers.Services.Mutations;

namespace Whiskers.Services.Terminal;

/// <summary>A session to open: the id the gateway's open event carries, who opens it, and where.</summary>
/// <param name="ServerId">The server; empty means the default server, as the gateway resolves it. One that is not
/// configured never falls back to a local shell.</param>
public sealed record TerminalOpenRequest(string SessionId, TerminalOwner Owner, string? ServerId, string? ContainerId = null);

/// <summary>
/// The web terminal's sessions (R1-17, PC-D2, INV-47). A session is opened only through the Mutation Gateway — the
/// pages call the open methods inside <c>IUiMutationRunner.RunAsync</c>, which decides Admin, the kill switch and
/// protected resources and audits the open — and ends exactly once with a close event (owner, target, duration,
/// reason). A session whose owner lost Admin, or every session once the kill switch is on, ends within a minute.
/// </summary>
public interface ITerminalSessionManager
{
    /// <summary>A shell inside a container. Throws for an unknown server, a disabled terminal or the session cap.</summary>
    [MutatingOperation("terminal.container_open")]
    Task<TerminalSession> OpenContainerShellAsync(TerminalOpenRequest request, CancellationToken ct = default);

    /// <summary>A shell on a server's host. Throws for an unknown server, a disabled terminal or the session cap.</summary>
    [MutatingOperation("terminal.host_open")]
    Task<TerminalSession> OpenHostShellAsync(TerminalOpenRequest request, CancellationToken ct = default);

    TerminalSession? GetSession(string sessionId);

    /// <summary>Ends the session and records its close event; a session already ended is left alone.</summary>
    Task CloseAsync(string sessionId, TerminalCloseReason reason = TerminalCloseReason.Closed);

    IReadOnlyList<TerminalSession> GetActiveSessions();
}

/// <summary>Whether a session may go on: <c>null</c>, or why it must end now (R1-17, INV-09/21).</summary>
public interface ITerminalAuthority
{
    TerminalCloseReason? MustEnd(TerminalSession session);
}

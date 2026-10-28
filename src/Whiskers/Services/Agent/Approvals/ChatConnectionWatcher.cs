using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Whiskers.Services.Agent.Approvals;

/// <summary>Tells the chats of one browser connection (Blazor circuit) that the connection is gone (R1-15, INV-36).
/// A circuit outlives a lost connection for a while before its components are disposed; a chat must not keep its
/// session — and its pending approvals — alive for that time.</summary>
public interface IChatConnectionWatcher
{
    /// <summary>Raised when the browser connection of this circuit drops.</summary>
    event Func<Task>? ConnectionDown;
}

/// <summary>The <see cref="CircuitHandler"/> behind <see cref="IChatConnectionWatcher"/>: one per circuit.</summary>
public sealed class ChatConnectionWatcher : CircuitHandler, IChatConnectionWatcher
{
    private readonly ILogger<ChatConnectionWatcher>? _logger;

    public ChatConnectionWatcher(ILogger<ChatConnectionWatcher>? logger = null) => _logger = logger;

    public event Func<Task>? ConnectionDown;

    public override async Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (ConnectionDown is not { } handlers) return;
        // Every chat is told before any is awaited: each stops its run synchronously, before its first await, so a
        // slow cleanup of one chat (cancelling its approvals in the database) never leaves another one running.
        var endings = new List<Task>();
        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
        {
            try { endings.Add(handler()); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Ending a chat after its connection dropped failed"); }
        }
        foreach (var ending in endings)
        {
            try { await ending; }
            catch (Exception ex) { _logger?.LogWarning(ex, "Ending a chat after its connection dropped failed"); }
        }
    }
}

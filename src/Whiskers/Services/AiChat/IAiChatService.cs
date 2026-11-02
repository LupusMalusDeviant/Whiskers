namespace Whiskers.Services.AiChat;

/// <summary>Read-only Whiskers advisor chat (guidance only; no actions).</summary>
public interface IAiChatService
{
    bool IsEnabled { get; }
    /// <summary>One advisor answer, bounded by <see cref="AiChatService.RequestTimeout"/> (R1-38); <paramref name="ct"/>
    /// ends it early when the chat is closed.</summary>
    Task<string> ChatAsync(string userMessage, List<ChatMessage>? history = null, CancellationToken ct = default);
}

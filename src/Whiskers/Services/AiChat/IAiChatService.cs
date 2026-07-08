namespace Whiskers.Services.AiChat;

/// <summary>Read-only Whiskers advisor chat (guidance only; no actions).</summary>
public interface IAiChatService
{
    bool IsEnabled { get; }
    Task<string> ChatAsync(string userMessage, List<ChatMessage>? history = null);
}

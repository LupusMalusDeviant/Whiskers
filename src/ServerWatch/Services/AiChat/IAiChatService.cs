namespace ServerWatch.Services.AiChat;

/// <summary>Read-only ServerWatch advisor chat (guidance only; no actions).</summary>
public interface IAiChatService
{
    bool IsEnabled { get; }
    Task<string> ChatAsync(string userMessage, List<ChatMessage>? history = null);
}

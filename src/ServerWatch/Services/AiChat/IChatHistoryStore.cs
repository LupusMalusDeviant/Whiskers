namespace ServerWatch.Services.AiChat;

/// <summary>Per-user persistence of the advisor chat history.</summary>
public interface IChatHistoryStore
{
    Task<List<ChatMessage>> LoadAsync(string userEmail);
    Task SaveAsync(string userEmail, List<ChatMessage> messages);
    Task ClearAsync(string userEmail);
}

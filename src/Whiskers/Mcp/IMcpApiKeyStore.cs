namespace Whiskers.Mcp;

/// <summary>Legacy flat MCP API-key store (kept for backwards compatibility).</summary>
public interface IMcpApiKeyStore
{
    Task InitializeAsync();
    bool ValidateKey(string key);
    IReadOnlySet<string> GetKeys();
    Task AddKeyAsync(string key);
    Task RemoveKeyAsync(string key);
}

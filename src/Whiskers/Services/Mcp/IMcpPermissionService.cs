using Whiskers.Models;

namespace Whiskers.Services.Mcp;

/// <summary>Validates MCP API keys and enforces per-tool permission levels.</summary>
public interface IMcpPermissionService
{
    Task InitializeAsync(CancellationToken ct = default);
    McpApiKeyConfig? ValidateKey(string key);
    bool IsToolAllowed(string key, string toolName);
    HashSet<string> GetAllowedTools(string key);
    McpPermissionData GetPermissionData();
    Task SavePermissionDataAsync(McpPermissionData data);
    /// <summary>Creates a key. The returned instance is the only place its plaintext <c>Key</c> ever appears —
    /// only the SHA-256 hash is stored, so the caller must show it to the user now.</summary>
    Task<McpApiKeyConfig> AddApiKeyAsync(string name, string permissionLevel);
    Task RemoveApiKeyAsync(string id);
    Task SetToolEnabledAsync(string toolName, bool enabled);
}

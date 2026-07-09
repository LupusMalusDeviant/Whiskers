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
    Task<McpApiKeyConfig> AddApiKeyAsync(string name, string permissionLevel);
    Task RemoveApiKeyAsync(string id);
    Task SetToolEnabledAsync(string toolName, bool enabled);
}

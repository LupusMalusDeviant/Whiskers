using System.Security.Cryptography;
using System.Text;
using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Mcp;

public class McpPermissionService : IMcpPermissionService
{
    private readonly JsonFileStore<McpPermissionData> _store;
    private readonly ILogger<McpPermissionService> _logger;
    private McpPermissionData _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public McpPermissionService(ILogger<McpPermissionService> logger)
    {
        _logger = logger;
        _store = new JsonFileStore<McpPermissionData>("/app/data/mcp-permissions.json");
    }

    public async Task InitializeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Build a fresh data object, then publish it with a single atomic assignment so the
            // lock-free readers never observe a half-seeded state.
            var data = await _store.LoadAsync();

            // Seed default tool configs for any missing tools
            var changed = false;
            foreach (var (tool, level) in McpPermissionLevels.DefaultToolLevels)
            {
                if (!data.Tools.ContainsKey(tool))
                {
                    data.Tools[tool] = new McpToolConfig
                    {
                        Enabled = true,
                        RequiredLevel = level,
                        Category = McpPermissionLevels.ToolCategories.GetValueOrDefault(tool, "Other")
                    };
                    changed = true;
                }
            }

            // Generate default API key if none exist
            if (data.ApiKeys.Count == 0)
            {
                var defaultKey = new McpApiKeyConfig
                {
                    Name = "Standard",
                    Key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                    PermissionLevel = McpPermissionLevels.Read,
                    Enabled = true
                };
                data.ApiKeys.Add(defaultKey);
                changed = true;
                _logger.LogInformation("Generated default MCP API key: {Key} (Level: {Level})", defaultKey.Key, defaultKey.PermissionLevel);
            }

            if (changed)
                await _store.SaveAsync(data);

            _data = data;

            _logger.LogInformation("MCP permissions loaded: {KeyCount} keys, {ToolCount} tools",
                _data.ApiKeys.Count, _data.Tools.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Constant-time comparison of a stored key against a provided key. Keys are kept as-is
    /// (plaintext); this only removes the early-exit timing side channel of an ordinal `==`.
    /// </summary>
    private static bool KeysMatch(string storedKey, string providedKey)
    {
        if (string.IsNullOrEmpty(storedKey) || string.IsNullOrEmpty(providedKey))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(storedKey),
            Encoding.UTF8.GetBytes(providedKey));
    }

    /// <summary>
    /// Validates an API key and returns the config if valid, null otherwise.
    /// </summary>
    public McpApiKeyConfig? ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        // Single reference read = consistent immutable snapshot (writers swap _data atomically).
        return _data.ApiKeys.FirstOrDefault(k => k.Enabled && KeysMatch(k.Key, key));
    }

    /// <summary>
    /// Checks if a specific tool is allowed for the given API key.
    /// </summary>
    public bool IsToolAllowed(string key, string toolName)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        var data = _data;
        var keyConfig = ValidateKey(key);
        if (keyConfig == null)
        {
            _logger.LogWarning("IsToolAllowed: key not found or disabled. Provided key starts with: {KeyPrefix}, total keys in store: {Count}",
                key.Length > 8 ? key[..8] : key, data.ApiKeys.Count);
            return false;
        }

        // Check if tool exists and is enabled
        if (!data.Tools.TryGetValue(toolName, out var toolConfig))
        {
            _logger.LogWarning("IsToolAllowed: tool '{Tool}' not found in {Count} configured tools", toolName, data.Tools.Count);
            return false;
        }

        if (!toolConfig.Enabled)
        {
            _logger.LogWarning("IsToolAllowed: tool '{Tool}' is disabled", toolName);
            return false;
        }

        // Check custom tool list if specified
        if (keyConfig.AllowedTools != null)
            return keyConfig.AllowedTools.Contains(toolName);

        // Otherwise check permission level
        var allowed = McpPermissionLevels.HasAccess(keyConfig.PermissionLevel, toolConfig.RequiredLevel);
        if (!allowed)
            _logger.LogWarning("IsToolAllowed: level '{KeyLevel}' insufficient for tool '{Tool}' (requires '{Required}')",
                keyConfig.PermissionLevel, toolName, toolConfig.RequiredLevel);
        return allowed;
    }

    /// <summary>
    /// Returns all tools that are enabled and accessible for the given key.
    /// </summary>
    public HashSet<string> GetAllowedTools(string key)
    {
        var keyConfig = ValidateKey(key);
        if (keyConfig == null) return new HashSet<string>();

        var data = _data;
        var allowed = new HashSet<string>();
        foreach (var (toolName, toolConfig) in data.Tools)
        {
            if (!toolConfig.Enabled) continue;

            if (keyConfig.AllowedTools != null)
            {
                if (keyConfig.AllowedTools.Contains(toolName))
                    allowed.Add(toolName);
            }
            else if (McpPermissionLevels.HasAccess(keyConfig.PermissionLevel, toolConfig.RequiredLevel))
            {
                allowed.Add(toolName);
            }
        }
        return allowed;
    }

    /// <summary>
    /// Returns a copy of the full permission data for the UI. Never hands out the live reference,
    /// so callers can't mutate the published snapshot out from under the lock-free readers.
    /// </summary>
    public McpPermissionData GetPermissionData()
    {
        var data = _data;
        return new McpPermissionData
        {
            ApiKeys = data.ApiKeys.Select(CloneKey).ToList(),
            Tools = data.Tools.ToDictionary(kv => kv.Key, kv => CloneTool(kv.Value))
        };
    }

    private static McpApiKeyConfig CloneKey(McpApiKeyConfig k) => new()
    {
        Id = k.Id,
        Name = k.Name,
        Key = k.Key,
        PermissionLevel = k.PermissionLevel,
        Enabled = k.Enabled,
        CreatedAt = k.CreatedAt,
        AllowedTools = k.AllowedTools == null ? null : new List<string>(k.AllowedTools)
    };

    private static McpToolConfig CloneTool(McpToolConfig t) => new()
    {
        Enabled = t.Enabled,
        RequiredLevel = t.RequiredLevel,
        Category = t.Category
    };

    /// <summary>
    /// Updates the full permission data from the UI.
    /// </summary>
    public async Task SavePermissionDataAsync(McpPermissionData data)
    {
        await _lock.WaitAsync();
        try
        {
            _data = data;
            await _store.SaveAsync(_data);
            _logger.LogInformation("MCP permissions saved: {KeyCount} keys, {ToolCount} tools",
                _data.ApiKeys.Count, _data.Tools.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Adds a new API key.
    /// </summary>
    public async Task<McpApiKeyConfig> AddApiKeyAsync(string name, string permissionLevel)
    {
        var keyConfig = new McpApiKeyConfig
        {
            Name = name,
            Key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            PermissionLevel = permissionLevel,
            Enabled = true
        };

        await _lock.WaitAsync();
        try
        {
            // Copy-on-write: build a new data object and publish it atomically.
            var newData = new McpPermissionData
            {
                ApiKeys = new List<McpApiKeyConfig>(_data.ApiKeys) { keyConfig },
                Tools = _data.Tools
            };
            await _store.SaveAsync(newData);
            _data = newData;
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("New MCP API key created: {Name} (Level: {Level})", name, permissionLevel);
        return keyConfig;
    }

    /// <summary>
    /// Removes an API key by ID.
    /// </summary>
    public async Task RemoveApiKeyAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            // Copy-on-write: build a new list without the removed key and publish atomically.
            var newData = new McpPermissionData
            {
                ApiKeys = _data.ApiKeys.Where(k => k.Id != id).ToList(),
                Tools = _data.Tools
            };
            await _store.SaveAsync(newData);
            _data = newData;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Toggles a tool's enabled state.
    /// </summary>
    public async Task SetToolEnabledAsync(string toolName, bool enabled)
    {
        await _lock.WaitAsync();
        try
        {
            if (_data.Tools.TryGetValue(toolName, out var existing))
            {
                // Copy-on-write: clone the Tools dictionary and the one changed entry, then publish.
                var tools = new Dictionary<string, McpToolConfig>(_data.Tools)
                {
                    [toolName] = new McpToolConfig
                    {
                        Enabled = enabled,
                        RequiredLevel = existing.RequiredLevel,
                        Category = existing.Category
                    }
                };
                var newData = new McpPermissionData
                {
                    ApiKeys = _data.ApiKeys,
                    Tools = tools
                };
                await _store.SaveAsync(newData);
                _data = newData;
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

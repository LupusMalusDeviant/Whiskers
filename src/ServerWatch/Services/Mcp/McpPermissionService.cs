using ServerWatch.Models;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Mcp;

public class McpPermissionService
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
            _data = await _store.LoadAsync();

            // Seed default tool configs for any missing tools
            var changed = false;
            foreach (var (tool, level) in McpPermissionLevels.DefaultToolLevels)
            {
                if (!_data.Tools.ContainsKey(tool))
                {
                    _data.Tools[tool] = new McpToolConfig
                    {
                        Enabled = true,
                        RequiredLevel = level,
                        Category = McpPermissionLevels.ToolCategories.GetValueOrDefault(tool, "Other")
                    };
                    changed = true;
                }
            }

            // Generate default API key if none exist
            if (_data.ApiKeys.Count == 0)
            {
                var defaultKey = new McpApiKeyConfig
                {
                    Name = "Standard",
                    Key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                    PermissionLevel = McpPermissionLevels.Read,
                    Enabled = true
                };
                _data.ApiKeys.Add(defaultKey);
                changed = true;
                _logger.LogInformation("Generated default MCP API key: {Key} (Level: {Level})", defaultKey.Key, defaultKey.PermissionLevel);
            }

            if (changed)
                await _store.SaveAsync(_data);

            _logger.LogInformation("MCP permissions loaded: {KeyCount} keys, {ToolCount} tools",
                _data.ApiKeys.Count, _data.Tools.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Validates an API key and returns the config if valid, null otherwise.
    /// </summary>
    public McpApiKeyConfig? ValidateKey(string key)
    {
        return _data.ApiKeys.FirstOrDefault(k => k.Key == key && k.Enabled);
    }

    /// <summary>
    /// Checks if a specific tool is allowed for the given API key.
    /// </summary>
    public bool IsToolAllowed(string key, string toolName)
    {
        var keyConfig = ValidateKey(key);
        if (keyConfig == null)
        {
            _logger.LogWarning("IsToolAllowed: key not found or disabled. Provided key starts with: {KeyPrefix}, total keys in store: {Count}",
                key.Length > 8 ? key[..8] : key, _data.ApiKeys.Count);
            return false;
        }

        // Check if tool exists and is enabled
        if (!_data.Tools.TryGetValue(toolName, out var toolConfig))
        {
            _logger.LogWarning("IsToolAllowed: tool '{Tool}' not found in {Count} configured tools", toolName, _data.Tools.Count);
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

        var allowed = new HashSet<string>();
        foreach (var (toolName, toolConfig) in _data.Tools)
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
    /// Returns the full permission data for the UI.
    /// </summary>
    public McpPermissionData GetPermissionData() => _data;

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
            _data.ApiKeys.Add(keyConfig);
            await _store.SaveAsync(_data);
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
            _data.ApiKeys.RemoveAll(k => k.Id == id);
            await _store.SaveAsync(_data);
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
            if (_data.Tools.TryGetValue(toolName, out var tool))
            {
                tool.Enabled = enabled;
                await _store.SaveAsync(_data);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

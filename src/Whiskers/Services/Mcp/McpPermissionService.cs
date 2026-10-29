using System.Security.Cryptography;
using System.Text;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Mcp;

public class McpPermissionService : IMcpPermissionService, IInitializable
{
    private readonly JsonFileStore<McpPermissionData> _store;
    private readonly DataPathOptions _dataPaths;
    private readonly ILogger<McpPermissionService> _logger;
    private McpPermissionData _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public McpPermissionService(ILogger<McpPermissionService> logger, DataPathOptions? dataPaths = null)
    {
        _logger = logger;
        _dataPaths = dataPaths ?? DataPathOptions.Default;
        _store = new JsonFileStore<McpPermissionData>(_dataPaths.McpPermissionsJson);
    }

    public int Order => 70;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        PurgeLegacyKeyFiles();

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

            // No default key is generated: only the hash is stored, so a key nobody saw at creation could never
            // be shown again. Keys are created in the UI (Settings → MCP) and displayed once to their creator.
            changed |= MigratePlaintextKeys(data);

            changed |= EnforceCodeFloor(data);

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
    /// Deletes the files of the removed ServerWatch-era flat key store (<c>api-keys.json</c> and its
    /// <c>initial-mcp-key.txt</c>). Their keys were unscoped plaintext secrets that no code path accepts
    /// any more; leaving them in the data volume (and in every self-backup) would only keep them exposed.
    /// They are deliberately not migrated: importing an unscoped key would mean inventing a scope for it.
    /// </summary>
    private void PurgeLegacyKeyFiles()
    {
        foreach (var path in new[] { _dataPaths.LegacyApiKeysJson, _dataPaths.LegacyInitialMcpKeyFile })
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                _logger.LogWarning(
                    "Deleted legacy MCP key file {Path}: the flat key store was removed and its keys are no " +
                    "longer accepted. Create scoped keys under Settings → MCP.", path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not delete legacy MCP key file {Path}; remove it manually", path);
            }
        }
    }

    /// <summary>Lower-case hex SHA-256 of a key — the only form a key is persisted in.</summary>
    internal static string HashKey(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string HintOf(string key) => key.Length >= 4 ? key[^4..] : "";

    /// <summary>Files written before key hashing hold the plaintext <c>Key</c>. Replace it with its hash in place
    /// (the key keeps working, its clients need no change) and drop the plaintext. Returns whether anything
    /// was migrated, so the caller rewrites the file and the plaintext leaves the disk.</summary>
    internal bool MigratePlaintextKeys(McpPermissionData data)
    {
        var migrated = 0;
        foreach (var k in data.ApiKeys)
        {
            if (string.IsNullOrEmpty(k.Key)) continue;
            if (string.IsNullOrEmpty(k.KeyHash)) k.KeyHash = HashKey(k.Key);
            if (string.IsNullOrEmpty(k.KeyHint)) k.KeyHint = HintOf(k.Key);
            k.Key = null;
            migrated++;
        }
        if (migrated > 0)
            _logger.LogWarning("Migrated {Count} MCP API key(s) from plaintext to SHA-256 hashes", migrated);
        return migrated > 0;
    }

    /// <summary>Constant-time comparison of two hex hashes (the early exit of <c>==</c> would leak a prefix match).</summary>
    private static bool HashesMatch(string storedHash, string providedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(providedHash));
    }

    /// <summary>
    /// Validates an API key and returns the config if valid, null otherwise.
    /// </summary>
    public McpApiKeyConfig? ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        var hash = HashKey(key);
        // Single reference read = consistent immutable snapshot (writers swap _data atomically).
        return _data.ApiKeys.FirstOrDefault(k => k.Enabled && HashesMatch(k.KeyHash, hash));
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
            // Never log any part of the presented token — not even a prefix.
            _logger.LogWarning("IsToolAllowed: key not found or disabled ({Count} keys in store)", data.ApiKeys.Count);
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
        KeyHash = k.KeyHash,
        KeyHint = k.KeyHint,
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

    /// <summary>A stored level may make a tool stricter than the code declares, never looser. Without this floor a
    /// level persisted in <c>mcp-permissions.json</c> outlived every tightening in code: raising
    /// <c>hetzner_enable_rescue</c> to admin changed nothing on an existing install, because the old "write" was
    /// already on disk. Returns whether anything was raised.</summary>
    internal bool EnforceCodeFloor(McpPermissionData data)
    {
        var raised = false;
        foreach (var (tool, config) in data.Tools)
        {
            if (!McpPermissionLevels.DefaultToolLevels.TryGetValue(tool, out var floor)) continue;
            if (McpPermissionLevels.GetRank(config.RequiredLevel) >= McpPermissionLevels.GetRank(floor)) continue;

            _logger.LogWarning("MCP tool {Tool}: stored level {Stored} is looser than the code's {Floor} — raised to {Floor}",
                tool, config.RequiredLevel, floor, floor);
            config.RequiredLevel = floor;
            raised = true;
        }
        return raised;
    }

    /// <summary>
    /// Updates the full permission data from the UI. Levels below the code's floor are raised (see
    /// <see cref="EnforceCodeFloor"/>).
    /// </summary>
    public async Task SavePermissionDataAsync(McpPermissionData data)
    {
        await _lock.WaitAsync();
        try
        {
            // The UI edits name, level, tool list and the active flag — never the key material. Take the hash
            // from the stored key with the same id, and drop entries the store does not know (keys are only
            // ever created by AddApiKeyAsync), so a crafted save cannot plant a key of its own.
            // Works on a copy, published only once it is written (R1-11): a failed write leaves the live
            // permissions as they were, and the caller's objects (the Settings page's edit buffer) never alias them.
            var stored = _data.ApiKeys.ToDictionary(k => k.Id);
            var next = new McpPermissionData
            {
                ApiKeys = data.ApiKeys
                    .Where(k => stored.ContainsKey(k.Id))
                    .Select(k =>
                    {
                        var copy = CloneKey(k);
                        copy.KeyHash = stored[k.Id].KeyHash;
                        copy.KeyHint = stored[k.Id].KeyHint;
                        copy.Key = null;
                        return copy;
                    })
                    .ToList(),
                Tools = data.Tools.ToDictionary(kv => kv.Key, kv => CloneTool(kv.Value)),
            };
            EnforceCodeFloor(next);
            var before = _data;
            await _store.SaveAsync(next);
            _data = next;
            Whiskers.Services.Mutations.ConfigChange.Record("mcp-permissions", before, next);   // R1-22
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
        // 256 bits from the CSPRNG, 64 hex characters — the same shape as the GUID-based keys before.
        var plaintext = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var keyConfig = new McpApiKeyConfig
        {
            Name = name,
            KeyHash = HashKey(plaintext),
            KeyHint = HintOf(plaintext),
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
            var before = _data;
            await _store.SaveAsync(newData);
            _data = newData;
            Whiskers.Services.Mutations.ConfigChange.Record("mcp-permissions", before, newData);   // R1-22
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation("New MCP API key created: {Name} (Level: {Level})", name, permissionLevel);
        // The caller gets the plaintext exactly once; the stored instance never had it.
        var shown = CloneKey(keyConfig);
        shown.Key = plaintext;
        return shown;
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
            var before = _data;
            await _store.SaveAsync(newData);
            _data = newData;
            Whiskers.Services.Mutations.ConfigChange.Record("mcp-permissions", before, newData);   // R1-22
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
                var before = _data;
                await _store.SaveAsync(newData);
                _data = newData;
                Whiskers.Services.Mutations.ConfigChange.Record("mcp-permissions", before, newData);   // R1-22
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServerWatch.Mcp;

public class McpApiKeyStore : IMcpApiKeyStore
{
    private readonly string _filePath;
    private HashSet<string> _keys = new();
    private readonly ILogger<McpApiKeyStore> _logger;

    public McpApiKeyStore(ILogger<McpApiKeyStore> logger)
    {
        _filePath = "/app/data/api-keys.json";
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (File.Exists(_filePath))
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var data = JsonSerializer.Deserialize<ApiKeyData>(json);
            _keys = new HashSet<string>(data?.Keys ?? []);
            _logger.LogInformation("Loaded {Count} MCP API keys", _keys.Count);
        }
        else
        {
            // Generate a default key on first run
            var defaultKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            _keys = new HashSet<string> { defaultKey };
            await SaveAsync();
            _logger.LogInformation("Generated default MCP API key: {Key}", defaultKey);
        }
    }

    public bool ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;
        // Constant-time scan so this legacy fallback doesn't leak key length/content via timing.
        var provided = Encoding.UTF8.GetBytes(key);
        var match = false;
        foreach (var stored in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), provided))
                match = true;
        }
        return match;
    }

    public IReadOnlySet<string> GetKeys() => _keys;

    public async Task AddKeyAsync(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("API key cannot be empty.", nameof(key));

        _keys.Add(key);
        await SaveAsync();
    }

    public async Task RemoveKeyAsync(string key)
    {
        _keys.Remove(key);
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        var data = new ApiKeyData { Keys = _keys.ToList() };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await File.WriteAllTextAsync(_filePath, json);
    }

    private class ApiKeyData
    {
        public List<string> Keys { get; set; } = new();
    }
}

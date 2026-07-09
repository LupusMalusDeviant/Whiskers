using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Whiskers.Mcp;

public class McpApiKeyStore : IMcpApiKeyStore
{
    private readonly string _filePath;
    private HashSet<string> _keys = new();
    private readonly ILogger<McpApiKeyStore> _logger;

    public McpApiKeyStore(ILogger<McpApiKeyStore> logger, string? filePath = null)
    {
        _filePath = filePath ?? "/app/data/api-keys.json";
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
            // Generate a default admin-capable key on first run.
            var defaultKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            _keys = new HashSet<string> { defaultKey };
            await SaveAsync();

            // SECURITY: never print the key to the log — logs land in `docker logs` and aggregators
            // (this used to LogInformation the key verbatim). Write it to a 0600 file next to
            // api-keys.json and log only WHERE it is. The setup wizard (outOfTheBox W1) will surface it
            // once and delete the file; until then the operator reads it from disk and removes it.
            var keyFile = Path.Combine(Path.GetDirectoryName(_filePath)!, "initial-mcp-key.txt");
            // Create + lock down the file BEFORE writing the secret, so the key never exists in a
            // world-readable file. SetUnixFileMode has no effect target on Windows, hence the guard.
            await File.WriteAllTextAsync(keyFile, string.Empty);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await File.WriteAllTextAsync(keyFile, defaultKey);

            _logger.LogWarning(
                "Generated an initial MCP admin API key and wrote it to {Path} (mode 0600). " +
                "Retrieve it, then delete that file. The key is NOT written to the log.", keyFile);
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

using Microsoft.Extensions.Logging;
using Whiskers.Mcp;

namespace Whiskers.Tests;

// C6: the first-run default MCP admin key must never be written to the log (it used to be printed
// verbatim). It goes to a 0600 file next to api-keys.json and only the file PATH is logged.
public sealed class McpApiKeyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mcpkey-{Guid.NewGuid():N}");

    private string ApiKeysPath => Path.Combine(_dir, "api-keys.json");
    private string KeyFilePath => Path.Combine(_dir, "initial-mcp-key.txt");

    [Fact]
    public async Task First_run_writes_key_to_a_file_and_never_logs_the_key()
    {
        Directory.CreateDirectory(_dir);
        var logger = new ListLogger<McpApiKeyStore>();
        var store = new McpApiKeyStore(logger, ApiKeysPath);

        await store.InitializeAsync();

        // The key is written to its own file and is a real, active key.
        Assert.True(File.Exists(KeyFilePath), "initial-mcp-key.txt must be written on first run");
        var key = (await File.ReadAllTextAsync(KeyFilePath)).Trim();
        Assert.NotEmpty(key);
        Assert.True(store.ValidateKey(key), "the file must hold a valid key");

        // The key value must NOT appear in any log message...
        Assert.DoesNotContain(logger.Messages, m => m.Contains(key));
        // ...but the operator is told where to find it.
        Assert.Contains(logger.Messages, m => m.Contains("initial-mcp-key.txt"));
    }

    [Fact]
    public async Task Second_run_loads_existing_keys_and_writes_no_key_file()
    {
        Directory.CreateDirectory(_dir);
        await new McpApiKeyStore(new ListLogger<McpApiKeyStore>(), ApiKeysPath).InitializeAsync();
        File.Delete(KeyFilePath); // simulate the operator retrieving + removing it

        var logger = new ListLogger<McpApiKeyStore>();
        await new McpApiKeyStore(logger, ApiKeysPath).InitializeAsync();

        Assert.False(File.Exists(KeyFilePath), "an existing install must not regenerate the key file");
        Assert.Contains(logger.Messages, m => m.Contains("Loaded"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly List<string> Messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Whiskers.Configuration;
using Whiskers.Services.Mcp;

namespace Whiskers.Tests;

// C6 (extended): the permission system also generates a default MCP API key on first run and used to
// log it verbatim — the same leak as the legacy McpApiKeyStore. It must never reach the log.
public sealed class McpPermissionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mcpperm-{Guid.NewGuid():N}");

    [Fact]
    public async Task First_run_generates_a_key_but_never_logs_a_key_value()
    {
        Directory.CreateDirectory(_dir);
        var logger = new ListLogger<McpPermissionService>();
        var svc = new McpPermissionService(logger, new DataPathOptions(_dir));

        await svc.InitializeAsync();

        // A default key was created (the store reports one key)...
        Assert.Contains(logger.Messages, m => m.Contains("1 keys"));
        // ...but no log line may contain a 64-hex key value (Guid-N + Guid-N).
        Assert.DoesNotContain(logger.Messages, m => Regex.IsMatch(m, "[0-9a-fA-F]{64}"));
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

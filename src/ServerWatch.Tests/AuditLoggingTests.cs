using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServerWatch.Services.AuditLog;

namespace ServerWatch.Tests;

// Audit-logging fixes for Bean ServerWatch-wjhf: MIT-4 fail-safe fallback (here) and, later, MIT-7's
// redacted scheduler audit. MIT-3/NIED-4 are UI/tool wiring covered by pattern-mirroring + build/DI boot.
public class AuditLoggingTests
{
    // ---------------------------------------------------------------- MIT-4: fail-safe fallback

    [Fact]
    public async Task AuditLog_write_failure_logs_full_entry_at_Error()
    {
        var logger = new CapturingLogger<AuditLogService>();
        var svc = new AuditLogService(new ThrowingScopeFactory(), logger);

        await svc.LogAsync("alice@example.com", "web", "vault.delete", "vault", "api-key", "api-key",
            details: "removed", serverId: "prod-1", success: true);

        // The write failed, but the fact survives at Error with the full entry (not a bare message).
        var err = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.NotNull(err.Exception);
        foreach (var expected in new[] { "alice@example.com", "web", "vault.delete", "vault", "api-key", "prod-1" })
            Assert.Contains(expected, err.Message);
    }

    // --- test doubles ---

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        // Simulates the audit DB being unavailable: acquiring a scope throws, so LogAsync hits its catch.
        public IServiceScope CreateScope() => throw new InvalidOperationException("audit db unavailable");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

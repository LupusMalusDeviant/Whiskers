using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Database;
using Whiskers.Services.Persistence;
using Whiskers.Services.Server;
using Whiskers.Startup;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>CodeQL triage 2026-11 (docs/security/codeql-triage-2026-11.md): a logged value stays on one line
/// (<c>cs/log-forging</c>), an account's address never reaches the application log — the audit row keeps it
/// (<c>cs/exposure-of-sensitive-information</c>, contract 0003) — and a database password never stands in a logged
/// command (<c>cs/cleartext-storage-of-sensitive-information</c>).</summary>
public sealed class LogSafeTests
{
    private const string Address = "ops.lead@corp.example";

    // ---- LogSafe -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a\r\nFAKE b")]
    [InlineData("a\nFAKE b")]
    [InlineData("a\rFAKE b")]
    [InlineData("a\u0085FAKE b")]
    [InlineData("a\u2028FAKE b")]
    [InlineData("a\u2029FAKE b")]
    public void A_value_becomes_one_line(string value)
    {
        var line = LogSafe.OneLine(value);

        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\u0085', line);
        Assert.DoesNotContain('\u2028', line);
        Assert.DoesNotContain('\u2029', line);
        Assert.StartsWith("a ", line);
        Assert.Equal(line, AuditLogService.OneLine(value));   // the audit service uses the same rule
    }

    [Fact]
    public void An_account_is_a_stable_pseudonym_that_does_not_show_the_address()
    {
        var pseudonym = LogSafe.Account(Address);

        Assert.Matches("^\\[account:[0-9a-f]{8}\\]$", pseudonym);
        Assert.DoesNotContain("ops.lead", pseudonym);
        Assert.DoesNotContain("corp", pseudonym);
        Assert.Equal(pseudonym, LogSafe.Account("  OPS.Lead@Corp.Example "));
        Assert.NotEqual(pseudonym, LogSafe.Account("other@corp.example"));
        Assert.Equal(LogSafe.NoAccount, LogSafe.Account(null));
        Assert.Equal(LogSafe.NoAccount, LogSafe.Account(" "));
    }

    [Fact]
    public void Addresses_become_pseudonyms_and_digests_and_ssh_targets_stay()
    {
        var text = LogSafe.Addresses($"user:{Address} pulled nginx@sha256:abcdef as root@10.0.0.1");

        Assert.DoesNotContain(Address, text);
        Assert.Contains("user:" + LogSafe.Account(Address), text);
        Assert.Contains("nginx@sha256:abcdef", text);
        Assert.Contains("root@10.0.0.1", text);
        Assert.Equal("", LogSafe.Addresses(null));
    }

    // ---- the logger factory (every log line of the app) ---------------------------------------------------------

    [Fact]
    public void A_logged_value_with_line_breaks_stays_one_line_in_every_provider()
    {
        var (factory, capture) = Logging();

        factory.CreateLogger("Test").LogWarning("Webhook {WebhookId} rejected: {Problem}",
            "hook\r\n2026-11-10 ERROR forged line", "bad signature");

        var message = Assert.Single(capture.Messages);
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain('\r', message);
        Assert.Contains("hook  2026-11-10 ERROR forged line", message);
        Assert.All(capture.Values, v => Assert.DoesNotContain('\n', v));
    }

    [Fact]
    public void A_multi_line_template_keeps_its_own_line_breaks()
    {
        var (factory, capture) = Logging();

        factory.CreateLogger("Test").LogInformation("Header\nvalue {Value}", "x\ny");

        Assert.Equal("Header\nvalue x y", Assert.Single(capture.Messages));
    }

    [Fact]
    public void An_address_reaches_no_provider_in_a_value_a_scope_or_an_exception()
    {
        var (factory, capture) = Logging();
        var logger = factory.CreateLogger("Test");

        using (logger.BeginScope("Session {Owner}", Address))
            logger.LogError(new InvalidOperationException($"Username '{Address}' is already taken."),
                "Local account {Email} was created by {Actor}", Address, $"user:{Address}");

        Assert.DoesNotContain(Address, capture.All);
        Assert.Contains(LogSafe.Account(Address), capture.All);
    }

    // ---- the audit service: row keeps the address, the log line does not ------------------------------------------

    [Fact]
    public async Task An_audit_row_keeps_the_address_and_its_log_line_neither_names_it_nor_breaks()
    {
        using var trail = new AuditTrailFixture(vaultKey: null);
        var logs = new NotificationDeliveryTests.ListLoggerProvider();
        // Without the redacting factory: the service itself keeps the line clean.
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var audit = new AuditLogService(trail.Scopes, factory.CreateLogger<AuditLogService>());

        Assert.True(await audit.TryLogAsync(Address, "web", "setup.first_admin", "auth", Address,
            "web\r\n2026-11-10 forged", success: true));
        var lost = new AuditLogService(new ThrowingScopes(), factory.CreateLogger<AuditLogService>());
        Assert.False(await lost.TryLogAsync(Address, "web", "container.restart", "container", Address,
            "web\nforged", serverId: "local\nforged"));

        using (var scope = trail.Scopes.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.SingleAsync();
            Assert.Equal(Address, row.Actor);                    // the database keeps it as it was
            Assert.Equal(Address, row.TargetId);
            Assert.Equal("web\r\n2026-11-10 forged", row.TargetName);
        }
        Assert.Equal(2, logs.Lines.Count);
        // The message, not the exception's stack trace the list provider appends after " | ".
        Assert.All(logs.Lines.Select(l => l.Split(" | ")[0]), line =>
        {
            Assert.DoesNotContain(Address, line);
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            Assert.Contains(LogSafe.Account(Address), line);
        });
    }

    // ---- database passwords in host commands --------------------------------------------------------------------

    [Theory]
    [InlineData(DatabaseType.PostgreSQL)]
    [InlineData(DatabaseType.MySQL)]
    [InlineData(DatabaseType.Neo4j)]
    public async Task A_database_password_never_stands_in_a_logged_command(DatabaseType type)
    {
        const string password = "c0rrect horse's battery";
        var host = new RecordingHost();
        var db = new DatabaseService(host, NullLogger<DatabaseService>.Instance);
        var creds = new DatabaseCredentials { User = "admin", Password = password, Database = "app" };

        await db.GetDatabasesAsync("c0ffee", type, creds, "local");
        await db.ExecuteQueryAsync("c0ffee", "select 1", type, creds, "local");
        if (type != DatabaseType.Neo4j) await db.BackupDatabaseAsync("c0ffee", "app", type, creds, "local", containerName: "db");

        Assert.NotEmpty(host.Commands);
        foreach (var command in host.Commands)
        {
            // What the executor logs (debug line, timeout warning) is the redacted command.
            var logged = SecretRedactor.Redact(command);
            Assert.DoesNotContain("c0rrect", logged);
            Assert.DoesNotContain("battery", logged);
            // Never an argv element of its own (the host's process list shows those).
            Assert.DoesNotContain("'-p' ", command);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static (ILoggerFactory Factory, Capture Capture) Logging()
    {
        var capture = new Capture();
        var sp = new ServiceCollection()
            .AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace))
            .AddLogRedaction()
            .BuildServiceProvider();
        return (sp.GetRequiredService<ILoggerFactory>(), capture);
    }

    private sealed class Capture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;
        public ConcurrentQueue<string> Messages { get; } = new();
        public ConcurrentQueue<string> Values { get; } = new();
        private readonly ConcurrentQueue<string> _texts = new();
        public string All => string.Join("\n", _texts);

        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        private sealed class Logger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                owner.Messages.Enqueue(message);
                owner._texts.Enqueue(message);
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    foreach (var (key, value) in values)
                    {
                        owner._texts.Enqueue($"{key}={value}");
                        if (key != "{OriginalFormat}") owner.Values.Enqueue($"{value}");
                    }
                if (exception is not null) owner._texts.Enqueue(exception.ToString());
                owner._scopes?.ForEachScope((scope, texts) =>
                {
                    texts.Enqueue(scope?.ToString() ?? "");
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        foreach (var (key, value) in pairs) texts.Enqueue($"{key}={value}");
                }, owner._texts);
            }
        }
    }

    private sealed class ThrowingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("database unavailable");
    }

    private sealed class RecordingHost : IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            lock (Commands) Commands.Add(command);
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = "" });
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Database;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>Contract 0023 (batch 1, safe points): a database query or backup observes the gateway action's token before
/// anything is sent, and never inside its step — a dump, once started, is copied out and cleaned up.</summary>
public sealed class DatabaseSafePointTests
{
    private static readonly DatabaseCredentials Creds = new() { User = "admin", Password = "pw", Database = "app" };

    private sealed class ScriptedHost(Action<string>? onCommand = null) : IHostCommandExecutor
    {
        public List<string> Commands { get; } = [];

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            lock (Commands) Commands.Add(command);
            onCommand?.Invoke(command);
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = command.StartsWith("stat", StringComparison.Ordinal) ? "42" : "" });
        }
    }

    [Fact]
    public async Task A_query_stopped_before_it_is_sent_sends_nothing()
    {
        var host = new ScriptedHost();
        var db = new DatabaseService(host, NullLogger<DatabaseService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.ExecuteQueryAsync("c0ffee", "delete from t",
            DatabaseType.PostgreSQL, Creds, "local", new CancellationToken(canceled: true)));

        Assert.Empty(host.Commands);
    }

    [Fact]
    public async Task A_backup_stopped_before_the_dump_sends_nothing()
    {
        var host = new ScriptedHost();
        var db = new DatabaseService(host, NullLogger<DatabaseService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.BackupDatabaseAsync("c0ffee", "app",
            DatabaseType.PostgreSQL, Creds, "local", "db", new CancellationToken(canceled: true)));

        Assert.Empty(host.Commands);
    }

    [Fact]
    public async Task A_backup_stopped_during_the_dump_copies_it_out_and_cleans_up()
    {
        using var stop = new CancellationTokenSource();
        var host = new ScriptedHost(c => { if (c.Contains("pg_dump", StringComparison.Ordinal)) stop.Cancel(); });
        var db = new DatabaseService(host, NullLogger<DatabaseService>.Instance);

        var (success, path, size, error) = await db.BackupDatabaseAsync("c0ffee", "app", DatabaseType.PostgreSQL, Creds,
            "local", "db", stop.Token);

        Assert.True(stop.IsCancellationRequested);
        Assert.True(success, error);
        Assert.Equal(42, size);
        Assert.Contains(host.Commands, c => c.StartsWith("docker cp", StringComparison.Ordinal) && c.Contains(path, StringComparison.Ordinal));
        Assert.Contains(host.Commands, c => c.Contains("'rm'", StringComparison.Ordinal));   // the in-container dump is gone
    }
}

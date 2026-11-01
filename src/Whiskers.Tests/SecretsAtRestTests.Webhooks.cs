using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.AutoUpdate;
using Whiskers.Services.Docker;
using Whiskers.Services.Persistence;
using Whiskers.Services.Vault;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

// R1-31 (INV-52): the inbound webhooks' signing secrets and the rollback snapshots (a container's full configuration,
// its environment included) are stored in the database encrypted with a vault-derived key. After a save or the
// start-up migration neither the database nor a self-backup holds them in plaintext, and both still work.
public partial class SecretsAtRestTests
{
    private ServiceProvider MetricsDb()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_paths.DbPath}"));
        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        return sp;
    }

    /// <summary>A container whose configuration carries a secret in its environment, as Docker reports it.</summary>
    private static (string Json, string EnvSecret) SnapshotWithSecret()
    {
        var envSecret = "DBPASS-" + Guid.NewGuid().ToString("N");
        return ($"{{\"Name\":\"web\",\"Config\":{{\"Env\":[\"DB_PASSWORD={envSecret}\"]}}}}", envSecret);
    }

    private static AutoUpdateService Updates(IServiceProvider sp, IDockerService docker, IVaultService? vault)
        => new(sp.GetRequiredService<IServiceScopeFactory>(), docker, null!, null!, null!, null!, null!,
            NullLogger<AutoUpdateService>.Instance, vault);

    private static ComponentTests.UiCreatedResourceAuditTests.Stub<IDockerService> SnapshotDocker(string json, Action<string>? rolledBackWith = null)
        => new ComponentTests.UiCreatedResourceAuditTests.Stub<IDockerService>()
            .On(nameof(IDockerService.CaptureRollbackSnapshotAsync), _ => Task.FromResult(("sha256:old", json)))
            .On(nameof(IDockerService.RollbackContainerAsync), args =>
            {
                rolledBackWith?.Invoke((string)args[2]!);
                return Task.FromResult("web");
            });

    private static readonly ContainerInfo Web = new() { Id = "web-1", Name = "web", ServerId = "local", Image = "web:1" };

    [Fact]
    public async Task Webhook_secrets_and_rollback_snapshots_are_stored_encrypted_and_keep_working()
    {
        var vault = await Vault();
        await using var sp = MetricsDb();
        var hooks = new WebhookService(sp.GetRequiredService<IServiceScopeFactory>(), docker: null!, executor: null!,
            planner: null!, NullLogger<WebhookService>.Instance, vault: vault);
        var created = await hooks.CreateWebhookAsync(new WebhookEntity { Name = "ci", TargetId = "/x" });
        var rotated = await hooks.RegenerateSecretAsync(created.WebhookId);
        var (json, envSecret) = SnapshotWithSecret();
        string? rolledBackWith = null;
        var updates = Updates(sp, SnapshotDocker(json, j => rolledBackWith = j).Object, vault);
        await updates.CaptureSnapshotAsync(Web);

        TestSqlite.ClearPools(_root);
        await CreatePlaintextBackupAsync(vault);
        AssertNoPlaintext(created.Secret, rotated, envSecret);

        // Both still work: a delivery signed with the secret is verified, the rollback gets the configuration back.
        var body = "{}"u8.ToArray();
        var reply = await hooks.AcceptAsync(created.WebhookId, new WebhookRequest(body, GitHubSignature: WebhookSignatures.Sign(rotated, body)));
        Assert.NotEqual(WebhookReply.Rejected, reply.Reply);
        var snapshot = Assert.Single(await updates.GetRollbacksAsync());
        await updates.RollbackAsync(snapshot.Id);
        Assert.Equal(json, rolledBackWith);
        TestSqlite.ClearPools(_root);
    }

    [Fact]
    public async Task The_migration_encrypts_plaintext_webhook_secrets_and_snapshots_and_both_keep_working()
    {
        var secret = "WHSECRET-" + Guid.NewGuid().ToString("N");
        var (json, envSecret) = SnapshotWithSecret();
        await using var sp = MetricsDb();
        WebhookEntity hook;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            hook = new WebhookEntity { Name = "legacy", Secret = secret, TargetId = "/x" };
            db.Webhooks.Add(hook);
            db.UpdateRollbacks.Add(new UpdateRollbackEntity
            {
                ContainerId = "web-1", ContainerName = "web", ServerId = "local", OldImageRef = "sha256:old", ConfigJson = json,
                CapturedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // Without a usable vault nothing changes, and the plaintext values keep working.
        using (var scope = sp.CreateScope())
            await ProtectedFieldMigration.RunAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), await Vault(key: null),
                NullLogger.Instance);
        using (var scope = sp.CreateScope())
            Assert.Equal((1, 1), await ProtectedFieldMigration.CountPlaintextAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>()));

        var vault = await Vault();
        using (var scope = sp.CreateScope())
            await ProtectedFieldMigration.RunAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), vault, NullLogger.Instance);
        using (var scope = sp.CreateScope())
            Assert.Equal((0, 0), await ProtectedFieldMigration.CountPlaintextAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>()));

        TestSqlite.ClearPools(_root);
        await CreatePlaintextBackupAsync(vault);
        AssertNoPlaintext(secret, envSecret);

        var hooks = new WebhookService(sp.GetRequiredService<IServiceScopeFactory>(), docker: null!, executor: null!,
            planner: null!, NullLogger<WebhookService>.Instance, vault: vault);
        var body = "{}"u8.ToArray();
        var reply = await hooks.AcceptAsync(hook.WebhookId, new WebhookRequest(body, GitHubSignature: WebhookSignatures.Sign(secret, body)));
        Assert.NotEqual(WebhookReply.Rejected, reply.Reply);
        string? rolledBackWith = null;
        var updates = Updates(sp, SnapshotDocker(json, j => rolledBackWith = j).Object, vault);
        await updates.RollbackAsync(Assert.Single(await updates.GetRollbacksAsync()).Id);
        Assert.Equal(json, rolledBackWith);
        TestSqlite.ClearPools(_root);
    }

    [Fact]
    public async Task A_migration_whose_value_does_not_read_back_changes_no_row()
    {
        await using var sp = MetricsDb();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.Webhooks.Add(new WebhookEntity { WebhookId = "first", Name = "a", Secret = "SECRET-A", TargetId = "/x" });
            db.Webhooks.Add(new WebhookEntity { WebhookId = "second", Name = "b", Secret = "SECRET-B", TargetId = "/x" });
            await db.SaveChangesAsync();
        }
        var vault = new UnreadableFor(await Vault(), ProtectedFields.WebhookSecret("second"));

        using (var scope = sp.CreateScope())
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProtectedFieldMigration.RunAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), vault, NullLogger.Instance));

        using (var scope = sp.CreateScope())
            Assert.Equal(["SECRET-A", "SECRET-B"],
                scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Webhooks.OrderBy(w => w.WebhookId).Select(w => w.Secret).ToList());
        TestSqlite.ClearPools(_root);
    }

    [Fact]
    public async Task In_wal_mode_the_migration_leaves_no_plaintext_in_the_live_database_files()
    {
        var secret = "WALSECRET-" + Guid.NewGuid().ToString("N");
        await using var sp = MetricsDb();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");   // as the app runs it
            db.Webhooks.Add(new WebhookEntity { Name = "legacy", Secret = secret, TargetId = "/x" });
            await db.SaveChangesAsync();
        }

        using (var scope = sp.CreateScope())
            await ProtectedFieldMigration.RunAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), await Vault(), NullLogger.Instance);

        // Read while the connection pool is still alive: closing it would fold the log back on its own.
        foreach (var file in Directory.EnumerateFiles(_root, "metrics.db*"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.False(System.Text.Encoding.UTF8.GetString(copy.ToArray()).Contains(secret, StringComparison.Ordinal),
                $"{Path.GetFileName(file)} still holds the replaced secret");
        }
        TestSqlite.ClearPools(_root);
    }

    [Fact]
    public async Task A_log_held_by_a_reader_is_reported_not_ignored()
    {
        await using var sp = MetricsDb();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            db.Webhooks.Add(new WebhookEntity { Name = "legacy", Secret = "SECRET-" + Guid.NewGuid().ToString("N"), TargetId = "/x" });
            await db.SaveChangesAsync();
        }
        // A reader in the middle of a transaction holds the log: the checkpoint cannot fold it back.
        await using var reader = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_paths.DbPath};Pooling=False");
        await reader.OpenAsync();
        await using var read = reader.BeginTransaction(deferred: true);
        await using (var select = reader.CreateCommand())
        {
            select.Transaction = read;
            select.CommandText = "SELECT COUNT(*) FROM \"Webhooks\";";
            await select.ExecuteScalarAsync();
        }

        var logger = new ListLogger();
        using (var scope = sp.CreateScope())
            await ProtectedFieldMigration.RunAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), await Vault(), logger);

        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("busy"));
        await read.RollbackAsync();
        TestSqlite.ClearPools(_root);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>A usable vault whose values for one purpose do not read back.</summary>
    private sealed class UnreadableFor(IVaultService inner, string purpose) : IVaultService
    {
        public bool IsEnabled => inner.IsEnabled;
        public VaultKeyState KeyState => inner.KeyState;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public List<VaultEntry> ListSecrets() => inner.ListSecrets();
        public Task SetSecretAsync(string key, string plainValue, string? containerId = null, string? containerName = null, int? rotateAfterDays = null)
            => throw new NotSupportedException();
        public string? GetSecret(string key) => inner.GetSecret(key);
        public Task DeleteSecretAsync(string key) => throw new NotSupportedException();
        public List<VaultEntry> GetExpiringSecrets() => [];
        public string? ProtectField(string p, string plaintext) => inner.ProtectField(p, plaintext);
        public bool TryUnprotectField(string p, string stored, out string plaintext)
        {
            plaintext = "";
            return p != purpose && inner.TryUnprotectField(p, stored, out plaintext);
        }
    }

    [Fact]
    public async Task Without_a_usable_vault_no_snapshot_is_kept_and_no_older_one_is_offered()
    {
        var vault = await Vault();
        await using var sp = MetricsDb();
        var (json, envSecret) = SnapshotWithSecret();
        await Updates(sp, SnapshotDocker(json).Object, vault).CaptureSnapshotAsync(Web);

        var inspected = false;
        var docker = SnapshotDocker(json).On(nameof(IDockerService.CaptureRollbackSnapshotAsync), _ =>
        {
            inspected = true;
            return Task.FromResult(("sha256:new", json));
        }).Object;
        var off = Updates(sp, docker, await Vault(key: null));
        await Assert.ThrowsAsync<RollbackSnapshotUnavailableException>(() => off.CaptureSnapshotAsync(Web));
        Assert.False(inspected);
        Assert.Empty(await off.GetRollbacksAsync());

        TestSqlite.ClearPools(_root);
        AssertNoPlaintext(envSecret);
    }

    [Fact]
    public async Task A_snapshot_that_cannot_be_opened_is_not_rolled_back_to()
    {
        var vault = await Vault();
        await using var sp = MetricsDb();
        var (json, _) = SnapshotWithSecret();
        var rolledBack = false;
        await Updates(sp, SnapshotDocker(json).Object, vault).CaptureSnapshotAsync(Web);

        var locked = Updates(sp, SnapshotDocker(json, _ => rolledBack = true).Object, await Vault("another-passphrase-entirely"));
        var snapshot = Assert.Single(await locked.GetRollbacksAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => locked.RollbackAsync(snapshot.Id));
        Assert.False(rolledBack);
        TestSqlite.ClearPools(_root);
    }
}

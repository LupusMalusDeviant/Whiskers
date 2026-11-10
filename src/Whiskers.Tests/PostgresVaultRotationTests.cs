using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Persistence;
using Whiskers.Services.Vault;

namespace Whiskers.Tests;

/// <summary>Contract 0015 Part B, step 4 on a REAL PostgreSQL 18 (Testcontainers): the protected fields are re-sealed and
/// the audit trail re-signed in one transaction with the production registration (retries on); a fault before the commit
/// rolls everything back. Marked <c>RequiresDocker</c>: mandatory on CI (<c>docker.txt</c>).</summary>
[Trait("Category", DockerRequirement.Category)]
public sealed class PostgresVaultRotationTests : IAsyncLifetime
{
    private const string OldKey = "pg-rotation-old";
    private const string NewKey = "pg-rotation-new";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-tests", "pgrot-" + Guid.NewGuid().ToString("N"));
    private Exception? _startFailure;
    private bool _up;

    public async Task InitializeAsync()
    {
        _startFailure = await DockerRequirement.TryStartAsync(_pg);
        _up = _startFailure is null;
        Directory.CreateDirectory(_root);
    }

    public async Task DisposeAsync()
    {
        if (_up) await _pg.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<VaultService> Vault(DataPathOptions paths, string key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VAULT_KEY_FILE"] = "", ["VAULT_KEY"] = key }).Build();
        var vault = new VaultService(config, NullLogger<VaultService>.Instance, dataPaths: paths);
        await vault.InitializeAsync();
        return vault;
    }

    [SkippableFact]
    public async Task Step_four_commits_on_postgres_and_a_fault_before_the_commit_rolls_back()
    {
        DockerRequirement.SkipUnlessStarted(_startFailure);
        var paths = new DataPathOptions(_root);
        var services = new ServiceCollection();
        services.AddWhiskersDbContexts("postgres", _pg.GetConnectionString(), paths);
        services.AddSingleton<IAuditChain>(sp => new AuditChain(OldKey, paths.AuditCheckpointJson, scopes: sp.GetRequiredService<IServiceScopeFactory>()));
        await using var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), NullLogger.Instance);

        var oldVault = await Vault(paths, OldKey);
        var secret = "whsec-" + Guid.NewGuid().ToString("N");
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var webhook = new WebhookEntity { Name = "deploy", TargetId = "web" };
            webhook.Secret = oldVault.ProtectField(ProtectedFields.WebhookSecret(webhook.WebhookId), secret)!;
            db.Webhooks.Add(webhook);
            await db.SaveChangesAsync();
        }
        var audit = new AuditLogService(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditLogService>.Instance);
        for (var i = 0; i < 3; i++) Assert.True(await audit.TryLogAsync("admin@local.test", "web", "container.restart", "container", $"c{i}", $"c{i}"));
        await sp.GetRequiredService<IAuditChain>().CheckpointAsync();

        // A fault inside the transaction: nothing of step 4 stays.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VaultKeyRotation(paths, sp, OldKey, NewKey)
        {
            FaultAt = s => { if (s == "before-commit") throw new InvalidOperationException("fault"); },
        }.RunAsync());
        await AssertDatabaseUnder(sp, paths, OldKey, secret);

        var journal = await new VaultKeyRotation(paths, sp, OldKey, NewKey).RunAsync();

        Assert.Equal(VaultKeyRotationJournal.Done, journal.Phase);
        Assert.Equal(3, journal.Counts!.AuditLinks);
        await AssertDatabaseUnder(sp, paths, NewKey, secret);
    }

    private async Task AssertDatabaseUnder(IServiceProvider sp, DataPathOptions paths, string key, string secret)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var webhook = await db.Webhooks.AsNoTracking().SingleAsync();
        // The vault of that key: the old one is vault.json until the switch; the new one after it.
        var vault = await Vault(paths, key);
        Assert.Equal(secret, ProtectedFields.Read(vault, ProtectedFields.WebhookSecret(webhook.WebhookId), webhook.Secret));
        var report = await new AuditChain(key, paths.AuditCheckpointJson).VerifyAsync(db, default);
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.True(report.CheckpointsSigned);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Modules.Webhooks;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>F11 / HOCH-12 part 2 — webhook secrets are mandatory. Covers: server-side secret
/// generation on create, fail-closed trigger validation (no secret / no signature / bad signature),
/// the signed end-to-end self test, secret regeneration, the enable guard for secret-less rows, and
/// the boot migration that disables legacy secret-less webhooks (keeping them, never deleting).</summary>
public sealed class WebhookSecretTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-secrets-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public WebhookSecretTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    /// <summary>Service under test. Docker/executor stay null — every covered path returns before
    /// touching them (validation failures) or is routed to the deploy-path guard (relative path).</summary>
    private WebhookService Service() => new(
        _sp.GetRequiredService<IServiceScopeFactory>(), docker: null!, executor: null!,
        NullLogger<WebhookService>.Instance);

    private WebhookEntity Seed(string secret, bool enabled = true)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var wh = new WebhookEntity
        {
            Name = $"wh-{Guid.NewGuid():N}"[..12],
            Secret = secret,
            Action = "deploy",
            TargetType = "compose",
            TargetId = "relative/path", // hits the absolute-path guard AFTER signature validation
            Enabled = enabled,
        };
        db.Webhooks.Add(wh);
        db.SaveChanges();
        return wh;
    }

    // --- Creation ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_generates_a_strong_secret_when_none_is_supplied()
    {
        var created = await Service().CreateWebhookAsync(new WebhookEntity { Name = "ci", TargetId = "/x" });
        Assert.Equal(64, created.Secret.Length); // 256-bit lowercase hex
        Assert.Matches("^[0-9a-f]{64}$", created.Secret);
    }

    [Fact]
    public async Task Create_keeps_an_explicitly_supplied_secret()
    {
        var created = await Service().CreateWebhookAsync(new WebhookEntity { Name = "ci", Secret = "caller-chosen", TargetId = "/x" });
        Assert.Equal("caller-chosen", created.Secret);
    }

    // --- Trigger validation (fail-closed) -----------------------------------------------------------------

    [Fact]
    public async Task Trigger_rejects_a_webhook_without_a_secret_fail_closed()
    {
        var wh = Seed(secret: "");
        var (success, output) = await Service().TriggerAsync(wh.WebhookId, signature: null, body: "{}");
        Assert.False(success);
        Assert.Contains("no secret", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Trigger_requires_a_signature()
    {
        var wh = Seed(secret: "s3cret");
        var (success, output) = await Service().TriggerAsync(wh.WebhookId, signature: null, body: "{}");
        Assert.False(success);
        Assert.Equal("Signature required", output);
    }

    [Fact]
    public async Task Trigger_rejects_an_invalid_signature()
    {
        var wh = Seed(secret: "s3cret");
        var (success, output) = await Service().TriggerAsync(wh.WebhookId,
            signature: "sha256=" + new string('0', 64), body: "{}");
        Assert.False(success);
        Assert.Equal("Invalid signature", output);
    }

    [Fact]
    public async Task Signed_test_passes_signature_validation_end_to_end()
    {
        // The seeded webhook uses a RELATIVE deploy path: a correctly signed request must get PAST the
        // HMAC check and fail at the absolute-path guard instead — proving the signature was accepted
        // without needing a Docker host.
        var wh = Seed(secret: "s3cret");
        var (success, output) = await Service().TriggerSignedTestAsync(wh.WebhookId);
        Assert.False(success);
        Assert.Equal("Deploy target must be an absolute path.", output);
    }

    // --- Regenerate + enable guard -------------------------------------------------------------------------

    [Fact]
    public async Task Regenerate_replaces_the_secret_and_returns_it_once()
    {
        var wh = Seed(secret: "old");
        var fresh = await Service().RegenerateSecretAsync(wh.WebhookId);
        Assert.Matches("^[0-9a-f]{64}$", fresh);

        using var scope = _sp.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<MetricsDbContext>()
            .Webhooks.Single(w => w.WebhookId == wh.WebhookId);
        Assert.Equal(fresh, stored.Secret);
    }

    [Fact]
    public async Task Enabling_a_secretless_webhook_is_rejected()
    {
        var wh = Seed(secret: "", enabled: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().SetEnabledAsync(wh.WebhookId, true));
    }

    [Fact]
    public async Task Disabling_and_reenabling_works_with_a_secret()
    {
        var wh = Seed(secret: "s3cret");
        var svc = Service();
        await svc.SetEnabledAsync(wh.WebhookId, false);
        await svc.SetEnabledAsync(wh.WebhookId, true);
        using var scope = _sp.CreateScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<MetricsDbContext>()
            .Webhooks.Single(w => w.WebhookId == wh.WebhookId).Enabled);
    }

    // --- Boot migration ------------------------------------------------------------------------------------

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<NotificationEvent> Sent { get; } = new();
        public Task SendAsync(NotificationEvent evt) { Sent.Add(evt); return Task.CompletedTask; }
        public Task SendTestAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task Module_init_disables_legacy_secretless_webhooks_and_notifies_admin()
    {
        var legacy = Seed(secret: "", enabled: true);
        var healthy = Seed(secret: "s3cret", enabled: true);

        var notify = new RecordingNotificationService();
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<INotificationService>(notify);
        using var sp = services.BuildServiceProvider();

        await new WebhooksModule().InitializeAsync(sp, CancellationToken.None);

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        Assert.False(db.Webhooks.Single(w => w.WebhookId == legacy.WebhookId).Enabled); // disabled, not deleted
        Assert.True(db.Webhooks.Single(w => w.WebhookId == healthy.WebhookId).Enabled); // untouched
        var evt = Assert.Single(notify.Sent);
        Assert.Equal("webhook_disabled", evt.EventType);
        Assert.Contains(legacy.Name, evt.ImageInfo);
    }

    [Fact]
    public async Task Module_init_is_a_noop_when_all_webhooks_have_secrets()
    {
        Seed(secret: "s3cret", enabled: true);
        var notify = new RecordingNotificationService();
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<INotificationService>(notify);
        using var sp = services.BuildServiceProvider();

        await new WebhooksModule().InitializeAsync(sp, CancellationToken.None);
        Assert.Empty(notify.Sent);
    }
}

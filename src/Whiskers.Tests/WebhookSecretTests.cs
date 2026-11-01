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
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { /* a detached retention pass may still hold it */ }
    }

    /// <summary>Service under test. Docker/executor stay null — every covered path returns before
    /// touching them (validation failures) or is routed to the deploy-path guard (relative path).</summary>
    private WebhookService Service() => new(
        _sp.GetRequiredService<IServiceScopeFactory>(), docker: null!, executor: null!, planner: null!,
        NullLogger<WebhookService>.Instance, vault: TestVault.Usable);

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
        var reply = await Service().AcceptAsync(wh.WebhookId, new WebhookRequest("{}"u8.ToArray()));
        Assert.Equal(WebhookReply.Rejected, reply.Reply);
    }

    [Fact]
    public async Task Trigger_requires_a_signature()
    {
        var wh = Seed(secret: "s3cret");
        var reply = await Service().AcceptAsync(wh.WebhookId, new WebhookRequest("{}"u8.ToArray()));
        Assert.Equal(WebhookReply.Rejected, reply.Reply);
    }

    [Fact]
    public async Task Trigger_rejects_an_invalid_signature()
    {
        var wh = Seed(secret: "s3cret");
        var reply = await Service().AcceptAsync(wh.WebhookId,
            new WebhookRequest("{}"u8.ToArray(), GitHubSignature: "sha256=" + new string('0', 64)));
        Assert.Equal(WebhookReply.Rejected, reply.Reply);
    }

    [Fact]
    public async Task Signed_test_passes_signature_validation_end_to_end()
    {
        // A correctly signed request must get PAST the HMAC check and stop at the next step instead — the action
        // runs through the automation runner (R1-09), which this bare provider does not have — proving the
        // signature was accepted without needing a Docker host.
        var wh = Seed(secret: "s3cret");
        var (success, output) = await Service().TriggerSignedTestAsync(wh.WebhookId);
        Assert.False(success);
        Assert.Equal("Automated actions are not available.", output);
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
        // R1-31: stored encrypted, bound to this webhook — never the secret itself.
        Assert.NotEqual(fresh, stored.Secret);
        Assert.Equal(fresh, Whiskers.Services.Vault.ProtectedFields.Read(TestVault.Usable,
            Whiskers.Services.Vault.ProtectedFields.WebhookSecret(wh.WebhookId), stored.Secret));
    }

    [Fact]
    public async Task Without_a_usable_vault_creating_or_rotating_a_secret_is_refused_and_nothing_is_written()
    {
        var wh = Seed(secret: "old");
        var noVault = new WebhookService(_sp.GetRequiredService<IServiceScopeFactory>(), docker: null!, executor: null!,
            planner: null!, NullLogger<WebhookService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => noVault.CreateWebhookAsync(new WebhookEntity { Name = "ci", TargetId = "/x" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => noVault.RegenerateSecretAsync(wh.WebhookId));

        using var scope = _sp.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Webhooks.ToList();
        Assert.Equal("old", Assert.Single(rows).Secret);
    }

    [Fact]
    public async Task A_secret_that_cannot_be_opened_refuses_every_delivery()
    {
        var created = await Service().CreateWebhookAsync(new WebhookEntity { Name = "ci", TargetId = "/x", Provider = WebhookSignatures.GitHub });
        var body = "{}"u8.ToArray();
        var signed = new WebhookRequest(body, GitHubSignature: WebhookSignatures.Sign(created.Secret, body));

        // The vault is off: the encrypted secret is unreadable, so even a correctly signed delivery is refused.
        var noVault = new WebhookService(_sp.GetRequiredService<IServiceScopeFactory>(), docker: null!, executor: null!,
            planner: null!, NullLogger<WebhookService>.Instance);
        Assert.Equal(WebhookReply.Rejected, (await noVault.AcceptAsync(created.WebhookId, signed)).Reply);
        var (success, _) = await noVault.TriggerSignedTestAsync(created.WebhookId);
        Assert.False(success);
    }

    [Fact]
    public async Task A_secret_copied_to_another_webhook_does_not_open_there()
    {
        var first = await Service().CreateWebhookAsync(new WebhookEntity { Name = "a", TargetId = "/x" });
        var second = Seed(secret: "");
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var stored = db.Webhooks.Single(w => w.WebhookId == first.WebhookId).Secret;
            db.Webhooks.Single(w => w.WebhookId == second.WebhookId).Secret = stored;
            db.SaveChanges();
        }
        var body = "{}"u8.ToArray();
        var reply = await Service().AcceptAsync(second.WebhookId,
            new WebhookRequest(body, GitHubSignature: WebhookSignatures.Sign(first.Secret, body)));
        Assert.Equal(WebhookReply.Rejected, reply.Reply);
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
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
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
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        services.AddSingleton<INotificationService>(notify);
        using var sp = services.BuildServiceProvider();

        await new WebhooksModule().InitializeAsync(sp, CancellationToken.None);
        Assert.Empty(notify.Sent);
    }
}

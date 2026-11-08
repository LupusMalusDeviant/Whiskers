using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>
/// R1-10 (INV-42): a webhook delivery is fresh, authentic and runs once. The provider rule decides what is signed —
/// a timestamp outside the window, a changed (unsigned) delivery id or timestamp, and an unsigned GitHub delivery id
/// never make a delivery new or fresh. A delivery runs at most once: concurrent duplicates, a duplicate after a
/// restart, and one cut off while running (interrupted, not run again). Runs on one target never overlap, and the
/// body, request rates and queue are bounded. Every invalid request gets the same answer; an accepted one its run id.
/// </summary>
public sealed class WebhookReplayTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"webhook-replay-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;
    private readonly RecordingRunner _runner = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly WebhookOptions _options = new();
    private readonly Faults _faults = new();
    private readonly string _outboxRoot = Path.Combine(Path.GetTempPath(), $"webhook-replay-outbox-{Guid.NewGuid():N}");
    private readonly AuditOutbox _outbox;

    public WebhookReplayTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False").AddInterceptors(_faults));
        services.AddSingleton<IAutomationMutationRunner>(_runner);
        _outbox = new AuditOutbox(new Whiskers.Configuration.DataPathOptions(_outboxRoot));
        services.AddSingleton(_outbox);
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _faults.Unstall();
        _faults.Unlock();
        _runner.Unblock();
        _runner.Release();
        _sp.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
        try { Directory.Delete(_outboxRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A new service is a new process: what another one queued or ran reads as cut off by a restart. Every
    /// one stops with the test (review round 5, MINOR-6): no background retry outlives it.</summary>
    private WebhookService Service(IDockerService? docker = null, IStringLocalizer<SharedResource>? text = null,
        IComposePlanner? planner = null, CancellationTokenSource? stop = null) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(), docker: docker!, executor: null!, planner: planner!,
            NullLogger<WebhookService>.Instance, Options.Create(_options), _time,
            lifetime: new StopsWith(CancellationTokenSource.CreateLinkedTokenSource(_stop.Token,
                (stop ?? _stop).Token).Token), text: text);

    private readonly CancellationTokenSource _stop = new();

    /// <summary>A host whose stopping is the given token.</summary>
    private sealed class StopsWith(CancellationToken stopping) : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private void Update(string webhookId, Action<WebhookEntity> change)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        change(db.Webhooks.Single(w => w.WebhookId == webhookId));
        db.SaveChanges();
    }

    private WebhookEntity Seed(string provider = WebhookSignatures.GitHub, string target = "web-1", bool enabled = true)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var wh = new WebhookEntity
        {
            Name = $"wh-{Guid.NewGuid():N}"[..12], Secret = "s3cret", Action = "restart", TargetType = "container",
            TargetId = target, ServerId = "local", Owner = "user:admin@example.com", Provider = provider, Enabled = enabled,
        };
        db.Webhooks.Add(wh);
        db.SaveChanges();
        return wh;
    }

    private static WebhookRequest GitHub(WebhookEntity wh, string body, string? unsignedDelivery = null) =>
        new(Encoding.UTF8.GetBytes(body), GitHubSignature: WebhookSignatures.Sign(wh.Secret, Encoding.UTF8.GetBytes(body)),
            WhiskersDelivery: unsignedDelivery);

    private WebhookRequest Signed(WebhookEntity wh, string delivery, DateTimeOffset? at = null, string body = "{\"ref\":\"main\"}")
    {
        var ts = (at ?? _time.GetUtcNow()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var bytes = Encoding.UTF8.GetBytes(body);
        return new WebhookRequest(bytes,
            WhiskersSignature: WebhookSignatures.Sign(wh.Secret, WebhookSignatures.SignedPayload(ts, delivery, bytes)),
            WhiskersTimestamp: ts, WhiskersDelivery: delivery);
    }

    private async Task<WebhookDeliveryEntity> Delivery(string runId)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries.AsNoTracking()
            .SingleAsync(d => d.RunId == runId);
    }

    private async Task<WebhookDeliveryEntity> Finished(string runId)
    {
        for (var i = 0; i < 200; i++)
        {
            var row = await Delivery(runId);
            if (row.State is not ("Queued" or "Running")) return row;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Run {runId} did not finish.");
    }

    // ---------------------------------------------------------------- freshness and what is signed

    [Theory]
    [InlineData(-10)]   // replayed ten minutes later
    [InlineData(10)]    // dated ahead
    public async Task A_timestamp_outside_the_window_is_rejected(int minutes)
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var reply = await Service().AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0001", _time.GetUtcNow().AddMinutes(minutes)));

        Assert.Equal(new WebhookAcceptance(WebhookReply.Rejected), reply);
        Assert.Equal(0, _runner.Runs);
    }

    [Fact]
    public async Task A_fresh_signed_delivery_is_accepted_with_its_run_id()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var reply = await Service().AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0002", _time.GetUtcNow().AddMinutes(-4)));

        Assert.Equal(WebhookReply.Accepted, reply.Reply);
        Assert.Equal("queued", reply.State);
        Assert.Equal("Succeeded", (await Finished(reply.RunId!)).State);
        // The run id is the run's operation id (R1-18): decision, run and audit carry it.
        Assert.Equal(reply.RunId, Assert.Single(_runner.OperationIds));
    }

    [Fact]
    public async Task Changing_the_signed_metadata_breaks_the_signature()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var original = Signed(wh, "delivery-0003");
        var service = Service();

        // A captured delivery with a new delivery id or a new timestamp is not a new or fresh delivery.
        Assert.Equal(WebhookReply.Rejected, (await service.AcceptAsync(wh.WebhookId,
            original with { WhiskersDelivery = "delivery-9999" })).Reply);
        Assert.Equal(WebhookReply.Rejected, (await service.AcceptAsync(wh.WebhookId,
            original with { WhiskersTimestamp = _time.GetUtcNow().AddSeconds(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) })).Reply);
        Assert.Equal(0, _runner.Runs);
    }

    [Fact]
    public async Task A_delivery_id_cannot_trade_characters_with_the_body()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        // "{ts}.delivery-0008.part.{}" would sign both id "delivery-0008.part" with body "{}" and id "delivery-0008"
        // with body "part.{}": a captured delivery could come back as a new one. An id with a dot is never valid,
        // so the signed fields split one way only.
        var dotted = Signed(wh, "delivery-0008.part", body: "{}");
        var split = new WebhookRequest(Encoding.UTF8.GetBytes("part.{}"), WhiskersSignature: dotted.WhiskersSignature,
            WhiskersTimestamp: dotted.WhiskersTimestamp, WhiskersDelivery: "delivery-0008");

        Assert.Equal(WebhookReply.Rejected, (await service.AcceptAsync(wh.WebhookId, dotted)).Reply);
        var accepted = await service.AcceptAsync(wh.WebhookId, split);
        await Finished(accepted.RunId!);
        Assert.Equal(WebhookReply.Rejected, (await service.AcceptAsync(wh.WebhookId, dotted)).Reply);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task An_unsigned_delivery_id_never_makes_a_github_delivery_new()
    {
        var wh = Seed();
        var service = Service();
        var first = await service.AcceptAsync(wh.WebhookId, GitHub(wh, "{\"after\":\"abc\"}", "id-1"));
        await Finished(first.RunId!);

        // The same signed body with another (unsigned) delivery id is the same delivery.
        var replay = await service.AcceptAsync(wh.WebhookId, GitHub(wh, "{\"after\":\"abc\"}", "id-2"));

        Assert.Equal(WebhookReply.Duplicate, replay.Reply);
        Assert.Equal(first.RunId, replay.RunId);
        Assert.Equal("succeeded", replay.State);
        Assert.Equal(1, _runner.Runs);
    }

    // ---------------------------------------------------------------- runs once

    [Fact]
    public async Task Concurrent_duplicates_run_once()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        var request = Signed(wh, "delivery-0004");

        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.AcceptAsync(wh.WebhookId, request))));

        var accepted = Assert.Single(replies, r => r.Reply == WebhookReply.Accepted);
        // A repeat while the first is still being recorded is busy (it may come again); after that, a duplicate.
        Assert.All(replies.Where(r => r.Reply != WebhookReply.Accepted), r =>
            Assert.True(r == new WebhookAcceptance(WebhookReply.Busy)
                        || r.Reply == WebhookReply.Duplicate && r.RunId == accepted.RunId, r.ToString()));
        await Finished(accepted.RunId!);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task A_duplicate_after_a_restart_does_not_run_again()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0005");
        var first = await Service().AcceptAsync(wh.WebhookId, request);
        await Finished(first.RunId!);

        var afterRestart = await Service().AcceptAsync(wh.WebhookId, request);

        Assert.Equal(WebhookReply.Duplicate, afterRestart.Reply);
        Assert.Equal(first.RunId, afterRestart.RunId);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task A_run_cut_off_by_a_restart_is_interrupted_and_not_run_again()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0006");
        _runner.Hold();
        var first = await Service().AcceptAsync(wh.WebhookId, request);
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // The process that runs it "stopped"; the provider delivers again to the new one.
        var afterRestart = await Service().AcceptAsync(wh.WebhookId, request);

        Assert.Equal(WebhookReply.Duplicate, afterRestart.Reply);
        Assert.Equal(first.RunId, afterRestart.RunId);
        Assert.Equal("interrupted", afterRestart.State);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task A_delivery_never_accepted_before_a_restart_runs_on_its_next_delivery()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0007");
        SeedDelivery(wh, request, "recording-before-restart", "Recording");

        var again = await Service().AcceptAsync(wh.WebhookId, request);

        Assert.Equal(WebhookReply.Accepted, again.Reply);
        Assert.Equal("recording-before-restart", again.RunId);
        Assert.Equal("Succeeded", (await Finished(again.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
    }

    /// <summary>INV-45 (review round 4, MAJOR-1): a delivery an earlier process accepted and left queued is never taken
    /// over — its run id may already say it did not run. A repeat is busy until the reconciliation settled it, then a
    /// duplicate of the failed run; it is one event and never runs.</summary>
    [Fact]
    public async Task A_delivery_accepted_before_a_restart_is_settled_never_taken_over()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0008");
        SeedDelivery(wh, request, "queued-before-restart", "Queued");
        var service = Service();

        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), await service.AcceptAsync(wh.WebhookId, request));
        await service.ReconcileAsync();

        Assert.Equal(new WebhookAcceptance(WebhookReply.Duplicate, "queued-before-restart", "failed"),
            await service.AcceptAsync(wh.WebhookId, request));
        await NotRunAsync("queued-before-restart", "container.restart");
        Assert.Equal(0, _runner.Runs);
    }

    private void SeedDelivery(WebhookEntity wh, WebhookRequest request, string runId, string state)
    {
        var (key, _) = WebhookSignatures.Verify(wh, request, _time.GetUtcNow(), _options.TimestampTolerance);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.WebhookDeliveries.Add(new WebhookDeliveryEntity
        {
            WebhookId = wh.WebhookId, DeliveryKey = key!, RunId = runId, State = state,
            Instance = "stopped-process/" + runId, ReceivedAt = _time.GetUtcNow().UtcDateTime,
        });
        db.SaveChanges();
    }

    // ---------------------------------------------------------------- serialization and bounds

    [Fact]
    public async Task Runs_on_the_same_target_never_overlap()
    {
        _options.MaxConcurrentRuns = 4;
        var one = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var two = Seed(WebhookSignatures.Whiskers, target: "web-1");   // another webhook, the same container
        var service = Service();
        _runner.Delay = TimeSpan.FromMilliseconds(100);

        var replies = await Task.WhenAll(
            service.AcceptAsync(one.WebhookId, Signed(one, "delivery-0101")),
            service.AcceptAsync(one.WebhookId, Signed(one, "delivery-0102")),
            service.AcceptAsync(two.WebhookId, Signed(two, "delivery-0103")));
        foreach (var r in replies) await Finished(r.RunId!);

        Assert.Equal(3, _runner.Runs);
        Assert.Equal(1, _runner.MaxConcurrent);
    }

    [Fact]
    public async Task Runs_on_one_container_never_overlap_whatever_it_is_called()
    {
        _options.MaxConcurrentRuns = 4;
        var docker = new FakeDocker(new ContainerInfo { Id = "aaa111bbb222ccc333", Name = "web-1", ServerId = "local" });
        var byName = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var byId = Seed(WebhookSignatures.Whiskers, target: "aaa111bbb222ccc333");
        var byPrefix = Seed(WebhookSignatures.Whiskers, target: "aaa111");
        var service = Service(docker);
        _runner.Delay = TimeSpan.FromMilliseconds(100);

        var replies = await Task.WhenAll(
            service.AcceptAsync(byName.WebhookId, Signed(byName, "delivery-0111")),
            service.AcceptAsync(byId.WebhookId, Signed(byId, "delivery-0112")),
            service.AcceptAsync(byPrefix.WebhookId, Signed(byPrefix, "delivery-0113")));
        foreach (var r in replies) await Finished(r.RunId!);

        Assert.Equal(3, _runner.Runs);
        Assert.Equal(1, _runner.MaxConcurrent);
        Assert.Equal(WebhookService.NormalizeDirectory("/opt//shop/./web/../"), WebhookService.NormalizeDirectory("/opt/shop"));
    }

    [Fact]
    public async Task A_rebuild_holds_off_every_run_on_its_replacement_until_it_ends()
    {
        _options.MaxConcurrentRuns = 4;
        var container = new ContainerInfo { Id = "aaa111bbb222ccc333", Name = "web-1", ServerId = "local" };
        var docker = new FakeDocker(container);
        var byName = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var byNewId = Seed(WebhookSignatures.Whiskers, target: "ddd444eee555fff666");
        var service = Service(docker);
        _runner.Hold();
        var first = await service.AcceptAsync(byName.WebhookId, Signed(byName, "delivery-0114"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // The rebuild has started the replacement under a new id but has not ended: runs on the replacement — by
        // its name or by its new id — wait for it.
        container.Id = "ddd444eee555fff666";
        var again = await service.AcceptAsync(byName.WebhookId, Signed(byName, "delivery-0115"));
        var direct = await service.AcceptAsync(byNewId.WebhookId, Signed(byNewId, "delivery-0116"));
        await Task.Delay(300);
        Assert.Equal(1, _runner.Runs);

        _runner.Release();
        foreach (var r in new[] { first, again, direct }) Assert.Equal("Succeeded", (await Finished(r.RunId!)).State);
        Assert.Equal(1, _runner.MaxConcurrent);
        // Each acted on the container it held the lock for: the replacement, once it was the one there.
        Assert.Equal(["aaa111bbb222ccc333", "ddd444eee555fff666", "ddd444eee555fff666"], _runner.ContainerIds);
        await WaitFor(() => service.LockedResources == 0);
    }

    [Fact]
    public async Task A_deployment_never_overlaps_a_container_action_on_its_server_even_an_overdue_one()
    {
        _options.MaxConcurrentRuns = 4;
        _options.RunTimeout = TimeSpan.FromMilliseconds(400);
        var docker = new FakeDocker(new ContainerInfo { Id = "aaa111bbb222ccc333", Name = "shop-web-1", ServerId = "local" });
        var rebuild = Seed(WebhookSignatures.Whiskers, target: "shop-web-1");
        var deploy = Seed(WebhookSignatures.Whiskers, target: "/opt/shop");
        Update(deploy.WebhookId, w => w.Action = "deploy");
        var service = Service(docker, planner: new ProjectPlanner("shop"));
        _runner.Hold();

        // The rebuild does not stop at its time limit: it keeps the server's containers until it ends.
        var first = await service.AcceptAsync(rebuild.WebhookId, Signed(rebuild, "delivery-0164"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var deployment = await service.AcceptAsync(deploy.WebhookId, Signed(deploy, "delivery-0165"));

        Assert.Equal("Failed", (await Finished(deployment.RunId!)).State);   // it never got its turn
        Assert.Equal(1, _runner.Runs);
        _runner.Release();
        Assert.Equal("Succeeded", (await Finished(first.RunId!)).State);
        await WaitFor(() => service.LockedResources == 0);
    }

    [Fact]
    public async Task Deployments_of_one_compose_project_never_overlap_whatever_directory_names_it()
    {
        _options.MaxConcurrentRuns = 4;
        // A symlinked directory and another directory declaring the same project name: one compose project.
        var planner = new ProjectPlanner("shop");
        var hooks = new[] { "/srv/shop", "/opt/shop", "/opt/shop-copy" }.Select(dir =>
        {
            var wh = Seed(WebhookSignatures.Whiskers, target: dir);
            Update(wh.WebhookId, w => w.Action = "deploy");
            return wh;
        }).ToList();
        var service = Service(planner: planner);
        _runner.Delay = TimeSpan.FromMilliseconds(100);

        var replies = await Task.WhenAll(hooks.Select((wh, i) => service.AcceptAsync(wh.WebhookId, Signed(wh, $"delivery-016{i}"))));
        foreach (var r in replies) await Finished(r.RunId!);

        Assert.Equal(3, _runner.Runs);
        Assert.Equal(1, _runner.MaxConcurrent);
    }

    [Fact]
    public async Task A_webhook_switched_off_while_its_delivery_waits_does_not_run()
    {
        _options.MaxConcurrentRuns = 2;
        var first = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var second = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var service = Service();
        _runner.Hold();
        var running = await service.AcceptAsync(first.WebhookId, Signed(first, "delivery-0121"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = await service.AcceptAsync(second.WebhookId, Signed(second, "delivery-0122"));
        await Task.Delay(100);   // the second run waits for the target

        Update(second.WebhookId, w => w.Enabled = false);
        _runner.Release();

        Assert.Equal("Succeeded", (await Finished(running.RunId!)).State);
        Assert.Equal("Refused", (await Finished(waiting.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task A_webhook_taken_over_while_its_delivery_waits_runs_on_its_owner_now()
    {
        _options.MaxConcurrentRuns = 2;
        var first = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var second = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var service = Service();
        _runner.Hold();
        await service.AcceptAsync(first.WebhookId, Signed(first, "delivery-0131"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = await service.AcceptAsync(second.WebhookId, Signed(second, "delivery-0132"));
        await Task.Delay(100);

        Update(second.WebhookId, w => w.Owner = "user:new-admin@example.com");
        _runner.Release();

        await Finished(waiting.RunId!);
        Assert.Contains("user:new-admin@example.com", _runner.Owners);
    }

    [Fact]
    public async Task A_run_that_outlives_its_time_keeps_its_target_but_not_its_worker()
    {
        _options.MaxConcurrentRuns = 1;
        // Long enough for a healthy run's own lookups on a loaded CI runner (300 ms failed there).
        _options.RunTimeout = TimeSpan.FromMilliseconds(1500);
        var stuck = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var sameTarget = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var other = Seed(WebhookSignatures.Whiskers, target: "worker");
        var service = Service();
        _runner.Hold();

        var first = await service.AcceptAsync(stuck.WebhookId, Signed(stuck, "delivery-0141"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var blocked = await service.AcceptAsync(sameTarget.WebhookId, Signed(sameTarget, "delivery-0142"));
        // The only worker comes free once the first run is past its time: the second fails to get the target in
        // time (the first still holds it), and a run on another target goes on.
        Assert.Equal("Failed", (await Finished(blocked.RunId!)).State);
        Assert.Equal("Running", (await Delivery(first.RunId!)).State);
        var elsewhere = await service.AcceptAsync(other.WebhookId, Signed(other, "delivery-0143"));
        _runner.ReleaseOthers();
        Assert.Equal("Succeeded", (await Finished(elsewhere.RunId!)).State);

        _runner.Release();
        Assert.Equal("Succeeded", (await Finished(first.RunId!)).State);
        Assert.Equal(2, _runner.Runs);
    }

    [Fact]
    public async Task A_run_past_its_time_keeps_its_target_and_worker_when_no_overdue_slot_is_free()
    {
        _options.MaxConcurrentRuns = 1;
        // Long enough for a healthy run's own lookups on a loaded CI runner (300 ms failed there).
        _options.RunTimeout = TimeSpan.FromMilliseconds(1500);
        var handedOver = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var kept = Seed(WebhookSignatures.Whiskers, target: "worker");
        var service = Service();
        _runner.Hold();

        // The first overdue run takes the only overdue slot; the second finds none free.
        var first = await service.AcceptAsync(handedOver.WebhookId, Signed(handedOver, "delivery-0144"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await service.AcceptAsync(kept.WebhookId, Signed(kept, "delivery-0145"));
        await WaitFor(() => _runner.Runs == 2);
        var third = await service.AcceptAsync(kept.WebhookId, Signed(kept, "delivery-0146"));
        await Task.Delay(3500);   // well past the second run's time

        // It is still running: its target is not released and nothing else runs on it.
        Assert.Equal("Running", (await Delivery(second.RunId!)).State);
        Assert.Equal("Queued", (await Delivery(third.RunId!)).State);
        Assert.Equal(2, _runner.Runs);

        _runner.Release();
        Assert.Equal("Succeeded", (await Finished(first.RunId!)).State);
        Assert.Equal("Succeeded", (await Finished(second.RunId!)).State);
        await Finished(third.RunId!);
        Assert.Equal(3, _runner.Runs);
    }

    [Fact]
    public async Task The_test_shows_a_workers_own_message_in_the_persons_language()
    {
        _options.MaxConcurrentRuns = 2;
        // Long enough that the run's own lookups finish under a loaded test run; the target is held throughout.
        _options.RunTimeout = TimeSpan.FromSeconds(2);
        var holder = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var tested = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var service = Service(text: new KeyLocalizer());
        _runner.Hold();
        await service.AcceptAsync(holder.WebhookId, Signed(holder, "delivery-0147"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // The target stays held past the test's run time: it does not get its turn.
        var (success, output) = await service.TriggerSignedTestAsync(tested.WebhookId);

        Assert.False(success);
        Assert.Equal("[Webhook_Run_NoTurn]", output);
    }

    // ---------------------------------------------------------------- INV-45: deliveries that end before their decision

    private IServiceScopeFactory Scopes => _sp.GetRequiredService<IServiceScopeFactory>();

    /// <summary>What a worker's read of a webhook contains.</summary>
    private const string WebhookReads = "FROM \"Webhooks\"";

    /// <summary>How long a test waits for a page Test's answer while it holds the database's lock or a blocked action.
    /// While a lock is held, SQLite busy-waits on pool threads (a start, a give-up's record, a pruning pass), so on a
    /// loaded runner the answer can take seconds to be scheduled — hence not the former 5 s.
    /// <para>What it proves, with <see cref="AssertAnsweredBeforeTheDatabase"/>: the locked start waits without a
    /// timeout until the test unlocks it, so an answer while it still waits did not wait for it (by order). Every
    /// other wait that meets the lock gives up only after a bound of its own — an audit write queued behind the audit
    /// chain's lock after 20 s (<c>MutationGateway.RefusalWriteTimeout</c>, given up without a command event; in
    /// production its event goes to the audit outbox, this test's gateway sink (<c>AuditTrail.Gateway</c>) has none and
    /// only logs it), a write already waiting in SQLite after SQLite's 30 s busy timeout — so an answer that waited for one of them comes
    /// after 20 s at the earliest: this guard, below every such bound, catches it (checked by awaiting the give-up's
    /// record before the answer: red). It does not prove more than that, and a runner that cannot schedule the
    /// answer within 15 s fails it.</para>
    /// The page Test's own wait (<see cref="WebhookOptions.TestRunTimeout"/>, 10 min) must exceed it, or a hang would
    /// read as an answer (asserted).</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    /// <summary>The run deadline of the lock tests: long enough that, on a loaded runner, the run reaches its start
    /// (and the lock) before it — nothing they prove depends on the deadline being short.</summary>
    private static readonly TimeSpan LockTestRunTimeout = TimeSpan.FromMilliseconds(2500);

    /// <summary>The answer of a page Test came while the database still held it up: the run's start had taken the
    /// lock (premise) and was still waiting for it, and no other command had failed or been cancelled — so the answer
    /// did not wait for the locked start; that it waited for no other bounded wait either rests on
    /// <see cref="HangGuard"/>.</summary>
    private void AssertAnsweredBeforeTheDatabase()
    {
        Assert.True(_options.TestRunTimeout > HangGuard, "premise: the page Test waits longer than the hang guard");
        Assert.True(_faults.LockTaken.IsCompleted,
            "premise: the run's start reached the lock before its deadline (a setup or runner problem, not the product)");
        Assert.Equal(1, _faults.Tripped);
        Assert.False(_faults.LockedCommandEnded, "the locked start ended before the answer");
        Assert.Equal(0, _faults.EndedBadlySinceArmed);
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The signal never came.");
            await Task.Delay(10);
        }
    }

    /// <summary>Refusals go through the real gateway into this test's <c>AuditLog</c> (the runs stay the recording
    /// runner's: they are not what these tests prove).</summary>
    private void RecordRefusals()
    {
        var f = AutomationMutationRunnerTests.Build(requireOwner: true, trail: Scopes);
        f.Whitelist.Allowed.Add("admin@example.com");
        f.Roles.Map["admin@example.com"] = AppRole.Admin;
        _runner.Audit = f.Runner;
    }

    /// <summary>The delivery's one event, read by its run id once it is written: Denied before any decision.</summary>
    private async Task<AuditLogEntity> NotRunAsync(string runId, string capability, bool owned = true, string? targetId = "web-1")
    {
        var rows = await AuditTrail.ForOperationAsync(Scopes, runId);
        var row = AuditTrail.One(rows, AuditTrail.Denied, capability, "Automation", owned: owned, decided: false,
            targetId: targetId);
        Assert.Equal(runId, row.OperationId);
        return row;
    }

    /// <summary>INV-45: a delivery that got no turn on its target is still one event — refused under its own run id,
    /// as the webhook's capability, on its owner's authority — and nothing runs. Its run timeout is the mechanism (the
    /// target stays held until the test releases it); the event is waited for by the run id, not by time.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_delivery_that_got_no_turn_is_on_record_once_as_not_run()
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 2;
        _options.RunTimeout = TimeSpan.FromSeconds(1);
        var holder = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var waiting = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var service = Service();
        _runner.Hold();
        await service.AcceptAsync(holder.WebhookId, Signed(holder, "delivery-0301"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));

        var accepted = await service.AcceptAsync(waiting.WebhookId, Signed(waiting, "delivery-0302"));

        Assert.Equal("Failed", (await Finished(accepted.RunId!)).State);
        var row = await NotRunAsync(accepted.RunId!, "container.restart");
        Assert.Equal("webhook:" + waiting.WebhookId, row.PrincipalId);
        Assert.Equal("user:admin@example.com", row.OnBehalfOfId);
        Assert.Equal(1, _runner.Runs);   // only the holder ran
        _runner.Release();
    }

    /// <summary>INV-45: a webhook switched off, or removed, while its delivery waited — one Denied event under the
    /// run id, as the delivery it was accepted for; nothing runs.</summary>
    [Theory]
    [InlineData("switched off")]
    [InlineData("removed")]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_delivery_whose_webhook_changed_while_it_waited_is_on_record_once_as_not_run(string change)
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 2;
        _options.RunTimeout = TimeSpan.FromSeconds(20);
        var holder = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var waiting = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var service = Service();
        _runner.Hold();
        await service.AcceptAsync(holder.WebhookId, Signed(holder, "delivery-0303"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var reads = _faults.RunCommands(WebhookReads);
        var accepted = await service.AcceptAsync(waiting.WebhookId, Signed(waiting, "delivery-0304"));
        // The waiting run has read its webhook (a signal, not a time) and waits for the target, which the holder keeps.
        await Until(() => _faults.RunCommands(WebhookReads) > reads);

        if (change == "removed") Remove(waiting.WebhookId);
        else Update(waiting.WebhookId, w => w.Enabled = false);
        _runner.Release();

        Assert.Equal("Refused", (await Finished(accepted.RunId!)).State);
        var refusal = await NotRunAsync(accepted.RunId!, "container.restart");
        Assert.Equal("webhook:" + waiting.WebhookId, refusal.PrincipalId);
        // Keyed as the worker's own refusal (round 6, MINOR-1), not as a delivery that did not run.
        Assert.Equal(Whiskers.Services.Mutations.AuditLogMutationSink.KeyedEventId(WebhookService.RefusedEventKey(accepted.RunId!)),
            refusal.EventId);
        Assert.Equal(1, _runner.Runs);
    }

    /// <summary>INV-45 (review B2): a webhook removed before the worker read it at all — both reads find nothing — is
    /// still one event: by the delivery's webhook id, unowned, under <see cref="WebhookService.RemovedWebhook"/>.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_delivery_whose_webhook_was_gone_before_its_worker_read_it_is_on_record_once()
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 1;
        _options.RunTimeout = TimeSpan.FromSeconds(20);
        var holder = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var gone = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var service = Service();
        _runner.Hold();
        await service.AcceptAsync(holder.WebhookId, Signed(holder, "delivery-0305"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        // Queued behind the only worker; removed before that worker reads it.
        var accepted = await service.AcceptAsync(gone.WebhookId, Signed(gone, "delivery-0306"));
        Remove(gone.WebhookId);
        _runner.Release();

        await Finished(accepted.RunId!);
        var row = await NotRunAsync(accepted.RunId!, WebhookService.RemovedWebhook, owned: false, targetId: "");
        Assert.Equal("unowned:webhook:" + gone.WebhookId, row.PrincipalId);
        Assert.Equal(1, _runner.Runs);
    }

    /// <summary>INV-45 (review B2): a run that was started and then failed before anything recorded it (the second read
    /// of its webhook fails) is given up as failed — and on record once, as a delivery that did not run.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_started_run_given_up_before_its_decision_is_on_record_once()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        // The worker's first read of the webhook passes, the one after the start fails.
        _faults.Arm("FROM \"Webhooks\"", Fault.Fail, runsOnly: true, skip: 1);

        var accepted = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0307"));

        Assert.Equal("Failed", (await Finished(accepted.RunId!)).State);
        await NotRunAsync(accepted.RunId!, "container.restart");
        Assert.Equal(0, _runner.Runs);
    }

    /// <summary>INV-45 (review B2): a start that lands after the run's deadline (a database lock) is recorded as
    /// failed — and the delivery, which never ran, is on record once.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_start_that_landed_after_the_deadline_is_on_record_once()
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 1;
        _options.RunTimeout = LockTestRunTimeout;
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.LockDatabase, runsOnly: true, skip: 1);   // after its acceptance, the first run write

        var (success, _) = await service.TriggerSignedTestAsync(wh.WebhookId).WaitAsync(HangGuard);
        // Answered while its start still waited for the lock: the start lands only after the deadline.
        AssertAnsweredBeforeTheDatabase();
        Assert.False(success);
        _faults.Unlock();
        string runId;
        using (var scope = _sp.CreateScope())
            runId = (await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries.SingleAsync()).RunId;

        Assert.Equal("Failed", (await Finished(runId)).State);
        await NotRunAsync(runId, "container.restart");
        Assert.Equal(0, _runner.Runs);
    }

    /// <summary>INV-45 (review round 3, BLOCKER-1): a delivery that got no turn, whose settlement stalls past its
    /// bookkeeping time (a locked database), is still exactly one event: it is recorded once, before it is settled, and
    /// the settlement is tried again until it lands. It ends failed, in the webhook's log too.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_no_turn_delivery_whose_bookkeeping_stalls_is_still_one_event()
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 2;
        _options.RunTimeout = TimeSpan.FromSeconds(1);
        var holder = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var waiting = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var service = Service();
        service.BookkeepingTimeout = TimeSpan.FromSeconds(1);
        service.SettleRetryDelay = TimeSpan.FromMilliseconds(50);
        _runner.Hold();
        await service.AcceptAsync(holder.WebhookId, Signed(holder, "delivery-0311"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        // The waiting run's settlement hangs past its bookkeeping time once (its acceptance, the first run write, passes).
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.Stall, runsOnly: true, skip: 1);

        var accepted = await service.AcceptAsync(waiting.WebhookId, Signed(waiting, "delivery-0312"));

        Assert.Equal("Failed", (await Finished(accepted.RunId!)).State);
        Assert.Equal(1, _faults.Tripped);   // the settlement really stalled once, and was tried again
        // Recorded before it was settled: nothing of it can come later.
        var rows = (await AuditTrail.RowsAsync(Scopes)).Where(r => r.OperationId == accepted.RunId).ToList();
        AuditTrail.One(rows, AuditTrail.Denied, "container.restart", "Automation", decided: false, targetId: "web-1");
        await Until(() => Logs(waiting.WebhookId).Any(l => !l.Success));
        Assert.Equal(1, _runner.Runs);
        _runner.Release();
    }

    /// <summary>Review rounds 3 and 5: the claim fences every late write under a claim that was let go — once a repeat
    /// took the row over, neither a start, a settlement nor a finish under the old claim touches that run. In the
    /// service the only accepted row it lets go unsettled is one whose acceptance answer was lost (answered busy); a run
    /// its worker gave up keeps its claim until it is settled or the process ends.</summary>
    [Fact]
    public async Task A_claim_that_was_let_go_never_touches_the_run_that_took_it_over()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var store = new WebhookDeliveryStore(Scopes, _time);
        var (fresh, row) = await store.RecordAsync(wh.WebhookId, "key-0313", CancellationToken.None);
        Assert.True(fresh);
        await store.AcceptAsync(row.RunId, row.Instance, CancellationToken.None);
        var givenUp = row.Instance;
        store.Release(givenUp);   // accepted, its answer lost: the delivery was told busy and its claim let go

        var (again, taken) = await store.RecordAsync(wh.WebhookId, "key-0313", CancellationToken.None);
        Assert.True(again);
        Assert.Equal(row.RunId, taken.RunId);
        await store.AcceptAsync(taken.RunId, taken.Instance, CancellationToken.None);
        Assert.True(await store.StartAsync(taken.RunId, taken.Instance, CancellationToken.None));

        Assert.False(await store.StartAsync(row.RunId, givenUp, CancellationToken.None));
        Assert.False(await store.SettleAsync(row.RunId, givenUp, CancellationToken.None));
        Assert.False(await store.FinishAsync(row.RunId, givenUp, "Failed", CancellationToken.None));
        Assert.Equal("Running", (await Delivery(row.RunId)).State);
    }

    /// <summary>INV-45 (review round 5, MAJOR-1): a no-turn give-up is recorded, its settlement fails, and Whiskers
    /// begins to stop before it is retried. Its claim stays held: a repeat that still arrives is a duplicate (never
    /// accepted, never run), no second event is written, and the next start only settles and logs it.</summary>
    [Fact]
    public async Task A_give_up_cut_off_by_a_stop_is_never_taken_over_and_stays_one_event()
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 2;
        _options.RunTimeout = TimeSpan.FromSeconds(1);
        var holder = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var waiting = Seed(WebhookSignatures.Whiskers, target: "web-1");
        using var stop = new CancellationTokenSource();
        var service = Service(stop: stop);
        service.SettleRetryDelay = TimeSpan.FromHours(1);   // the retry would come only after the stop
        var returned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StoppedUnsettled += runId => returned.TrySetResult(runId);
        _runner.Hold();
        var held = await service.AcceptAsync(holder.WebhookId, Signed(holder, "delivery-0314"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        // The give-up's settlement fails (its acceptance, the first run write, passes).
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.Fail, runsOnly: true, skip: 1);
        var request = Signed(waiting, "delivery-0315");
        var accepted = await service.AcceptAsync(waiting.WebhookId, request);
        await Until(() => _faults.Tripped == 1);
        await NotRunAsync(accepted.RunId!, "container.restart");

        stop.Cancel();
        // The give-up has returned unsettled — past the point where it used to let its claim go.
        Assert.Equal(accepted.RunId, await returned.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var repeat = await service.AcceptAsync(waiting.WebhookId, request);

        Assert.Equal(WebhookReply.Duplicate, repeat.Reply);
        Assert.Equal(accepted.RunId, repeat.RunId);
        Assert.Equal("Queued", (await Delivery(accepted.RunId!)).State);
        _runner.Release();
        await Finished(held.RunId!);   // the holder ends: only the given-up delivery is left behind
        Assert.Equal(0, await Service().ReconcileAsync());   // the next start: its event is there, none is added
        Assert.Equal("Failed", (await Delivery(accepted.RunId!)).State);
        Assert.Single(await AuditTrail.RowsAsync(Scopes), r => r.OperationId == accepted.RunId);
        Assert.Single(Logs(waiting.WebhookId), l => !l.Success);
        Assert.Equal(1, _runner.Runs);   // the holder's
    }

    /// <summary>INV-45 (review round 6, MINOR-3 and NIT-3): a worker that is busy when Whiskers stops takes no further
    /// queued run. The run it would have taken stays queued under its live claim and never runs; a page's Test waiting
    /// for it is told at once that Whiskers is stopping; the next start records it once as not run.</summary>
    [Fact]
    public async Task A_worker_busy_at_a_stop_starts_no_further_queued_run()
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 1;
        var first = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var second = Seed(WebhookSignatures.Whiskers, target: "web-2");
        using var stop = new CancellationTokenSource();
        var service = Service(stop: stop);
        _runner.Hold();
        var running = await service.AcceptAsync(first.WebhookId, Signed(first, "delivery-0322"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        // Waited for in the process, once the run is in the queue — not by its database state, which is written before
        // (review round 7, MINOR-2).
        var enqueued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RunEnqueued += runId => enqueued.TrySetResult(runId);
        var test = service.TriggerSignedTestAsync(second.WebhookId);
        var queuedRunId = await enqueued.Task.WaitAsync(TimeSpan.FromSeconds(10));

        stop.Cancel();

        // Told at the stop; the queued run never starts. (The running action is handed over at the stop — an overdue
        // slot is free — so its worker comes back; that every worker may stay busy is pinned by
        // Every_test_waiting_at_a_stop_is_told_at_once, review round 9, NIT-1.)
        var (success, output) = await test.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(success);
        Assert.Equal(WebhookService.Stopping, output);
        Assert.Equal(1, _runner.Runs);
        _runner.Release();
        await Finished(running.RunId!);
        Assert.Equal(1, _runner.Runs);   // only the run that was already running
        Assert.Equal("Queued", (await Delivery(queuedRunId)).State);

        Assert.Equal(1, await Service().ReconcileAsync());   // the next start
        Assert.Equal("Failed", (await Delivery(queuedRunId)).State);
        await NotRunAsync(queuedRunId, "container.restart", targetId: "web-2");
        Assert.Equal(1, _runner.Runs);
    }

    /// <summary>Review rounds 7 and 8 (MINOR-1): at a stop every page Test waiting for a run is told at once, also while
    /// every worker is busy. A stop cuts a running action loose from its worker while an overdue slot is free, so the
    /// two overdue slots are taken first (two actions past their time, handed over); then both workers hold an action
    /// that keeps them whatever the stop (no overdue slot free), and two Tests wait in the queue. At the stop both are
    /// told that Whiskers is stopping before any held action ends — no worker comes back for them. Their rows stay
    /// queued and the next start records each once as not run; neither runs. A Test triggered after the stop is not
    /// accepted: nothing is recorded, nothing runs (round 8, NIT-1).</summary>
    [Fact]
    public async Task Every_test_waiting_at_a_stop_is_told_at_once()
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 2;
        // Long enough for a healthy run's own lookups on a loaded CI runner (as in the overdue tests above).
        _options.RunTimeout = TimeSpan.FromMilliseconds(1500);
        var holders = Enumerable.Range(1, 4).Select(i => Seed(WebhookSignatures.Whiskers, target: $"web-{i}")).ToArray();
        var firstTest = Seed(WebhookSignatures.Whiskers, target: "web-5");
        var secondTest = Seed(WebhookSignatures.Whiskers, target: "web-5");
        var lateTest = Seed(WebhookSignatures.Whiskers, target: "web-5");
        using var stop = new CancellationTokenSource();
        var service = Service(stop: stop);
        _runner.Hold();
        var held = new List<string>();
        // The first two run past their time and are handed over: both overdue slots taken, both workers free again.
        foreach (var (wh, i) in holders.Take(2).Select((w, i) => (w, i)))
            held.Add((await service.AcceptAsync(wh.WebhookId, Signed(wh, $"delivery-03{25 + i}"))).RunId!);
        await Until(() => _runner.Runs == 2);
        // The next two take both workers and keep them: no overdue slot is free for them.
        foreach (var (wh, i) in holders.Skip(2).Select((w, i) => (w, i)))
            held.Add((await service.AcceptAsync(wh.WebhookId, Signed(wh, $"delivery-03{27 + i}"))).RunId!);
        await Until(() => _runner.Runs == 4);   // both workers busy
        var enqueued = new System.Collections.Concurrent.ConcurrentQueue<string>();
        service.RunEnqueued += enqueued.Enqueue;
        var first = service.TriggerSignedTestAsync(firstTest.WebhookId);
        var second = service.TriggerSignedTestAsync(secondTest.WebhookId);
        await Until(() => enqueued.Count == 2);

        stop.Cancel();

        // Both answered while every held action still runs and keeps its worker.
        var answers = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([(false, WebhookService.Stopping), (false, WebhookService.Stopping)], answers);
        Assert.Equal(4, _runner.Runs);
        var queued = enqueued.ToArray();
        foreach (var runId in queued) Assert.Equal("Queued", (await Delivery(runId)).State);

        // Triggered after the stop: not accepted, not recorded, not run.
        Assert.Equal((false, WebhookService.StoppingNotAccepted),
            await service.TriggerSignedTestAsync(lateTest.WebhookId).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, enqueued.Count);
        using (var scope = _sp.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries
                .CountAsync(d => d.WebhookId == lateTest.WebhookId));

        _runner.Release();
        foreach (var runId in held) await Finished(runId);
        Assert.Equal(4, _runner.Runs);

        Assert.Equal(2, await Service().ReconcileAsync());   // the next start: each queued Test once
        foreach (var runId in queued)
        {
            Assert.Equal("Failed", (await Delivery(runId)).State);
            await NotRunAsync(runId, "container.restart", targetId: "web-5");
        }
        Assert.Equal(4, _runner.Runs);
    }

    /// <summary>Review round 8 (NIT-1): while Whiskers stops, a new delivery is answered busy and not recorded — the
    /// provider may deliver it again to the next start — although the queue has room; the drain of the queue at the stop
    /// does not lift its limit. A delivery seen before is still answered as its duplicate.</summary>
    [Fact]
    public async Task A_delivery_while_stopping_is_busy_and_not_recorded()
    {
        _options.QueueCapacity = 4;
        var wh = Seed(WebhookSignatures.Whiskers, target: "web-1");
        using var stop = new CancellationTokenSource();
        var service = Service(stop: stop);
        _runner.Hold();
        var before = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0327"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));

        stop.Cancel();
        var during = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0328"));
        var repeat = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0327"));

        Assert.Equal(WebhookReply.Busy, during.Reply);
        Assert.Null(during.RunId);
        Assert.Equal(WebhookReply.Duplicate, repeat.Reply);
        Assert.Equal(before.RunId, repeat.RunId);
        using (var scope = _sp.CreateScope())
            Assert.Equal([before.RunId], await scope.ServiceProvider.GetRequiredService<MetricsDbContext>()
                .WebhookDeliveries.Where(d => d.WebhookId == wh.WebhookId).Select(d => d.RunId).ToListAsync());
        _runner.Release();
        await Finished(before.RunId!);
        Assert.Equal(1, _runner.Runs);
    }

    /// <summary>INV-45 (review round 6, MINOR-3): the interleaving the key closes — the next start's reconciliation
    /// records a delivery as not run while the give-up's own copy of that event is still kept in the audit outbox
    /// (its database write failed and the pass did not see it), then the audit recovery drains the outbox: one row,
    /// and the outbox is empty.</summary>
    [Fact]
    public async Task A_not_run_event_reconciled_and_then_drained_from_the_outbox_is_one_row()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        SeedDelivery(wh, Signed(wh, "delivery-0324"), "kept-then-drained", "Queued");
        Assert.Equal(1, await Service().ReconcileAsync());
        // The give-up's copy, written by the real sink under the same key while the database is unavailable to it:
        // its insert fails (here on the unique event id) and so does the read-back, so the sink keeps it in the outbox
        // (review round 7, NIT-2).
        _faults.Arm(".\"EventId\" = @", Fault.Fail);
        var sink = new AuditLogMutationSink(Scopes, outbox: _outbox);
        var giveUp = new MutationRequest("container.restart",
            new MutationPrincipal(MutationPrincipalKind.Automation, "webhook:" + wh.WebhookId, "Webhook",
                McpPermissionLevels.Write),
            MutationSurface.Automation, new MutationTarget("container", "web-1"),
            new Dictionary<string, System.Text.Json.JsonElement>(), OperationId: "kept-then-drained")
        {
            EventKey = WebhookService.NotRunEventKey("kept-then-drained"),
        };
        await Assert.ThrowsAnyAsync<Exception>(() => sink.RecordDeniedAsync("kept-then-drained", giveUp, null,
            MutationStatus.Denied, WebhookService.GivenUp, CancellationToken.None));
        Assert.Equal(1, _faults.Tripped);
        Assert.Equal(WebhookService.NotRunEventId("kept-then-drained"), Assert.Single(_outbox.Load()).Row!.EventId);

        var recovery = new AuditRecovery(new AuditLogMutationSink(Scopes, outbox: _outbox), Scopes,
            NullLogger<AuditRecovery>.Instance, outbox: _outbox);
        await recovery.RecoverAsync();

        Assert.Single(await AuditTrail.RowsAsync(Scopes), r => r.OperationId == "kept-then-drained");
        Assert.Equal(0, _outbox.Count);
        Assert.Equal("Failed", (await Delivery("kept-then-drained")).State);
    }

    /// <summary>What a settlement's SQL contains, and no other delivery write: it ends a queued or running run.</summary>
    private static readonly string[] Settlements = ["UPDATE \"WebhookDeliveries\"", "FinishedAt", "'Queued'"];

    /// <summary>INV-45 (review round 5, MINOR-5): a settlement that hangs and ignores its cancellation (as SQLite does)
    /// is waited for again, never joined by a second one — exactly one settle write while it hangs; it then lands.</summary>
    [Fact]
    public async Task A_settlement_that_hangs_is_never_joined_by_a_second_one()
    {
        RecordRefusals();
        _options.MaxConcurrentRuns = 2;
        _options.RunTimeout = TimeSpan.FromSeconds(1);
        var holder = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var waiting = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var service = Service();
        service.BookkeepingTimeout = TimeSpan.FromMilliseconds(500);
        service.SettleRetryDelay = TimeSpan.FromMilliseconds(20);
        _runner.Hold();
        await service.AcceptAsync(holder.WebhookId, Signed(holder, "delivery-0316"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.StallIgnoringCancellation, runsOnly: true, skip: 1);
        var before = _faults.RunWrites(Settlements);

        var accepted = await service.AcceptAsync(waiting.WebhookId, Signed(waiting, "delivery-0317"));
        await Until(() => _faults.Tripped == 1);
        await Task.Delay(TimeSpan.FromSeconds(2));   // several bookkeeping times and many retry periods

        // The one hanging settlement: no second one was issued meanwhile.
        Assert.Equal(before + 1, _faults.RunWrites(Settlements));
        Assert.Equal("Queued", (await Delivery(accepted.RunId!)).State);
        _faults.Unstall();
        Assert.Equal("Failed", (await Finished(accepted.RunId!)).State);
        await NotRunAsync(accepted.RunId!, "container.restart");
        _runner.Release();
    }

    /// <summary>INV-45 (review round 5, MINOR-5): an acceptance whose write fails leaves the delivery unaccepted — it is
    /// answered busy, and its next delivery takes it over and runs it once.</summary>
    [Fact]
    public async Task An_acceptance_that_could_not_be_written_is_busy_and_the_next_delivery_runs_it_once()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0318");
        var service = Service();
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.Fail, runsOnly: true);

        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), await service.AcceptAsync(wh.WebhookId, request));
        Assert.Equal(1, _faults.Tripped);
        using (var scope = _sp.CreateScope())
            Assert.Equal("Recording", scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries
                .AsNoTracking().Single(d => d.WebhookId == wh.WebhookId).State);

        var again = await service.AcceptAsync(wh.WebhookId, request);
        Assert.Equal(WebhookReply.Accepted, again.Reply);
        Assert.Equal("Succeeded", (await Finished(again.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
    }

    /// <summary>INV-45 (review round 5, MINOR-5): an acceptance whose answer is lost is read back — it was written, so
    /// the delivery is accepted and runs once.</summary>
    [Fact]
    public async Task An_acceptance_whose_answer_was_lost_is_read_back_as_accepted()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.LoseAnswer, runsOnly: true);

        var accepted = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0319"));

        Assert.Equal(1, _faults.Tripped);
        Assert.Equal(WebhookReply.Accepted, accepted.Reply);
        Assert.Equal("Succeeded", (await Finished(accepted.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
    }

    /// <summary>INV-45 (review round 5, MINOR-1): the "did not run" event is keyed by its run id — a copy that arrives
    /// later from the audit outbox (the drain moved it while a pass read) finds the row the pass wrote and adds none.</summary>
    [Fact]
    public async Task A_not_run_event_written_twice_is_one_row()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        SeedDelivery(wh, Signed(wh, "delivery-0320"), "drained-meanwhile", "Queued");
        Assert.Equal(1, await Service().ReconcileAsync());

        using (var scope = _sp.CreateScope())
        {
            var copy = new AuditLogEntity
            {
                EventId = WebhookService.NotRunEventId("drained-meanwhile"), OperationId = "drained-meanwhile",
                Phase = "outcome", Outcome = nameof(MutationStatus.Denied), Action = "container.restart",
            };
            await AuditLogMutationSink.InsertOnceAsync(scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), copy,
                Whiskers.Services.AuditLog.AuditChain.Of(scope.ServiceProvider), CancellationToken.None);
        }

        Assert.Equal(WebhookService.NotRunEventId("drained-meanwhile"),
            (await NotRunAsync("drained-meanwhile", "container.restart")).EventId);
    }

    /// <summary>INV-45 (review round 5, MINOR-2): a webhook read that fails during the reconciliation skips the row —
    /// nothing is recorded as a removed webhook — and a later pass records it on its owner, as its capability.</summary>
    [Fact]
    public async Task A_webhook_read_that_fails_is_tried_again_not_recorded_as_removed()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        SeedDelivery(wh, Signed(wh, "delivery-0321"), "read-failed", "Queued");
        var service = Service();
        service.ReconcileRetryDelay = TimeSpan.FromHours(1);
        _faults.Arm(WebhookReads, Fault.Fail, runsOnly: true);
        using var start = new CancellationTokenSource();

        Assert.Equal(0, await service.ReconcileAsync(start.Token));
        Assert.Equal(1, _faults.Tripped);
        Assert.DoesNotContain(await AuditTrail.RowsAsync(Scopes), r => r.OperationId == "read-failed");
        Assert.Equal("Queued", (await Delivery("read-failed")).State);

        Assert.Equal(1, await service.ReconcileAsync());
        Assert.Contains(WebhookService.NotSettled, (await NotRunAsync("read-failed", "container.restart")).Details);
        Assert.Equal("Failed", (await Delivery("read-failed")).State);
    }

    /// <summary>INV-45 (review round 3, MAJOR-1): what an earlier process accepted and left unsettled — runs still
    /// waiting at a stop or crash, a give-up it could not settle — is recorded once at the next start and settled:
    /// queued or running without an event → one Denied event, failed; queued or running with its "did not run" event
    /// (a give-up recorded before the stop) → failed, no second event; running with only the gateway's refusal →
    /// refused, not logged again; running with its intent on record (cut off while it ran) → left as it is. Every one
    /// settled as not run is in the webhook's log. A second pass, and another start, record nothing.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task Deliveries_an_earlier_process_left_unsettled_are_recorded_once_at_the_next_start()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        string Row(string state)
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var row = new WebhookDeliveryEntity
            {
                WebhookId = wh.WebhookId, DeliveryKey = Guid.NewGuid().ToString("N"), RunId = Guid.NewGuid().ToString("N"),
                State = state, Instance = "stopped-process/" + Guid.NewGuid().ToString("N"),
                ReceivedAt = _time.GetUtcNow().UtcDateTime,
            };
            db.WebhookDeliveries.Add(row);
            db.SaveChanges();
            return row.RunId;
        }
        // A give-up's "did not run" event (keyed), the worker's own keyed refusal (its webhook changed while it waited),
        // or the gateway's refusal of a run that had started.
        async Task OnRecordAsync(string runId, bool givenUp, bool changed = false) => await _runner.Audit!.RefuseAsync(
            WebhookService.ActorOf(wh) with
            {
                OperationId = runId,
                EventKey = givenUp ? WebhookService.NotRunEventKey(runId)
                    : changed ? WebhookService.RefusedEventKey(runId) : null,
            }, "container.restart", new { containerId = "web-1", serverId = "local" }, "recorded before the stop");
        var queued = Row("Queued");
        var running = Row("Running");
        var queuedGivenUp = Row("Queued");
        var runningGivenUp = Row("Running");
        var runningRefused = Row("Running");
        // Refused by its worker (the webhook changed while it waited); its finish failed, its log entry was written.
        var runningChanged = Row("Running");
        var runningCutOff = Row("Running");
        await OnRecordAsync(queuedGivenUp, givenUp: true);
        await OnRecordAsync(runningGivenUp, givenUp: true);
        await OnRecordAsync(runningRefused, givenUp: false);
        await OnRecordAsync(runningChanged, givenUp: false, changed: true);
        using (var scope = _sp.CreateScope())
        {
            // Its intent is on record: it was running when the process stopped.
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.AuditLog.Add(new AuditLogEntity
            {
                EventId = Guid.NewGuid().ToString("N"), OperationId = runningCutOff, Phase = "intent",
                Action = "container.restart", Surface = "Automation",
            });
            db.SaveChanges();
        }

        var service = Service();
        Assert.Equal(2, await service.ReconcileAsync());

        foreach (var runId in new[] { queued, running })
        {
            Assert.Equal("Failed", (await Delivery(runId)).State);
            Assert.Contains(WebhookService.NotSettled, (await NotRunAsync(runId, "container.restart")).Details);
        }
        // Given up before the stop and not settled: settled failed, no second event, logged now.
        Assert.Equal("Failed", (await Delivery(queuedGivenUp)).State);
        Assert.Equal("Failed", (await Delivery(runningGivenUp)).State);
        // Refused by the gateway (its run's own bookkeeping logged it): refused, nothing more (round 5, MINOR-4).
        Assert.Equal("Refused", (await Delivery(runningRefused)).State);
        // Its own key kind (round 6, MINOR-1): refused like any refusal — not failed, not logged or notified again.
        Assert.Equal("Refused", (await Delivery(runningChanged)).State);
        Assert.Equal("Running", (await Delivery(runningCutOff)).State);   // read as "interrupted"
        foreach (var runId in new[] { queuedGivenUp, runningGivenUp, runningRefused, runningChanged, runningCutOff })
            Assert.Single(await AuditTrail.RowsAsync(Scopes), r => r.OperationId == runId);

        var before = (await AuditTrail.RowsAsync(Scopes)).Count;
        Assert.Equal(0, await service.ReconcileAsync());
        Assert.Equal(0, await Service().ReconcileAsync());
        Assert.Equal(before, (await AuditTrail.RowsAsync(Scopes)).Count);
        // The two recorded now and the two given up — not the two refused ones, not the one cut off.
        Assert.Equal(4, Logs(wh.WebhookId).Count(l => !l.Success));
        Assert.Equal(0, _runner.Runs);
    }

    /// <summary>INV-45 (review round 4, MAJOR-1): the reconciliation records a left-behind delivery and then cannot
    /// settle it. The row is not lost and not taken over: a repeat is busy, a later pass settles it without a second
    /// event, then the repeat is a duplicate of the failed run — and it never runs. (That the later pass comes without
    /// being asked is <see cref="A_row_the_reconciliation_could_not_take_does_not_stop_the_rows_after_it"/>; here it is
    /// started by hand, so no timing decides what the repeat sees — review round 5, MINOR-6.)</summary>
    [Fact]
    public async Task A_reconciliation_that_could_not_settle_is_retried_and_its_delivery_never_runs()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0009");
        SeedDelivery(wh, request, "settle-failed", "Queued");
        var service = Service();
        service.ReconcileRetryDelay = TimeSpan.FromHours(1);
        // The pass's own writes carry its token; the first is the adoption, the second the settlement.
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.Fail, runsOnly: true, skip: 1);
        using var start = new CancellationTokenSource();

        Assert.Equal(1, await service.ReconcileAsync(start.Token));
        Assert.Equal(1, _faults.Tripped);
        Assert.Equal("Queued", (await Delivery("settle-failed")).State);
        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), await service.AcceptAsync(wh.WebhookId, request));

        Assert.Equal(0, await service.ReconcileAsync());   // the later pass: its event is there, none is added
        Assert.Equal("Failed", (await Delivery("settle-failed")).State);
        Assert.Equal(new WebhookAcceptance(WebhookReply.Duplicate, "settle-failed", "failed"),
            await service.AcceptAsync(wh.WebhookId, request));
        await NotRunAsync("settle-failed", "container.restart");
        Assert.Single(Logs(wh.WebhookId), l => !l.Success);
        Assert.Equal(0, _runner.Runs);
    }

    /// <summary>INV-45 (review round 4, MAJOR-1): a row that fails is skipped, not the pass — the rows after it are
    /// reconciled at once, and the retry reconciles the one that failed. Each is one event.</summary>
    [Fact]
    public async Task A_row_the_reconciliation_could_not_take_does_not_stop_the_rows_after_it()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        SeedDelivery(wh, Signed(wh, "delivery-0010"), "first-fails", "Queued");
        SeedDelivery(wh, Signed(wh, "delivery-0011"), "second-settles", "Queued");
        var service = Service();
        service.ReconcileRetryDelay = TimeSpan.FromMilliseconds(20);
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.Fail, runsOnly: true);
        using var start = new CancellationTokenSource();

        Assert.Equal(1, await service.ReconcileAsync(start.Token));
        Assert.Equal(1, _faults.Tripped);
        Assert.Equal("Failed", (await Delivery("second-settles")).State);

        await Until(() => Logs(wh.WebhookId).Count(l => !l.Success) == 2);   // written after each settlement
        Assert.Equal("Failed", (await Delivery("first-fails")).State);
        foreach (var runId in new[] { "first-fails", "second-settles" })
            Assert.Contains(WebhookService.NotSettled, (await NotRunAsync(runId, "container.restart")).Details);
        Assert.Equal(2, Logs(wh.WebhookId).Count(l => !l.Success));
        Assert.Equal(0, _runner.Runs);
    }

    /// <summary>INV-45 (review round 4, MINOR-5): the start reconciles — the Webhooks module's initialisation (run
    /// before Kestrel listens) settles what an earlier process left queued.</summary>
    [Fact]
    public async Task The_webhooks_module_reconciles_at_start()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0012");
        SeedDelivery(wh, request, "left-at-stop", "Queued");
        var service = Service();

        await new Whiskers.Modules.Webhooks.WebhooksModule().InitializeAsync(new WithWebhooks(_sp, service), CancellationToken.None);

        Assert.Equal("Failed", (await Delivery("left-at-stop")).State);
        await NotRunAsync("left-at-stop", "container.restart");
        Assert.Equal(new WebhookAcceptance(WebhookReply.Duplicate, "left-at-stop", "failed"),
            await service.AcceptAsync(wh.WebhookId, request));
        Assert.Equal(0, _runner.Runs);
    }

    private sealed class WithWebhooks(IServiceProvider inner, IWebhookService webhooks) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IWebhookService) ? webhooks : inner.GetService(serviceType);
    }

    /// <summary>INV-45 (review round 4, MINOR-5): a give-up's "did not run" event still in the audit outbox
    /// (written there while the database was unavailable, not yet in the log) counts — the reconciliation settles and
    /// logs the row without a second event.</summary>
    [Fact]
    public async Task A_refusal_kept_in_the_outbox_is_not_recorded_twice_by_the_reconciliation()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        SeedDelivery(wh, Signed(wh, "delivery-0013"), "kept-in-outbox", "Queued");
        Assert.True(_outbox.TryKeep(new AuditLogEntity
        {
            EventId = WebhookService.NotRunEventId("kept-in-outbox"), OperationId = "kept-in-outbox", Phase = "outcome",
            Outcome = nameof(MutationStatus.Denied), Action = "container.restart", Success = false,
        }));

        Assert.Equal(0, await Service().ReconcileAsync());

        Assert.DoesNotContain(await AuditTrail.RowsAsync(Scopes), r => r.OperationId == "kept-in-outbox");
        Assert.Equal("Failed", (await Delivery("kept-in-outbox")).State);
        Assert.Single(Logs(wh.WebhookId), l => !l.Success);
        Assert.Equal(0, _runner.Runs);
    }

    private List<WebhookLogEntity> Logs(string webhookId)
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookLogs.AsNoTracking()
            .Where(l => l.WebhookId == webhookId).ToList();
    }

    private void Remove(string webhookId)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.Webhooks.Remove(db.Webhooks.Single(w => w.WebhookId == webhookId));
        db.SaveChanges();
    }

    [Fact]
    public async Task The_test_shows_a_cancelled_action_in_the_persons_language()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service(text: new KeyLocalizer());
        _runner.Cancel = true;

        var (success, output) = await service.TriggerSignedTestAsync(wh.WebhookId);

        Assert.False(success);
        Assert.Equal("[Webhook_Run_Cancelled]", output);

        // As the gateway reports it: a failed outcome carrying the cancellation.
        _runner.Cancel = false;
        _runner.CancelLikeGateway = true;
        (success, output) = await service.TriggerSignedTestAsync(wh.WebhookId);
        Assert.False(success);
        Assert.Equal("[Webhook_Run_Cancelled]", output);
    }

    // ---------------------------------------------------------------- database faults

    /// <summary>INV-45 (review M-1): a run its worker gives up before it started (its first read of the webhook fails)
    /// is not left queued for a redelivery the provider, told "accepted", never sends: it ends failed and final, and is
    /// on record once — one Denied event under its run id on the owner's authority. While it is being given up and
    /// after, a repeat of the delivery is that run, never a new chance to run it; nothing runs.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_run_its_worker_gave_up_before_it_started_is_final_and_on_record_once()
    {
        RecordRefusals();
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        var request = Signed(wh, "delivery-0171");
        _faults.Arm(WebhookReads, Fault.Fail, runsOnly: true);

        var first = await service.AcceptAsync(wh.WebhookId, request);
        Assert.Equal(WebhookReply.Accepted, first.Reply);
        for (var i = 0; i < 20 && (await Delivery(first.RunId!)).State is "Queued" or "Running"; i++)
            Assert.Equal(WebhookReply.Duplicate, (await service.AcceptAsync(wh.WebhookId, request)).Reply);

        Assert.Equal("Failed", (await Finished(first.RunId!)).State);
        var row = await NotRunAsync(first.RunId!, "container.restart");
        Assert.Contains(WebhookService.GivenUp, row.Details);
        // The Webhooks page shows it: one failed entry in the webhook's log (review round 3, MINOR-4).
        await Until(() => Logs(wh.WebhookId).Count(l => !l.Success && l.Error!.Contains("given up", StringComparison.Ordinal)) == 1);
        Assert.Equal("user:admin@example.com", row.OnBehalfOfId);
        Assert.Equal(new WebhookAcceptance(WebhookReply.Duplicate, first.RunId, "failed"),
            await service.AcceptAsync(wh.WebhookId, request));
        Assert.Equal(0, _runner.Runs);
    }

    /// <summary>Review M-1, the race: the run's start lands, but its answer reaches the worker only after the deadline,
    /// so the worker never learns it started. Giving it up settles it by what the database says — running under its
    /// claim, so failed by this call — and it is on record once.</summary>
    [Fact]
    [AuditPath("S-04", AuditTrail.Denied)]
    public async Task A_start_that_landed_unseen_before_the_deadline_is_on_record_once()
    {
        RecordRefusals();
        _options.RunTimeout = TimeSpan.FromMilliseconds(500);
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        // The start is written; its answer is held past the deadline.
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.HoldAnswer, runsOnly: true, skip: 1);   // after its acceptance, the first run write

        var accepted = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0179"));
        await _faults.Holding.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Failed", (await Finished(accepted.RunId!)).State);
        _faults.Resume();
        Assert.Contains(WebhookService.GivenUp, (await NotRunAsync(accepted.RunId!, "container.restart")).Details);
        Assert.Equal(0, _runner.Runs);
    }

    [Fact]
    public async Task A_delivery_whose_recording_answer_was_lost_is_taken_over_by_its_next_delivery()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        var request = Signed(wh, "delivery-0172");
        // The row is written, but the answer never arrives: the delivery is refused as busy.
        _faults.Arm("INSERT INTO \"WebhookDeliveries\"", Fault.LoseAnswer);

        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), await service.AcceptAsync(wh.WebhookId, request));
        var again = await service.AcceptAsync(wh.WebhookId, request);

        Assert.Equal(WebhookReply.Accepted, again.Reply);
        Assert.Equal("Succeeded", (await Finished(again.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task A_repeat_during_recording_is_busy_not_queued_when_the_recording_then_fails()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        var request = Signed(wh, "delivery-0178");
        // The first delivery's row is written; its answer is held, and then lost.
        _faults.Arm("INSERT INTO \"WebhookDeliveries\"", Fault.HoldAnswer);
        var first = Task.Run(() => service.AcceptAsync(wh.WebhookId, request));
        await _faults.Holding.WaitAsync(TimeSpan.FromSeconds(10));

        // A repeat meanwhile is not told the delivery is queued: the first may yet fail.
        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), await service.AcceptAsync(wh.WebhookId, request));

        _faults.Resume();
        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), await first);
        var again = await service.AcceptAsync(wh.WebhookId, request);
        Assert.Equal(WebhookReply.Accepted, again.Reply);
        Assert.Equal("Succeeded", (await Finished(again.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task A_database_wait_does_not_outlive_the_run_timeout()
    {
        _options.MaxConcurrentRuns = 1;
        // Long enough for the second, healthy run on a loaded CI runner (300 ms failed it there), short enough that
        // the stalled run's deadline frees the worker well inside Finished's wait.
        _options.RunTimeout = TimeSpan.FromMilliseconds(1500);
        var stalled = Seed(WebhookSignatures.Whiskers, target: "web-1");
        var next = Seed(WebhookSignatures.Whiskers, target: "worker");
        var service = Service();
        // The worker's read of the webhook hangs (a lock wait) far beyond the run's time.
        _faults.Arm("FROM \"Webhooks\"", Fault.Stall, runsOnly: true);

        var first = await service.AcceptAsync(stalled.WebhookId, Signed(stalled, "delivery-0173"));
        var second = await service.AcceptAsync(next.WebhookId, Signed(next, "delivery-0174"));

        // The only worker is free again within the deadline: the next run goes on; the stalled one never ran and was
        // given up, final (INV-45: on record, see A_run_its_worker_gave_up_before_it_started_is_final_and_on_record_once).
        Assert.Equal("Succeeded", (await Finished(second.RunId!)).State);
        Assert.Equal("Failed", (await Finished(first.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task Pruning_never_holds_a_worker()
    {
        _options.MaxConcurrentRuns = 1;
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        // The first run's retention pass hangs (a lock wait).
        _faults.Arm("DELETE FROM \"WebhookDeliveries\"", Fault.Stall, runsOnly: true);

        var first = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0175"));
        Assert.Equal("Succeeded", (await Finished(first.RunId!)).State);
        var second = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0176"));

        Assert.Equal("Succeeded", (await Finished(second.RunId!)).State);
    }


    [Fact]
    public async Task A_real_database_lock_does_not_hold_a_worker_past_its_deadline()
    {
        _options.MaxConcurrentRuns = 1;
        _options.RunTimeout = LockTestRunTimeout;
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        // Another connection holds SQLite's write lock when the run starts; the fault lets the start wait for it without
        // a timeout (until the test unlocks it), as SQLite waits synchronously and does not cancel a command under way.
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.LockDatabase, runsOnly: true, skip: 1);   // after its acceptance, the first run write

        var (success, _) = await service.TriggerSignedTestAsync(wh.WebhookId).WaitAsync(HangGuard);

        // The worker came back within the deadline, not the database's timeout — proven by order: its start was still
        // waiting for the lock when it answered. Once the lock is gone the start that landed late is recorded as
        // failed — the run never ran.
        AssertAnsweredBeforeTheDatabase();
        Assert.False(success);
        _faults.Unlock();
        string runId;
        using (var scope = _sp.CreateScope())
            runId = (await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries.SingleAsync()).RunId;
        Assert.Equal("Failed", (await Finished(runId)).State);
        Assert.Equal(0, _runner.Runs);
    }

    [Fact]
    public async Task Synchronous_work_before_the_action_returns_does_not_hold_a_worker_past_its_deadline()
    {
        _options.MaxConcurrentRuns = 1;
        _options.RunTimeout = TimeSpan.FromMilliseconds(300);
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service(text: new KeyLocalizer());
        _runner.BlockSynchronously = true;
        Assert.True(_options.TestRunTimeout > HangGuard, "premise: the page Test waits longer than the hang guard");

        // The run is past its time while its owner's authority is still being looked up: it is handed over (it
        // keeps its server) and the worker comes back — by order: the action is still blocked (it is unblocked only
        // below), so an answer at all, and "overdue", shows the worker did not wait for it.
        var (success, output) = await service.TriggerSignedTestAsync(wh.WebhookId).WaitAsync(HangGuard);

        Assert.False(success);
        Assert.Equal("[Webhook_Run_Overdue]", output);
        Assert.Equal(1, service.LockedResources);
        _runner.Unblock();
        await WaitFor(() => service.LockedResources == 0);
    }

    private static async Task<WebhookAcceptance> Eventually(Func<Task<WebhookAcceptance>> attempt,
        Func<WebhookAcceptance, bool> done)
    {
        for (var i = 0; ; i++)
        {
            var reply = await attempt();
            if (done(reply) || i == 200) return reply;
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task A_worker_carries_nothing_of_the_caller_that_started_it()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        WebhookAcceptance first;
        Ambient.Value = "page-request";
        using (MutationScope.Begin("container.restart", "ui-operation"))
            first = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0151"));
        Ambient.Value = null;
        await Finished(first.RunId!);

        Assert.Equal(new (string?, string?)[] { (null, null) }, _runner.Contexts);
    }

    internal static readonly AsyncLocal<string?> Ambient = new();

    [Fact]
    public async Task A_full_queue_answers_busy_and_records_nothing_but_still_names_known_runs()
    {
        _options.MaxConcurrentRuns = 1;
        _options.QueueCapacity = 1;
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        _runner.Hold();
        var running = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0201"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0202"));

        // Full: a new delivery is busy and not recorded; one already queued is named with its run, which will run.
        var busy = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0203"));
        var repeat = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0202"));

        Assert.Equal(WebhookReply.Accepted, running.Reply);
        Assert.Equal(WebhookReply.Accepted, waiting.Reply);
        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), busy);
        Assert.Equal(new WebhookAcceptance(WebhookReply.Duplicate, waiting.RunId, "queued"), repeat);
        using (var scope = _sp.CreateScope())
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries.CountAsync());

        _runner.Release();
        Assert.Equal("Succeeded", (await Finished(waiting.RunId!)).State);
        var retried = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0203"));
        Assert.Equal(WebhookReply.Accepted, retried.Reply);
        await Finished(retried.RunId!);
        Assert.Equal(3, _runner.Runs);
    }

    [Fact]
    public async Task A_full_queue_answers_busy_for_a_delivery_a_stopped_process_left_unaccepted()
    {
        _options.MaxConcurrentRuns = 1;
        _options.QueueCapacity = 1;
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        var request = Signed(wh, "delivery-0204");
        SeedDelivery(wh, request, "left-queued", "Recording");
        _runner.Hold();
        await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0205"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0206"));

        // Nothing would run it, so it is not "queued": the provider is told to come back.
        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), await service.AcceptAsync(wh.WebhookId, request));

        _runner.Release();
        await Finished(waiting.RunId!);
        var again = await service.AcceptAsync(wh.WebhookId, request);
        Assert.Equal(new WebhookAcceptance(WebhookReply.Accepted, "left-queued", "queued"), again);
        Assert.Equal("Succeeded", (await Finished("left-queued")).State);
    }

    [Fact]
    public async Task A_takeover_retried_after_its_answer_was_lost_still_runs()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0207");
        var (key, _) = WebhookSignatures.Verify(wh, request, _time.GetUtcNow(), _options.TimestampTolerance);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.WebhookDeliveries.Add(new WebhookDeliveryEntity
            {
                WebhookId = wh.WebhookId, DeliveryKey = key!, RunId = "left-queued", State = "Recording",
                Instance = "stopped-process/claim", ReceivedAt = _time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }
        // The takeover commits, its answer is lost, and the retry changes nothing.
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.Replay);

        var reply = await Service().AcceptAsync(wh.WebhookId, request);

        Assert.Equal(new WebhookAcceptance(WebhookReply.Accepted, "left-queued", "queued"), reply);
        Assert.Equal("Succeeded", (await Finished("left-queued")).State);
        Assert.Equal(1, _runner.Runs);
    }

    [Fact]
    public async Task A_takeover_lost_to_one_that_then_failed_is_busy_not_queued()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0208");
        var (key, _) = WebhookSignatures.Verify(wh, request, _time.GetUtcNow(), _options.TimestampTolerance);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.WebhookDeliveries.Add(new WebhookDeliveryEntity
            {
                WebhookId = wh.WebhookId, DeliveryKey = key!, RunId = "left-queued", State = "Recording",
                Instance = "stopped-process/claim", ReceivedAt = _time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }
        var service = Service();
        // Another delivery takes it over first and then fails before queueing it: this one's takeover changes
        // nothing and finds the row under a claim nothing holds.
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.Steal);

        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), await service.AcceptAsync(wh.WebhookId, request));

        var again = await service.AcceptAsync(wh.WebhookId, request);
        Assert.Equal(new WebhookAcceptance(WebhookReply.Accepted, "left-queued", "queued"), again);
        Assert.Equal("Succeeded", (await Finished("left-queued")).State);
    }

    [Fact]
    public void Requests_per_webhook_and_overall_are_limited()
    {
        _options.RequestsPerMinutePerWebhook = 2;
        var service = Service();

        Assert.Null(service.Admit("hook-a"));
        Assert.Null(service.Admit("hook-a"));
        Assert.Equal(WebhookReply.RateLimited, service.Admit("hook-a"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(service.Admit("hook-a"));

        _options.RequestsPerMinute = 1;
        var limited = Service();
        Assert.Null(limited.Admit("anything"));
        Assert.Equal(WebhookReply.RateLimited, limited.Admit("something-else"));
    }

    [Fact]
    public async Task A_known_and_an_unknown_id_are_limited_alike()
    {
        _options.RequestsPerMinutePerWebhook = 3;
        var wh = Seed(WebhookSignatures.Whiskers);
        var bad = Signed(wh, "delivery-0306") with { WhiskersSignature = "sha256=00" };

        var known = new List<int?>();
        var unknown = new List<int?>();
        // A limiter each: in one, the two ids could share a bucket.
        var forKnown = Service();
        var forUnknown = Service();
        for (var i = 0; i < 4; i++) known.Add((await Endpoint(forKnown, wh.WebhookId, bad)).Status);
        for (var i = 0; i < 4; i++) unknown.Add((await Endpoint(forUnknown, "no-such-hook-0001", bad)).Status);

        int?[] expected = [400, 400, 400, 429];
        Assert.Equal(expected, known);
        Assert.Equal(expected, unknown);
    }

    [Fact]
    public async Task A_limited_request_is_answered_without_reading_its_body_and_oversized_ones_count()
    {
        _options.RequestsPerMinutePerWebhook = 2;
        _options.MaxBodyBytes = 4;
        var wh = Seed();
        var service = Service();
        var oversized = GitHub(wh, "too large");

        Assert.Equal(400, (await Endpoint(service, wh.WebhookId, oversized)).Status);
        Assert.Equal(400, (await Endpoint(service, wh.WebhookId, oversized)).Status);
        // The third is limited before its body is touched (the stream would throw).
        Assert.Equal(429, (await Endpoint(service, wh.WebhookId, oversized, new ThrowingStream())).Status);
    }

    [Fact]
    public async Task With_the_module_off_a_request_is_rejected_without_reading_its_body()
    {
        var wh = Seed();

        var answer = await Endpoint(new NoopWebhookService(), wh.WebhookId, GitHub(wh, "{}"), new ThrowingStream());

        Assert.Equal((400, "{ status = rejected }"), answer);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_rejected()
    {
        _options.MaxBodyBytes = 16;
        var wh = Seed();
        var reply = await Service().AcceptAsync(wh.WebhookId, GitHub(wh, new string('x', 17)));

        Assert.Equal(WebhookReply.Rejected, reply.Reply);
        Assert.Equal(0, _runner.Runs);
    }

    // ---------------------------------------------------------------- the endpoint's answers

    [Fact]
    public async Task Every_invalid_request_gets_the_same_answer()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var off = Seed(WebhookSignatures.Whiskers, enabled: false);
        var service = Service();

        var answers = new[]
        {
            await Endpoint(service, "does-not-exist", Signed(wh, "delivery-0401")),
            await Endpoint(service, off.WebhookId, Signed(off, "delivery-0402")),
            await Endpoint(service, wh.WebhookId, Signed(wh, "delivery-0403") with { WhiskersSignature = "sha256=00" }),
            await Endpoint(service, wh.WebhookId, Signed(wh, "delivery-0404", _time.GetUtcNow().AddHours(-1))),
        };

        Assert.All(answers, a =>
        {
            Assert.Equal(StatusCodes.Status400BadRequest, a.Status);
            Assert.Equal("{ status = rejected }", a.Body);
        });
        var accepted = await Endpoint(service, wh.WebhookId, Signed(wh, "delivery-0405"));
        Assert.Equal(StatusCodes.Status202Accepted, accepted.Status);
        Assert.Contains("status = queued, runId = ", accepted.Body);
        Assert.Equal(StatusCodes.Status200OK, (await Endpoint(service, wh.WebhookId, Signed(wh, "delivery-0405"))).Status);
    }

    private async Task<(int? Status, string? Body)> Endpoint(IWebhookService service, string webhookId, WebhookRequest request,
        Stream? body = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton(Options.Create(_options));
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Body = body ?? new MemoryStream(request.Body);
        if (request.WhiskersSignature is { } s) ctx.Request.Headers["X-Whiskers-Signature"] = s;
        if (request.WhiskersTimestamp is { } t) ctx.Request.Headers["X-Whiskers-Timestamp"] = t;
        if (request.WhiskersDelivery is { } d) ctx.Request.Headers["X-Whiskers-Delivery"] = d;
        if (request.GitHubSignature is { } g) ctx.Request.Headers["X-Hub-Signature-256"] = g;
        var result = await WebhookEndpoint.HandleAsync(webhookId, ctx);
        return ((result as IStatusCodeHttpResult)?.StatusCode, (result as IValueHttpResult)?.Value?.ToString());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    // ---------------------------------------------------------------- doubles

    /// <summary>Plans every directory as the one compose project <paramref name="project"/>.</summary>
    private sealed class ProjectPlanner(string project) : IComposePlanner
    {
        public Task<ComposePlan> PlanAsync(ComposeDeployment deployment, CancellationToken ct = default) =>
            Task.FromResult(new ComposePlan(deployment.ServerId, project, deployment.ProjectDirectory ?? "/", [], [], null,
                HasEnvFile: false, RemoveOrphans: false, [], []));

        public Task<ComposePlan> PlanEnvChangeAsync(ContainerInfo container, string serverId, Func<string?, string> newEnv,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private enum Fault { Fail, LoseAnswer, Stall, Replay, HoldAnswer, Steal, LockDatabase, StallIgnoringCancellation }

    /// <summary>Breaks the next database command whose text contains a fragment — with <c>runsOnly</c>, only one of
    /// a run (the worker's commands carry the run's deadline; a delivery's own lookups have none): fails it, executes
    /// it but loses its answer, or makes it wait until it is cancelled.</summary>
    private sealed class Faults : DbCommandInterceptor
    {
        private readonly object _lock = new();
        private string? _fragment;
        private bool _runsOnly;
        private Fault _fault;
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a <see cref="Fault.HoldAnswer"/> command has executed and holds its answer.</summary>
        public Task Holding => _holding.Task;

        public void Resume() => _resume.TrySetResult();

        private readonly TaskCompletionSource _unstall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets a <see cref="Fault.StallIgnoringCancellation"/> command go on (it then executes).</summary>
        public void Unstall() => _unstall.TrySetResult();

        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _runWrites = new();

        /// <summary>How many writes of a run (a cancellable token) containing <paramref name="fragment"/> were issued.</summary>
        public int RunWrites(params string[] fragments) =>
            _runWrites.Count(c => fragments.All(f => c.Contains(f, StringComparison.Ordinal)));

        private Microsoft.Data.Sqlite.SqliteConnection? _locker;
        private DbCommand? _locked;
        private int _lockedEnded;

        /// <summary>Whether the command a <see cref="Fault.LockDatabase"/> lock was taken for has ended (executed,
        /// failed or cancelled). It waits without a timeout while the lock is held — so a run that was answered while
        /// this is false came back before that command did, whatever the time it took.</summary>
        public bool LockedCommandEnded => Volatile.Read(ref _lockedEnded) == 1;

        private readonly TaskCompletionSource _lockTaken = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a <see cref="Fault.LockDatabase"/> fault holds the lock and its command, its token
        /// not yet cancelled, is about to go on to SQLite (without a timeout) — the premise of the lock tests.</summary>
        public Task LockTaken => _lockTaken.Task;

        private int _endedBadly;

        /// <summary>Commands that failed or were cancelled since the last <see cref="Arm"/> — any command, not only the
        /// locked one (a command that met the lock fails after SQLite's 30 s busy timeout). A wait abandoned without
        /// its command ending does not show here; the lock tests' hang guard covers that.</summary>
        public int EndedBadlySinceArmed => Volatile.Read(ref _endedBadly);

        private void Ended(DbCommand command)
        {
            if (ReferenceEquals(command, Volatile.Read(ref _locked))) Volatile.Write(ref _lockedEnded, 1);
        }

        private void EndedBadly(DbCommand command)
        {
            Interlocked.Increment(ref _endedBadly);
            Ended(command);
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken ct = default)
        {
            Ended(command);
            return ValueTask.FromResult(result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Ended(command);
            return result;
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken ct = default)
        {
            EndedBadly(command);
            return Task.CompletedTask;
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => EndedBadly(command);

        // EF Core reports an OperationCanceledException here, not as a failure.
        public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken ct = default)
        {
            EndedBadly(command);
            return Task.CompletedTask;
        }

        public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => EndedBadly(command);

        /// <summary>Ends a <see cref="Fault.LockDatabase"/> lock (closing the connection rolls it back).</summary>
        public void Unlock() => Interlocked.Exchange(ref _locker, null)?.Dispose();

        private int _skip;
        private int _tripped;

        /// <summary>How many armed faults have fired — a test that relies on one asserts it did.</summary>
        public int Tripped => Volatile.Read(ref _tripped);

        /// <param name="skip">How many matching commands pass before the one that breaks.</param>
        public void Arm(string fragment, Fault fault, bool runsOnly = false, int skip = 0)
        {
            lock (_lock) (_fragment, _fault, _runsOnly, _skip) = (fragment, fault, runsOnly, skip);
            Volatile.Write(ref _endedBadly, 0);
        }

        private Fault? Trip(DbCommand command, CancellationToken ct)
        {
            lock (_lock)
            {
                if (_fragment is null || !command.CommandText.Contains(_fragment, StringComparison.Ordinal)) return null;
                if (_runsOnly && !ct.CanBeCanceled) return null;
                if (_skip-- > 0) return null;
                _fragment = null;
                Interlocked.Increment(ref _tripped);
                return _fault;
            }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (ct.CanBeCanceled) _runWrites.Enqueue(command.CommandText);
            await Break(command, ct);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            await Break(command, ct);
            return result;
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _runCommands = new();

        /// <summary>How many reads of a run (a cancellable token: the worker's, not the acceptance's) containing
        /// <paramref name="fragment"/> have executed — a signal to wait on instead of time.</summary>
        public int RunCommands(string fragment) => _runCommands.Count(c => c.Contains(fragment, StringComparison.Ordinal));

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken ct = default)
        {
            if (ct.CanBeCanceled) _runCommands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        private async Task Break(DbCommand command, CancellationToken ct)
        {
            switch (Trip(command, ct))
            {
                case Fault.Fail:
                    throw new InvalidOperationException("The database is unavailable.");
                case Fault.Stall:
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
                case Fault.StallIgnoringCancellation:
                    // As SQLite does: a command under way is not cancelled by its token.
                    await _unstall.Task;
                    break;
                case Fault.LoseAnswer:
                    // Executed, then the answer is lost: the caller cannot tell whether it was written.
                    await using (var reader = await command.ExecuteReaderAsync(ct)) { }
                    throw new OperationCanceledException("The answer was lost.");
                case Fault.HoldAnswer:
                    await using (var reader = await command.ExecuteReaderAsync(ct)) { }
                    _holding.TrySetResult();
                    await _resume.Task;
                    throw new OperationCanceledException("The answer was lost.");
                case Fault.LockDatabase:
                {
                    // Another connection takes the database's write lock just before this command.
                    var locker = new Microsoft.Data.Sqlite.SqliteConnection(command.Connection!.ConnectionString);
                    locker.Open();
                    using (var begin = locker.CreateCommand())
                    {
                        begin.CommandText = "BEGIN IMMEDIATE";
                        begin.ExecuteNonQuery();
                    }
                    _locker = locker;
                    Volatile.Write(ref _locked, command);
                    // This command waits for the lock until the test lets it go — not SQLite's 30 s: "answered before
                    // the database did" then holds however slowly a loaded runner schedules the answer.
                    command.CommandTimeout = 0;
                    // A token already cancelled ends it at once instead (CommandCanceled → LockedCommandEnded): then
                    // the run reached the lock only after its deadline, a runner problem — the premise stays unmet, so
                    // the lock tests almost always fail on the premise (a deadline that fires between this check and
                    // the execute still ends the command, and they fail on "ended before the answer").
                    if (!ct.IsCancellationRequested) _lockTaken.TrySetResult();
                    break;
                }
                case Fault.Steal:
                    // Another claim (one that is no longer held) took the row just before this command.
                    await using (var steal = command.Connection!.CreateCommand())
                    {
                        steal.Transaction = command.Transaction;
                        steal.CommandText = "UPDATE \"WebhookDeliveries\" SET \"Instance\" = 'lost-winner/claim'";
                        await steal.ExecuteNonQueryAsync(ct);
                    }
                    break;
                case Fault.Replay:
                    // Committed once, the answer lost, and retried (as a retrying execution strategy does): the
                    // caller sees only the retry.
                    await command.ExecuteNonQueryAsync(ct);
                    break;
            }
        }
    }

    /// <summary>Answers every key as "[key]", so a test sees which resource was used.</summary>
    private sealed class KeyLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, $"[{name}]");
        public LocalizedString this[string name, params object[] arguments] => new(name, $"[{name}]");
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    /// <summary>Runs nothing: records each run, how many overlapped, and can hold a run until released.</summary>
    private sealed class RecordingRunner : IAutomationMutationRunner
    {
        private int _runs, _current, _max;
        private TaskCompletionSource _gate = Completed();
        private readonly List<TaskCompletionSource> _gates = [];
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string?> OperationIds { get; } = [];
        public List<string?> Owners { get; } = [];
        public List<(string?, string?)> Contexts { get; } = [];
        public List<string?> ContainerIds { get; } = [];
        private int _held;
        public TimeSpan Delay { get; set; }
        /// <summary>Every run is cancelled before its action.</summary>
        public bool Cancel { get; set; }
        /// <summary>Blocks the calling thread before the run returns a task (synchronous work — an owner lookup in a
        /// locked database) until <see cref="Unblock"/>.</summary>
        public bool BlockSynchronously { get; set; }
        private readonly ManualResetEventSlim _unblock = new(false);
        public void Unblock() => _unblock.Set();
        /// <summary>Every run ends as the gateway reports a cancelled action: failed, with the cancellation.</summary>
        public bool CancelLikeGateway { get; set; }
        public int Runs => Volatile.Read(ref _runs);
        public int MaxConcurrent => Volatile.Read(ref _max);
        public Task Started => _started.Task;

        /// <summary>Runs from now on wait until released; each Hold is a gate of its own.</summary>
        public void Hold()
        {
            lock (_gates)
                _gates.Add(_gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public void Release()
        {
            lock (_gates)
                foreach (var gate in _gates) gate.TrySetResult();
        }

        /// <summary>Releases the runs of the first Hold only.</summary>
        public void ReleaseFirst()
        {
            lock (_gates) _gates[0].TrySetResult();
        }

        /// <summary>Only the run already waiting stays held; later runs pass.</summary>
        public void ReleaseOthers() => _onlyFirst = true;
        private volatile bool _onlyFirst;

        private static TaskCompletionSource Completed()
        {
            var tcs = new TaskCompletionSource();
            tcs.SetResult();
            return tcs;
        }

        public async Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            if (BlockSynchronously) _unblock.Wait();
            if (Cancel) throw new OperationCanceledException("cancelled");
            if (CancelLikeGateway)
                return new MutationOutcome<T>(actor.OperationId ?? "op", MutationStatus.Failed, default, "cancelled",
                    VerificationStatus.NotRun, new OperationCanceledException("A task was canceled."));
            Interlocked.Increment(ref _runs);
            lock (OperationIds)
            {
                OperationIds.Add(actor.OperationId);
                Owners.Add(actor.OwnerId);
                Contexts.Add((Ambient.Value, MutationScope.Current?.OperationId));
                ContainerIds.Add(arguments.GetType().GetProperty("containerId")?.GetValue(arguments) as string);
            }
            var held = _gate;
            var first = Interlocked.Increment(ref _held) == 1;
            var now = Interlocked.Increment(ref _current);
            int seen;
            while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            _started.TrySetResult();
            try
            {
                if (first || !_onlyFirst) await held.Task;
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
            return new MutationOutcome<T>(actor.OperationId ?? "op", MutationStatus.Executed, (T)(object)(true, "ok"), null,
                VerificationStatus.NotRun);
        }

        public Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task> action, CancellationToken ct = default) =>
            throw new NotSupportedException();

        /// <summary>The deliveries recorded as not run (INV-45): who, under which operation, as which capability.</summary>
        public List<(AutomationActor Actor, string Capability, string Reason)> Refusals { get; } = [];

        /// <summary>When set, refusals are recorded through it as well — the real gateway writing <c>AuditLog</c>
        /// rows (INV-45).</summary>
        public IAutomationMutationRunner? Audit { get; set; }

        public Task<MutationOutcome<object?>> RefuseAsync(AutomationActor actor, string capabilityId, object arguments,
            string reason, CancellationToken ct = default)
        {
            lock (Refusals) Refusals.Add((actor, capabilityId, reason));
            return Audit?.RefuseAsync(actor, capabilityId, arguments, reason, ct)
                   ?? Task.FromResult(new MutationOutcome<object?>(actor.OperationId ?? "op", MutationStatus.Denied, null, reason,
                       VerificationStatus.NotRun));
        }
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new InvalidOperationException("The body must not be read.");
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

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

    public WebhookReplayTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False").AddInterceptors(_faults));
        services.AddSingleton<IAutomationMutationRunner>(_runner);
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _faults.Unlock();
        _runner.Unblock();
        _runner.Release();
        _sp.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    /// <summary>A new service is a new process: what another one queued or ran reads as cut off by a restart.</summary>
    private WebhookService Service(IDockerService? docker = null, IStringLocalizer<SharedResource>? text = null,
        IComposePlanner? planner = null) =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(), docker: docker!, executor: null!, planner: planner!,
            NullLogger<WebhookService>.Instance, Options.Create(_options), _time, text: text);

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
    public async Task A_delivery_still_queued_at_a_restart_runs_on_its_next_delivery()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var request = Signed(wh, "delivery-0007");
        var (key, _) = WebhookSignatures.Verify(wh, request, _time.GetUtcNow(), _options.TimestampTolerance);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.WebhookDeliveries.Add(new WebhookDeliveryEntity
            {
                WebhookId = wh.WebhookId, DeliveryKey = key!, RunId = "queued-before-restart", State = "Queued",
                Instance = "stopped-process/claim", ReceivedAt = _time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        var again = await Service().AcceptAsync(wh.WebhookId, request);

        Assert.Equal(WebhookReply.Accepted, again.Reply);
        Assert.Equal("queued-before-restart", again.RunId);
        Assert.Equal("Succeeded", (await Finished(again.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
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

    [Fact]
    public async Task A_run_its_worker_could_not_start_is_taken_over_by_its_next_delivery()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        var request = Signed(wh, "delivery-0171");
        // The worker's read of the webhook fails.
        _faults.Arm("FROM \"Webhooks\"", Fault.Fail, runsOnly: true);

        var first = await service.AcceptAsync(wh.WebhookId, request);
        Assert.Equal(WebhookReply.Accepted, first.Reply);
        var again = await Eventually(() => service.AcceptAsync(wh.WebhookId, request), r => r.Reply != WebhookReply.Duplicate);

        // Not "queued" for ever: nothing held it any more, so the next delivery runs it.
        Assert.Equal(new WebhookAcceptance(WebhookReply.Accepted, first.RunId, "queued"), again);
        Assert.Equal("Succeeded", (await Finished(first.RunId!)).State);
        Assert.Equal(1, _runner.Runs);
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

        // The only worker is free again within the deadline: the next run goes on, the stalled one never started.
        Assert.Equal("Succeeded", (await Finished(second.RunId!)).State);
        Assert.Equal("Queued", (await Delivery(first.RunId!)).State);
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
    public async Task A_run_given_up_by_its_worker_never_touches_the_run_that_took_it_over()
    {
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        var request = Signed(wh, "delivery-0177");
        _faults.Arm("FROM \"Webhooks\"", Fault.Fail, runsOnly: true);
        var first = await service.AcceptAsync(wh.WebhookId, request);
        _runner.Hold();

        var again = await Eventually(() => service.AcceptAsync(wh.WebhookId, request), r => r.Reply != WebhookReply.Duplicate);
        Assert.Equal(WebhookReply.Accepted, again.Reply);
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);

        // The run that took it over is running; the worker that gave it up did not mark it failed.
        Assert.Equal("Running", (await Delivery(first.RunId!)).State);
        _runner.Release();
        Assert.Equal("Succeeded", (await Finished(first.RunId!)).State);
    }

    [Fact]
    public async Task A_real_database_lock_does_not_hold_a_worker_past_its_deadline()
    {
        _options.MaxConcurrentRuns = 1;
        _options.RunTimeout = TimeSpan.FromMilliseconds(300);
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        // Another connection holds SQLite's write lock when the run starts; SQLite waits for it synchronously (up to
        // its own 30 s timeout) and does not cancel.
        _faults.Arm("UPDATE \"WebhookDeliveries\"", Fault.LockDatabase, runsOnly: true);

        var (success, _) = await service.TriggerSignedTestAsync(wh.WebhookId).WaitAsync(TimeSpan.FromSeconds(5));

        // The worker came back within the deadline, not the database's timeout; once the lock is gone the start
        // that landed late is recorded as failed — the run never ran.
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

        // The run is past its time while its owner's authority is still being looked up: it is handed over (it
        // keeps its server) and the worker comes back.
        var (success, output) = await service.TriggerSignedTestAsync(wh.WebhookId).WaitAsync(TimeSpan.FromSeconds(5));

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
    public async Task A_full_queue_answers_busy_for_a_delivery_a_stopped_process_left_queued()
    {
        _options.MaxConcurrentRuns = 1;
        _options.QueueCapacity = 1;
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        var request = Signed(wh, "delivery-0204");
        var (key, _) = WebhookSignatures.Verify(wh, request, _time.GetUtcNow(), _options.TimestampTolerance);
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.WebhookDeliveries.Add(new WebhookDeliveryEntity
            {
                WebhookId = wh.WebhookId, DeliveryKey = key!, RunId = "left-queued", State = "Queued",
                Instance = "stopped-process/claim", ReceivedAt = _time.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }
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
                WebhookId = wh.WebhookId, DeliveryKey = key!, RunId = "left-queued", State = "Queued",
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
                WebhookId = wh.WebhookId, DeliveryKey = key!, RunId = "left-queued", State = "Queued",
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

    private enum Fault { Fail, LoseAnswer, Stall, Replay, HoldAnswer, Steal, LockDatabase }

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

        private Microsoft.Data.Sqlite.SqliteConnection? _locker;

        /// <summary>Ends a <see cref="Fault.LockDatabase"/> lock (closing the connection rolls it back).</summary>
        public void Unlock() => Interlocked.Exchange(ref _locker, null)?.Dispose();

        public void Arm(string fragment, Fault fault, bool runsOnly = false)
        {
            lock (_lock) (_fragment, _fault, _runsOnly) = (fragment, fault, runsOnly);
        }

        private Fault? Trip(DbCommand command, CancellationToken ct)
        {
            lock (_lock)
            {
                if (_fragment is null || !command.CommandText.Contains(_fragment, StringComparison.Ordinal)) return null;
                if (_runsOnly && !ct.CanBeCanceled) return null;
                _fragment = null;
                return _fault;
            }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            await Break(command, ct);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            await Break(command, ct);
            return result;
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

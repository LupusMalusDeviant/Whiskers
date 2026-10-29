using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Models;
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

    public WebhookReplayTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton<IAutomationMutationRunner>(_runner);
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _runner.Release();
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    /// <summary>A new service is a new process: what another one queued or ran reads as cut off by a restart.</summary>
    private WebhookService Service() => new(_sp.GetRequiredService<IServiceScopeFactory>(), docker: null!,
        executor: null!, planner: null!, NullLogger<WebhookService>.Instance, Options.Create(_options), _time);

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
        Assert.All(replies.Where(r => r.Reply != WebhookReply.Accepted), r =>
        {
            Assert.Equal(WebhookReply.Duplicate, r.Reply);
            Assert.Equal(accepted.RunId, r.RunId);
        });
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
    public async Task A_full_queue_answers_busy_and_keeps_nothing()
    {
        _options.MaxConcurrentRuns = 1;
        _options.QueueCapacity = 1;
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();
        _runner.Hold();
        var running = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0201"));
        await _runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0202"));
        var busy = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0203"));

        Assert.Equal(WebhookReply.Accepted, running.Reply);
        Assert.Equal(WebhookReply.Accepted, waiting.Reply);
        Assert.Equal(new WebhookAcceptance(WebhookReply.Busy), busy);

        _runner.Release();
        await Finished(waiting.RunId!);
        // Nothing was recorded for the busy one: delivered again, it runs.
        var retried = await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0203"));
        Assert.Equal(WebhookReply.Accepted, retried.Reply);
        await Finished(retried.RunId!);
        Assert.Equal(3, _runner.Runs);
    }

    [Fact]
    public async Task Requests_per_webhook_and_overall_are_limited()
    {
        _options.RequestsPerMinutePerWebhook = 2;
        var wh = Seed(WebhookSignatures.Whiskers);
        var service = Service();

        Assert.Equal(WebhookReply.Accepted, (await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0301"))).Reply);
        Assert.Equal(WebhookReply.Rejected, (await service.AcceptAsync(wh.WebhookId, Signed(wh, "bad"))).Reply);
        Assert.Equal(WebhookReply.RateLimited, (await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0302"))).Reply);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(WebhookReply.Accepted, (await service.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0303"))).Reply);

        _options.RequestsPerMinute = 1;
        var limited = Service();
        await limited.AcceptAsync("unknown", Signed(wh, "delivery-0304"));
        Assert.Equal(WebhookReply.RateLimited, (await limited.AcceptAsync(wh.WebhookId, Signed(wh, "delivery-0305"))).Reply);
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

    private async Task<(int? Status, string? Body)> Endpoint(IWebhookService service, string webhookId, WebhookRequest request)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton(Options.Create(_options));
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Body = new MemoryStream(request.Body);
        if (request.WhiskersSignature is { } s) ctx.Request.Headers["X-Whiskers-Signature"] = s;
        if (request.WhiskersTimestamp is { } t) ctx.Request.Headers["X-Whiskers-Timestamp"] = t;
        if (request.WhiskersDelivery is { } d) ctx.Request.Headers["X-Whiskers-Delivery"] = d;
        if (request.GitHubSignature is { } g) ctx.Request.Headers["X-Hub-Signature-256"] = g;
        var result = await WebhookEndpoint.HandleAsync(webhookId, ctx);
        return ((result as IStatusCodeHttpResult)?.StatusCode, (result as IValueHttpResult)?.Value?.ToString());
    }

    // ---------------------------------------------------------------- doubles

    /// <summary>Runs nothing: records each run, how many overlapped, and can hold a run until released.</summary>
    private sealed class RecordingRunner : IAutomationMutationRunner
    {
        private int _runs, _current, _max;
        private TaskCompletionSource _gate = Completed();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string?> OperationIds { get; } = [];
        public TimeSpan Delay { get; set; }
        public int Runs => Volatile.Read(ref _runs);
        public int MaxConcurrent => Volatile.Read(ref _max);
        public Task Started => _started.Task;

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate.TrySetResult();

        private static TaskCompletionSource Completed()
        {
            var tcs = new TaskCompletionSource();
            tcs.SetResult();
            return tcs;
        }

        public async Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);
            lock (OperationIds) OperationIds.Add(actor.OperationId);
            var now = Interlocked.Increment(ref _current);
            int seen;
            while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            _started.TrySetResult();
            try
            {
                await _gate.Task;
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

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

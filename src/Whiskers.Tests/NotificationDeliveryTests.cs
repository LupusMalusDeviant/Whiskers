using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>
/// R1-40 (F-08): a channel reports its failure instead of swallowing it, so the composite's retry and deadline reach it;
/// a critical event a channel still failed is kept in a bounded, durable outbox and redelivered — also after a restart;
/// what is given up on is logged and counted without credentials. Driven through the real channel adapters.
/// </summary>
public sealed class NotificationDeliveryTests : IDisposable
{
    internal const string SlackUrl = "https://hooks.slack.example/services/T01/B01/r140-slack-capability-token-1234";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-notify-{Guid.NewGuid():N}");

    public NotificationDeliveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private static NotificationEvent Stopped(string name = "web") =>
        new() { EventType = "stopped", ContainerName = name, ContainerId = name + "-1", ExitCode = 137 };

    [Fact]
    public async Task A_hanging_provider_is_cut_off_by_the_attempt_deadline_and_retried()
    {
        var handler = new ScriptedHandler(Hang, Hang);
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        var composite = Composite([Slack(handler)], outbox, TimeSpan.FromMilliseconds(200));

        await composite.SendAsync(Stopped()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, handler.Cancelled);                    // the deadline reached the network call
        var kept = Assert.Single(await outbox.DueAsync(DateTime.UtcNow.AddHours(1)));
        Assert.StartsWith("TimeoutException", kept.LastError);
    }

    [Fact]
    public async Task An_smtp_server_that_never_answers_is_cut_off_by_the_smtp_deadline()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync();       // accepts, never greets
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var email = new EmailNotificationService(new StaticOptionsMonitor<EmailSettings>(new EmailSettings
            {
                Enabled = true, Host = "127.0.0.1", Port = port, UseSsl = false, From = "whiskers@example.com",
                To = "ops@example.com",
            }), NullLogger<EmailNotificationService>.Instance) { SmtpDeadline = TimeSpan.FromMilliseconds(500) };

            // Bounded by the cancellation (TimeoutException); SmtpClient.Timeout may end it first (SmtpException).
            var started = DateTime.UtcNow;
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => email.DeliverAsync(Stopped(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(error is TimeoutException or System.Net.Mail.SmtpException, error.ToString());
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));

            // The caller's own cancellation stays a cancellation.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var slow = new EmailNotificationService(new StaticOptionsMonitor<EmailSettings>(new EmailSettings
            {
                Enabled = true, Host = "127.0.0.1", Port = port, UseSsl = false, From = "whiskers@example.com",
                To = "ops@example.com",
            }), NullLogger<EmailNotificationService>.Instance);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => slow.DeliverAsync(Stopped("api"), cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            listener.Stop();
            try { (await accepted).Dispose(); } catch { /* listener stopped */ }
        }
    }

    [Fact]
    public async Task A_critical_event_survives_a_restart_in_the_outbox_and_is_redelivered()
    {
        var paths = new DataPathOptions(_dir);
        var failing = new ScriptedHandler(Status(500), Status(500));
        var before = new NotificationOutbox(paths, NullLogger<NotificationOutbox>.Instance);
        var composite = Composite([Slack(failing)], before);
        await composite.SendAsync(Stopped());
        Assert.Equal(2, failing.Requests);
        Assert.True(await before.FlushAsync());                // the worker's pass writes it

        // The process restarts: a new outbox reads the file; the channel works again.
        var outbox = new NotificationOutbox(paths, NullLogger<NotificationOutbox>.Instance);
        var working = new ScriptedHandler();
        var worker = new NotificationOutboxWorker([Slack(working)], outbox, NullLogger<NotificationOutboxWorker>.Instance,
            TimeSpan.FromSeconds(5));

        Assert.True(await outbox.FlushAsync());                // reads the file
        await worker.RedeliverDueAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, working.Requests);                    // not due yet: it waits before the first redelivery
        Assert.Equal(1, outbox.Status.Pending);

        await worker.RedeliverDueAsync(DateTime.UtcNow.AddMinutes(2), CancellationToken.None);
        Assert.Equal(1, working.Requests);
        Assert.Contains("web", working.LastBody);
        Assert.True(await outbox.FlushAsync());
        Assert.Equal(new NotificationOutboxStatus(0, 1, 0, 0), outbox.Status);
        var after = new NotificationOutbox(paths, NullLogger<NotificationOutbox>.Instance);
        Assert.True(await after.FlushAsync());
        Assert.Empty(await after.DueAsync(DateTime.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task Only_critical_events_are_kept_for_redelivery()
    {
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        var composite = Composite([Slack(new ScriptedHandler(Status(500), Status(500), Status(500), Status(500)))], outbox);

        await composite.SendAsync(new NotificationEvent { EventType = "image_update", ContainerName = "web" });
        Assert.Equal(0, outbox.Status.Pending);
        await composite.SendAsync(new NotificationEvent { EventType = "automation_suspended", ContainerName = "AutoUpdate" });
        Assert.Equal(1, outbox.Status.Pending);

        Assert.True(CompositeNotificationService.IsCritical(new NotificationEvent { EventType = "agent_approval" }));
        Assert.True(CompositeNotificationService.IsCritical(new NotificationEvent { EventType = "operations_interrupted" }));
        Assert.True(CompositeNotificationService.IsCritical(Stopped()));
        Assert.True(CompositeNotificationService.IsCritical(new NotificationEvent { EventType = "server_recovered" }));
        Assert.False(CompositeNotificationService.IsCritical(new NotificationEvent { EventType = "image_update" }));
    }

    [Fact]
    public async Task The_outbox_is_bounded_and_drops_its_oldest_entry()
    {
        var outbox = new NotificationOutbox(new Whiskers.Services.Persistence.JsonFileStore<NotificationOutbox.OutboxFile>(
            Path.Combine(_dir, "outbox.json")), NullLogger<NotificationOutbox>.Instance);
        var start = DateTime.UtcNow;
        for (var i = 0; i <= NotificationOutbox.MaxEntries; i++)
            await outbox.AddAsync("Slack", Stopped($"c{i}"), new HttpRequestException("500"), start.AddSeconds(i));

        Assert.True(await outbox.FlushAsync());
        Assert.Equal(new NotificationOutboxStatus(NotificationOutbox.MaxEntries, 0, 0, 1), outbox.Status);
        var due = await outbox.DueAsync(start.AddHours(1));
        Assert.DoesNotContain(due, e => e.Event.ContainerName == "c0");   // the oldest went
        Assert.Contains(due, e => e.Event.ContainerName == $"c{NotificationOutbox.MaxEntries}");
    }

    [Fact]
    public async Task Redelivery_gives_up_after_its_attempts_and_says_so_without_credentials()
    {
        KnownSecrets.Register(SlackUrl);
        var logs = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var outbox = NotificationOutbox.InMemory(factory.CreateLogger<NotificationOutbox>());
        var leaking = new ScriptedHandler(Enumerable.Range(0, 20)
            .Select(_ => (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((req, _) =>
                throw new HttpRequestException($"POST {req.RequestUri} failed"))).ToArray());
        var composite = Composite([Slack(leaking)], outbox, logger: factory.CreateLogger<CompositeNotificationService>());
        var worker = new NotificationOutboxWorker([Slack(leaking)], outbox, factory.CreateLogger<NotificationOutboxWorker>(),
            TimeSpan.FromSeconds(5));

        await composite.SendAsync(Stopped());
        var now = DateTime.UtcNow;
        for (var i = 0; i < NotificationOutbox.MaxAttempts; i++)
        {
            now = now.AddHours(2);   // past every wait, and below the 24 h limit only for the first rounds
            await worker.RedeliverDueAsync(now, CancellationToken.None);
        }

        Assert.Equal(0, outbox.Status.Pending);
        Assert.Equal(1, outbox.Status.Exhausted);
        Assert.Contains(logs.Lines, l => l.Contains("to Slack failed after 2 attempts", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, l => l.Contains("could not be delivered to Slack: gave up", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("r140-slack-capability-token", StringComparison.Ordinal));
        Assert.Contains(logs.Lines, l => l.Contains("HttpRequestException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_whose_channel_was_switched_off_or_removed_leaves_the_outbox()
    {
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        await outbox.AddAsync("Slack", Stopped(), new HttpRequestException("500"), DateTime.UtcNow);
        await outbox.AddAsync("Gone", Stopped("api"), new HttpRequestException("500"), DateTime.UtcNow);
        var off = new SlackNotificationService(new HttpClient(new ScriptedHandler()),
            new StaticOptionsMonitor<SlackSettings>(new SlackSettings { Enabled = false, WebhookUrl = SlackUrl }),
            NullLogger<SlackNotificationService>.Instance);
        var worker = new NotificationOutboxWorker([off], outbox, NullLogger<NotificationOutboxWorker>.Instance,
            TimeSpan.FromSeconds(5));

        await worker.RedeliverDueAsync(DateTime.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Equal(new NotificationOutboxStatus(0, 0, 0, 2), outbox.Status);   // gone, counted as dropped — not delivered
    }

    [Fact]
    public async Task The_ai_trigger_dispatch_runs_once_and_is_bounded()
    {
        var dispatcher = new CountingDispatcher(hang: true);
        var composite = Composite([], null, TimeSpan.FromMilliseconds(200), dispatcher);

        await composite.SendAsync(Stopped()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, dispatcher.Calls);
    }

    [Fact]
    public async Task Distinct_queued_approvals_are_all_redelivered_although_the_channel_throttles()
    {
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
            await outbox.AddAsync("Slack", new NotificationEvent { EventType = "agent_approval", ApprovalId = $"a{i}" },
                new HttpRequestException("503"), now);
        var working = new ScriptedHandler();
        var throttling = new SlackNotificationService(new HttpClient(working), new StaticOptionsMonitor<SlackSettings>(
            new SlackSettings { Enabled = true, WebhookUrl = SlackUrl, ThrottleMinutes = 60 }), NullLogger<SlackNotificationService>.Instance);
        var worker = new NotificationOutboxWorker([throttling], outbox, NullLogger<NotificationOutboxWorker>.Instance, TimeSpan.FromSeconds(5));

        await worker.RedeliverDueAsync(now.AddMinutes(2), CancellationToken.None);

        Assert.Equal(3, working.Requests);                     // each approval once, none dropped by the throttle
        Assert.Equal(new NotificationOutboxStatus(0, 3, 0, 0), outbox.Status);
    }

    [Fact]
    public async Task A_matrix_retry_after_a_lost_answer_reuses_its_transaction_id()
    {
        var urls = new List<string>();
        var handler = new ScriptedHandler(
            (req, _) => { urls.Add(req.RequestUri!.AbsolutePath); throw new HttpRequestException("connection reset after the send"); },
            (req, _) => { urls.Add(req.RequestUri!.AbsolutePath); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var matrix = new MatrixNotificationService(new HttpClient(handler), new StaticOptionsMonitor<MatrixSettings>(new MatrixSettings
        {
            Enabled = true, HomeserverUrl = "https://matrix.example", AccessToken = "r140-matrix-token", RoomId = "!room:example",
            ThrottleMinutes = 0,
        }), NullLogger<MatrixNotificationService>.Instance);

        await Composite([matrix], null).SendAsync(Stopped());

        Assert.Equal(2, urls.Count);
        Assert.Equal(urls[0], urls[1]);                        // the homeserver sees the same transaction twice
    }

    [Fact]
    public async Task An_outbox_that_cannot_be_read_for_now_is_read_again_and_not_overwritten()
    {
        var path = Path.Combine(_dir, "outbox.json");
        var first = new NotificationOutbox(new Whiskers.Services.Persistence.JsonFileStore<NotificationOutbox.OutboxFile>(path),
            NullLogger<NotificationOutbox>.Instance);
        await first.AddAsync("Slack", Stopped("kept"), new HttpRequestException("500"), DateTime.UtcNow);
        Assert.True(await first.FlushAsync());

        var outbox = new NotificationOutbox(new Whiskers.Services.Persistence.JsonFileStore<NotificationOutbox.OutboxFile>(path),
            NullLogger<NotificationOutbox>.Instance);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // the volume is away for a moment
        {
            Assert.False(await outbox.FlushAsync());
            await outbox.AddAsync("Slack", Stopped("new"), new HttpRequestException("500"), DateTime.UtcNow);
            Assert.False(await outbox.FlushAsync());
            Assert.Equal(1, outbox.Status.Pending);            // what it holds in memory; the file is not read yet
            Assert.True(outbox.Status.Unsaved);
        }

        Assert.True(await outbox.FlushAsync());                // read now, merged, written
        var names = (await outbox.DueAsync(DateTime.UtcNow.AddHours(1))).Select(e => e.Event.ContainerName).ToList();
        Assert.Equal(["kept", "new"], names);
        Assert.False(outbox.Status.Unsaved);
    }

    [Fact]
    public async Task A_change_the_file_could_not_take_is_written_by_the_next_pass()
    {
        var path = Path.Combine(_dir, "outbox.json");
        var outbox = new NotificationOutbox(new Whiskers.Services.Persistence.JsonFileStore<NotificationOutbox.OutboxFile>(path),
            NullLogger<NotificationOutbox>.Instance);
        await outbox.AddAsync("Slack", Stopped(), new HttpRequestException("500"), DateTime.UtcNow);
        Assert.True(await outbox.FlushAsync());
        var entry = Assert.Single(await outbox.DueAsync(DateTime.UtcNow.AddHours(1)));

        // The write fails (on every platform: a share lock does not stop a rename on Linux).
        Whiskers.Services.Persistence.DurableFile.FailingWrites[Path.GetFullPath(path)] = 0;
        try
        {
            await outbox.CompleteAsync(entry.Id, delivered: true);
            Assert.False(await outbox.FlushAsync());
            Assert.True(outbox.Status.Unsaved);
        }
        finally
        {
            Whiskers.Services.Persistence.DurableFile.FailingWrites.TryRemove(Path.GetFullPath(path), out _);
        }
        Assert.True(await outbox.FlushAsync());

        var restarted = new NotificationOutbox(new Whiskers.Services.Persistence.JsonFileStore<NotificationOutbox.OutboxFile>(path),
            NullLogger<NotificationOutbox>.Instance);
        Assert.True(await restarted.FlushAsync());
        Assert.Empty(await restarted.DueAsync(DateTime.UtcNow.AddHours(1)));   // not delivered a second time
    }

    [Fact]
    public async Task A_producer_is_not_held_up_by_an_outbox_whose_file_is_blocked()
    {
        var path = Path.Combine(_dir, "outbox.json");
        await File.WriteAllTextAsync(path, "{\"Entries\":[]}");
        var outbox = new NotificationOutbox(new Whiskers.Services.Persistence.JsonFileStore<NotificationOutbox.OutboxFile>(path),
            NullLogger<NotificationOutbox>.Instance);
        var composite = Composite([Slack(new ScriptedHandler(Status(500), Status(500)))], outbox);
        using var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await composite.SendAsync(Stopped()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, outbox.Status.Pending);
        Assert.True(outbox.Status.Unsaved);
    }

    [Fact]
    public async Task An_entry_past_its_age_is_given_up_before_it_is_sent()
    {
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        var now = DateTime.UtcNow;
        await outbox.AddAsync("Slack", Stopped(), new HttpRequestException("500"), now - NotificationOutbox.MaxAge - TimeSpan.FromMinutes(1));
        var working = new ScriptedHandler();
        var worker = new NotificationOutboxWorker([Slack(working)], outbox, NullLogger<NotificationOutboxWorker>.Instance, TimeSpan.FromSeconds(5));

        await worker.RedeliverDueAsync(now, CancellationToken.None);

        Assert.Equal(0, working.Requests);
        Assert.Equal(new NotificationOutboxStatus(0, 0, 1, 0), outbox.Status);
    }

    [Fact]
    public async Task Configured_credentials_reach_no_file_generation_log_or_payload_not_even_as_a_prefix()
    {
        // Unique per run, registered as the module registers configured channel credentials.
        var token = "r140tok" + Guid.NewGuid().ToString("N");
        var password = "r140pw" + Guid.NewGuid().ToString("N");
        var url = $"https://hooks.slack.example/services/T01/B01/{token}";
        KnownSecrets.Register(url);
        KnownSecrets.Register(password);
        string[] canaries = [token, password];

        var logs = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var paths = new DataPathOptions(_dir);
        var outbox = new NotificationOutbox(paths, factory.CreateLogger<NotificationOutbox>());
        // The provider echoes the URL and the event's text in its error, with the secret near the 300-character cut.
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> echo = (req, _) =>
            throw new HttpRequestException($"{new string('x', 270)} POST {req.RequestUri} failed; smtp login {password} refused");
        var composite = Composite([Slack(new ScriptedHandler(echo, echo, echo, echo), url)], outbox,
            logger: factory.CreateLogger<CompositeNotificationService>());

        await composite.SendAsync(new NotificationEvent
        {
            EventType = "automation_suspended", ContainerName = "AutoUpdate",
            ImageInfo = $"posting to {url} failed; the mail password is {password}",
        });
        Assert.True(await outbox.FlushAsync());
        await composite.SendAsync(new NotificationEvent { EventType = "automation_suspended", ContainerName = "AutoPatch" });
        Assert.True(await outbox.FlushAsync());                 // a second generation: the first is now the .bak

        var working = new ScriptedHandler();
        var worker = new NotificationOutboxWorker([Slack(working, url)], outbox, factory.CreateLogger<NotificationOutboxWorker>(),
            TimeSpan.FromSeconds(5));
        await worker.RedeliverDueAsync(DateTime.UtcNow.AddMinutes(2), CancellationToken.None);
        Assert.Equal(2, working.Requests);

        Assert.True(File.Exists(paths.NotificationOutboxJson + ".bak"));
        var seen = new List<string>(logs.Lines) { working.LastBody };
        foreach (var file in new[] { paths.NotificationOutboxJson, paths.NotificationOutboxJson + ".bak" })
            seen.Add(await File.ReadAllTextAsync(file));
        foreach (var text in seen)
            foreach (var canary in canaries)
                Assert.DoesNotContain(canary[..10], text);     // not the value, nor its first characters
        Assert.Contains(logs.Lines, l => l.Contains("hooks.slack.example", StringComparison.Ordinal)
                                         && l.Contains("HttpRequestException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_redelivery_is_on_disk_before_the_next_starts_and_so_is_an_addition()
    {
        var path = Path.Combine(_dir, "outbox.json");
        var outbox = new NotificationOutbox(NotificationOutbox.Store(path), NullLogger<NotificationOutbox>.Instance);
        var now = DateTime.UtcNow;
        foreach (var name in new[] { "one", "two", "three" })
            await outbox.AddAsync("Slack", Stopped(name), new HttpRequestException("500"), now);
        Assert.True(await outbox.FlushAsync());

        var atTwo = new TaskCompletionSource();
        var releaseTwo = new TaskCompletionSource();
        var atThree = new TaskCompletionSource();
        var releaseThree = new TaskCompletionSource();
        var handler = new ScriptedHandler(Status(200),
            async (_, _) => { atTwo.SetResult(); await releaseTwo.Task; return new HttpResponseMessage(HttpStatusCode.OK); },
            async (_, _) => { atThree.SetResult(); await releaseThree.Task; return new HttpResponseMessage(HttpStatusCode.OK); });
        var worker = new NotificationOutboxWorker([Slack(handler)], outbox, NullLogger<NotificationOutboxWorker>.Instance,
            TimeSpan.FromSeconds(30));

        var pass = worker.RedeliverDueAsync(now.AddMinutes(2), CancellationToken.None);
        await atTwo.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["two", "three"], await OnDiskAsync(path));          // the first delivery is written already
        await outbox.AddAsync("Slack", Stopped("new"), new HttpRequestException("500"), now);

        releaseTwo.SetResult();
        await atThree.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["three", "new"], await OnDiskAsync(path));          // and the addition, before the third is sent

        releaseThree.SetResult();
        await pass.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["new"], await OnDiskAsync(path));
    }

    [Fact]
    public async Task An_entry_that_ran_out_of_time_during_the_pass_is_not_sent_and_a_wait_counts_from_the_failure()
    {
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        var now = DateTime.UtcNow;
        var clock = now;
        await outbox.AddAsync("Slack", Stopped("slow"), new HttpRequestException("500"), now - TimeSpan.FromHours(1));
        await outbox.AddAsync("Slack", Stopped("late"), new HttpRequestException("500"),
            now - NotificationOutbox.MaxAge + TimeSpan.FromSeconds(5));             // five seconds left when the pass starts
        var handler = new ScriptedHandler((_, _) =>
        {
            clock += TimeSpan.FromSeconds(20);                                      // the first delivery takes 20 s, then fails
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        });
        var worker = new NotificationOutboxWorker([Slack(handler)], outbox, NullLogger<NotificationOutboxWorker>.Instance,
            TimeSpan.FromSeconds(30)) { Clock = () => clock };

        await worker.RedeliverDueAsync(now, CancellationToken.None);

        Assert.Equal(1, handler.Requests);                                         // "late" was too old by its turn
        Assert.Equal(1, outbox.Status.Exhausted);
        var failedAt = now + TimeSpan.FromSeconds(20);
        Assert.Empty(await outbox.DueAsync(failedAt + NotificationOutbox.WaitAfter(2) - TimeSpan.FromSeconds(1)));
        Assert.Single(await outbox.DueAsync(failedAt + NotificationOutbox.WaitAfter(2)));
    }

    [Fact]
    public async Task An_entry_dropped_from_a_full_outbox_during_the_pass_is_not_sent()
    {
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        var now = DateTime.UtcNow;
        await outbox.AddAsync("Slack", Stopped("first"), new HttpRequestException("500"), now - TimeSpan.FromHours(2));
        await outbox.AddAsync("Slack", Stopped("second"), new HttpRequestException("500"), now - TimeSpan.FromHours(1));
        var atFirst = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var handler = new ScriptedHandler(async (_, _) =>
        {
            atFirst.SetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var worker = new NotificationOutboxWorker([Slack(handler)], outbox, NullLogger<NotificationOutboxWorker>.Instance,
            TimeSpan.FromSeconds(30));

        var pass = worker.RedeliverDueAsync(now, CancellationToken.None);
        await atFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < NotificationOutbox.MaxEntries; i++)                    // a burst pushes both out
            await outbox.AddAsync("Slack", Stopped($"burst{i}"), new HttpRequestException("500"), now);
        release.SetResult();
        await pass.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, handler.Requests);                                         // "second" was not sent from its copy
        Assert.Equal(2, outbox.Status.Dropped);
        Assert.False(await outbox.CompleteAsync("no-such-entry", delivered: true));
        Assert.Equal(0, outbox.Status.Redelivered);                                // "first" was dropped meanwhile
    }

    [Theory]
    [InlineData("{\"Entries\":[null]}")]
    [InlineData("{\"Entries\":[{\"Id\":\"a\",\"Channel\":\"Slack\",\"Event\":null}]}")]
    [InlineData("{\"Entries\":[{\"Id\":\"\",\"Channel\":\"Slack\",\"Event\":{}}]}")]
    public async Task A_file_that_parses_but_is_no_outbox_is_preserved_and_the_previous_generation_used(string broken)
    {
        var path = Path.Combine(_dir, "outbox.json");
        var first = new NotificationOutbox(NotificationOutbox.Store(path), NullLogger<NotificationOutbox>.Instance);
        await first.AddAsync("Slack", Stopped("kept"), new HttpRequestException("500"), DateTime.UtcNow);
        Assert.True(await first.FlushAsync());
        Assert.True(await first.FlushAsync());
        await first.AddAsync("Slack", Stopped("later"), new HttpRequestException("500"), DateTime.UtcNow);
        Assert.True(await first.FlushAsync());                                   // the .bak holds "kept"
        await File.WriteAllTextAsync(path, broken);

        var outbox = new NotificationOutbox(NotificationOutbox.Store(path), NullLogger<NotificationOutbox>.Instance);
        Assert.True(await outbox.FlushAsync());

        Assert.Equal(["kept"], (await outbox.DueAsync(DateTime.UtcNow.AddHours(1))).Select(e => e.Event.ContainerName));
        Assert.Single(Directory.GetFiles(_dir, "outbox.json.corrupt-*"));        // the broken file is kept for a look
    }

    [Theory]
    [InlineData("{\"Entries\":[null]}")]
    [InlineData("{\"Entries\":[{\"Id\":\"a\",\"Channel\":\"Slack\",\"Event\":null}]}")]
    public async Task A_broken_file_without_a_previous_generation_does_not_block_new_notifications(string broken)
    {
        var path = Path.Combine(_dir, "outbox.json");
        await File.WriteAllTextAsync(path, broken);
        var outbox = new NotificationOutbox(NotificationOutbox.Store(path), NullLogger<NotificationOutbox>.Instance);
        var working = new ScriptedHandler();
        var worker = new NotificationOutboxWorker([Slack(working)], outbox, NullLogger<NotificationOutboxWorker>.Instance,
            TimeSpan.FromSeconds(5));

        Assert.True(await outbox.FlushAsync());
        Assert.Single(Directory.GetFiles(_dir, "outbox.json.corrupt-*"));
        await outbox.AddAsync("Slack", Stopped("new"), new HttpRequestException("500"), DateTime.UtcNow);
        await worker.RedeliverDueAsync(DateTime.UtcNow.AddMinutes(2), CancellationToken.None);

        Assert.Equal(1, working.Requests);
        Assert.Equal(0, outbox.Status.Pending);
    }

    [Fact]
    public async Task An_outbox_whose_file_could_not_be_read_yet_reports_itself_unsaved()
    {
        var path = Path.Combine(_dir, "outbox.json");
        await File.WriteAllTextAsync(path, "{\"Entries\":[]}");
        var outbox = new NotificationOutbox(NotificationOutbox.Store(path), NullLogger<NotificationOutbox>.Instance);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await outbox.FlushAsync());
            Assert.Equal(0, outbox.Status.Pending);
            Assert.True(outbox.Status.Unsaved);                // nothing new arrived, the backlog is still unknown
        }
        Assert.True(await outbox.FlushAsync());
        Assert.False(outbox.Status.Unsaved);
    }

    [Fact]
    public async Task A_full_outbox_gives_up_an_alarm_before_an_approval_or_an_administrative_event()
    {
        var outbox = NotificationOutbox.InMemory(NullLogger<NotificationOutbox>.Instance);
        var now = DateTime.UtcNow;
        await outbox.AddAsync("Slack", new NotificationEvent { EventType = "agent_approval", ApprovalId = "a1" },
            new HttpRequestException("503"), now.AddMinutes(-10));
        await outbox.AddAsync("Slack", new NotificationEvent { EventType = "automation_suspended", ContainerName = "AutoUpdate" },
            new HttpRequestException("503"), now.AddMinutes(-9));
        for (var i = 0; i < NotificationOutbox.MaxEntries; i++)                    // a burst of alarms
            await outbox.AddAsync("Slack", Stopped($"c{i}"), new HttpRequestException("503"), now.AddSeconds(i));

        var kept = (await outbox.DueAsync(now.AddHours(1))).Select(e => e.Event.EventType).ToList();
        Assert.Contains("agent_approval", kept);
        Assert.Contains("automation_suspended", kept);
        Assert.Equal(NotificationOutbox.MaxEntries, kept.Count);
    }

    [Fact]
    public async Task What_came_in_while_the_file_cannot_be_read_is_still_redelivered()
    {
        var path = Path.Combine(_dir, "outbox.json");
        await File.WriteAllTextAsync(path, "{\"Entries\":[]}");
        var outbox = new NotificationOutbox(NotificationOutbox.Store(path), NullLogger<NotificationOutbox>.Instance);
        var working = new ScriptedHandler();
        var worker = new NotificationOutboxWorker([Slack(working)], outbox, NullLogger<NotificationOutboxWorker>.Instance,
            TimeSpan.FromSeconds(5));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await outbox.FlushAsync());
            await outbox.AddAsync("Slack", Stopped("new"), new HttpRequestException("500"), DateTime.UtcNow);
            await worker.RedeliverDueAsync(DateTime.UtcNow.AddMinutes(2), CancellationToken.None);
            Assert.Equal(1, working.Requests);
            Assert.Equal(0, outbox.Status.Pending);
        }
        Assert.True(await outbox.FlushAsync());
        Assert.Empty(await outbox.DueAsync(DateTime.UtcNow.AddHours(1)));       // not sent twice once the file is read
    }

    private static async Task<List<string>> OnDiskAsync(string path) =>
        ((await new Whiskers.Services.Persistence.JsonFileStore<NotificationOutbox.OutboxFile>(path).LoadAsync()).Entries ?? [])
        .Select(e => e.Event.ContainerName).ToList();

    // ---- helpers -------------------------------------------------------------------------------------------------

    internal static SlackNotificationService Slack(HttpMessageHandler handler, string url = SlackUrl) =>
        new(new HttpClient(handler), new StaticOptionsMonitor<SlackSettings>(new SlackSettings
        {
            Enabled = true, WebhookUrl = url, ThrottleMinutes = 0,
        }), NullLogger<SlackNotificationService>.Instance);

    internal static CompositeNotificationService Composite(IEnumerable<INotificationChannel> channels,
        INotificationOutbox? outbox, TimeSpan? attemptTimeout = null, IAiTriggerDispatcher? dispatcher = null,
        ILogger<CompositeNotificationService>? logger = null) =>
        new(channels, new NullInApp(), new AllowAll(),
            new ServiceCollection().AddSingleton(dispatcher ?? new CountingDispatcher(hang: false)).BuildServiceProvider(),
            logger ?? NullLogger<CompositeNotificationService>.Instance, outbox, attemptTimeout ?? TimeSpan.FromSeconds(5),
            TimeSpan.Zero);

    internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Status(int code) =>
        (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code));

    private static async Task<HttpResponseMessage> Hang(HttpRequestMessage _, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    /// <summary>Answers each request with the next script step; 200 once the script is used up.</summary>
    internal sealed class ScriptedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _steps = new(steps);
        private int _requests, _cancelled;
        public int Requests => Volatile.Read(ref _requests);
        public int Cancelled => Volatile.Read(ref _cancelled);
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? step;
            lock (_steps) step = _steps.Count > 0 ? _steps.Dequeue() : null;
            try
            {
                return step is null ? new HttpResponseMessage(HttpStatusCode.OK) : await step(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
        }
    }

    private sealed class CountingDispatcher(bool hang) : IAiTriggerDispatcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task OnEventAsync(NotificationEvent evt)
        {
            Interlocked.Increment(ref _calls);
            return hang ? Task.Delay(Timeout.Infinite) : Task.CompletedTask;
        }
    }

    private sealed class NullInApp : IInAppNotificationStore
    {
        public IReadOnlyList<InAppNotification> Recent => [];
        public int UnreadCount => 0;
        public void Add(NotificationEvent evt) { }
        public void MarkAllRead() { }
        public void Clear() { }
        public event Action? Changed { add { } remove { } }
        public event Action<InAppNotification>? Added { add { } remove { } }
        public Task<List<InAppNotification>> QueryAsync(string? severity, string? eventType, int skip, int take,
            CancellationToken ct = default) => Task.FromResult(new List<InAppNotification>());
        public Task<int> CountAsync(string? severity, string? eventType, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class AllowAll : IContainerNotificationPrefsService
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ContainerNotifEntry GetPrefs(string containerName) => new();
        public bool ShouldNotify(string containerName, string eventType) => true;
        public Task SavePrefsAsync(string containerName, ContainerNotifEntry entry) => Task.CompletedTask;
    }

    internal sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
        public ILogger CreateLogger(string categoryName) => new ListLogger(this);
        public void Dispose() { }

        private sealed class ListLogger(ListLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines) owner._lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
            }
        }
    }
}

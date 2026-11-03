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

            // Bounded either way: SmtpClient.Timeout (SmtpException) or the cancellation (TimeoutException), whichever first.
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
        var composite = Composite([Slack(failing)], new NotificationOutbox(paths, NullLogger<NotificationOutbox>.Instance));
        await composite.SendAsync(Stopped());
        Assert.Equal(2, failing.Requests);

        // The process restarts: a new outbox reads the file; the channel works again.
        var outbox = new NotificationOutbox(paths, NullLogger<NotificationOutbox>.Instance);
        var working = new ScriptedHandler();
        var worker = new NotificationOutboxWorker([Slack(working)], outbox, NullLogger<NotificationOutboxWorker>.Instance,
            TimeSpan.FromSeconds(5));

        await worker.RedeliverDueAsync(DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(0, working.Requests);                    // not due yet: it waits before the first redelivery
        Assert.Equal(1, outbox.Status.Pending);

        await worker.RedeliverDueAsync(DateTime.UtcNow.AddMinutes(2), CancellationToken.None);
        Assert.Equal(1, working.Requests);
        Assert.Contains("web", working.LastBody);
        Assert.Equal(new NotificationOutboxStatus(0, 1, 0, 0), outbox.Status);
        Assert.Empty(await new NotificationOutbox(paths, NullLogger<NotificationOutbox>.Instance).DueAsync(DateTime.MaxValue));
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
        Assert.False(CompositeNotificationService.IsCritical(new NotificationEvent { EventType = "server_recovered" }));
    }

    [Fact]
    public async Task The_outbox_is_bounded_and_drops_its_oldest_entry()
    {
        var outbox = new NotificationOutbox(new Whiskers.Services.Persistence.JsonFileStore<NotificationOutbox.OutboxFile>(
            Path.Combine(_dir, "outbox.json")), NullLogger<NotificationOutbox>.Instance);
        var start = DateTime.UtcNow;
        for (var i = 0; i <= NotificationOutbox.MaxEntries; i++)
            await outbox.AddAsync("Slack", Stopped($"c{i}"), new HttpRequestException("500"), start.AddSeconds(i));

        Assert.Equal(new NotificationOutboxStatus(NotificationOutbox.MaxEntries, 0, 0, 1), outbox.Status);
        var due = await outbox.DueAsync(DateTime.MaxValue);
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

        Assert.Equal(new NotificationOutboxStatus(0, 0, 0, 0), outbox.Status);   // gone, but not counted as delivered
    }

    [Fact]
    public async Task The_ai_trigger_dispatch_runs_once_and_is_bounded()
    {
        var dispatcher = new CountingDispatcher(hang: true);
        var composite = Composite([], null, TimeSpan.FromMilliseconds(200), dispatcher);

        await composite.SendAsync(Stopped()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, dispatcher.Calls);
    }

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

using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Modules.Notifications;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Approvals;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Startup;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>R1-25 (INV-51): the app log and every outbound channel get only redacted text — whatever a caller logs or a
/// producer sends: secrets in messages, structured values, templates, scopes, exceptions, URLs, arguments and model
/// summaries. An entry that cannot be redacted is withheld, never written as it was.</summary>
public sealed partial class SinkRedactionTests
{
    // ---- logging -------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_log_provider_receives_only_redacted_entries()
    {
        var (factory, capture) = Logging();
        var logger = factory.CreateLogger("Test");

        using (logger.BeginScope("Request {Path}", $"/callback?token={Canary}"))
        {
            logger.LogWarning(new InvalidOperationException($"connect failed: password={Canary}",
                    new HttpRequestException($"POST {TelegramUrl} failed")),
                "Calling {Url} with {Command}", TelegramUrl, $"mysql -p{Canary}");
#pragma warning disable CA2254 // a caller that builds the template itself must not leak either
            logger.LogError($"raw template Authorization: Bearer {Canary}");
#pragma warning restore CA2254
        }

        Assert.Contains("Calling", capture.All);
        Assert.Contains("***", capture.All);
        AssertClean(capture.All);
        Assert.DoesNotContain(TelegramToken, capture.All);
        Assert.Contains(typeof(InvalidOperationException).FullName!, capture.All);
    }

    [Fact]
    public void An_entry_that_cannot_be_redacted_is_withheld_not_written_raw()
    {
        var (factory, capture) = Logging();

        factory.CreateLogger("Test").Log(LogLevel.Error, default, new UnreadableState(), null,
            (_, _) => $"password={Canary}");

        Assert.Equal(RedactingLoggerFactory.WithheldEntry, Assert.Single(capture.Messages));
        AssertClean(capture.All);
    }

    [Fact]
    public void Plain_scopes_and_list_values_are_redacted_and_disposal_reaches_the_real_factory()
    {
        var capture = new Capture();
        var sp = new ServiceCollection().AddLogging(b => b.AddProvider(capture)).AddLogRedaction().BuildServiceProvider();
        var factory = Assert.IsType<RedactingLoggerFactory>(sp.GetRequiredService<ILoggerFactory>());
        var logger = factory.CreateLogger("Test");

        using (logger.BeginScope($"session token={Canary}"))
            logger.LogWarning("Arguments {Args}", (object)new[] { "kept", $"password={Canary}" });

        Assert.Contains("kept", capture.All);
        AssertClean(capture.All);
        sp.Dispose();
        Assert.Throws<ObjectDisposedException>(() => factory.Inner.CreateLogger("after"));
    }

    [Fact]
    public void The_in_app_feed_redacts_what_it_is_given_directly()
    {
        var store = new InAppNotificationStore(new ThrowingScopes(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InAppNotificationStore>.Instance);

        store.Add(new NotificationEvent { EventType = "log_alert", ContainerName = "web", ImageInfo = string.Join(" ", SecretTexts) });

        var item = Assert.Single(store.Recent);
        AssertClean(item.Detail);
        AssertClean(item.Title);
    }

    [Fact]
    public void The_redaction_keeps_each_providers_own_filter_rules()
    {
        var capture = new Capture();
        using var sp = new ServiceCollection()
            .AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace).AddFilter<Capture>(null, LogLevel.Warning))
            .AddLogRedaction()
            .BuildServiceProvider();
        var factory = sp.GetRequiredService<ILoggerFactory>();
        var logger = sp.GetRequiredService<ILogger<SinkRedactionTests>>();

        logger.LogInformation("below the provider's floor");
        logger.LogWarning("at the provider's floor password={Secret}", Canary);

        Assert.IsType<RedactingLoggerFactory>(factory);
        Assert.Equal("at the provider's floor password=***", Assert.Single(capture.Messages));
    }

    [Fact]
    public void The_http_client_request_log_stays_at_warning_against_a_configured_default()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace", ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Information",
        });
        builder.AddWhiskersLogRedaction();
        using var sp = builder.Services.BuildServiceProvider();

        var factory = sp.GetRequiredService<ILoggerFactory>();
        Assert.False(factory.CreateLogger("System.Net.Http.HttpClient.MatrixNotificationService.LogicalHandler")
            .IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger("Whiskers.Services.Notifications.MatrixNotificationService")
            .IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void A_channel_secret_saved_later_is_withheld_after_the_settings_reload()
    {
        var source = new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource();
        var config = new ConfigurationBuilder().Add(source).Build();
        var services = new ServiceCollection();
        new NotificationsModule().ConfigureServices(services, config);
        using var sp = services.BuildServiceProvider();
        var settings = sp.GetRequiredService<IOptionsMonitor<WebhookNotificationSettings>>();
        _ = settings.CurrentValue;

        var saved = $"https://Inbox.Example.org:443/later/{Guid.NewGuid():N}";
        config["WebhookNotification:Url"] = saved;
        config.Reload();
        _ = settings.CurrentValue;

        Assert.Equal("POST *** failed", SecretRedactor.Redact($"POST {saved} failed"));
        // A request or an exception prints the normalized form: lower-case host, default port dropped.
        Assert.Equal("POST *** failed", SecretRedactor.Redact($"POST {new Uri(saved).AbsoluteUri} failed"));
    }

    [Fact]
    public async Task The_audit_fallback_log_is_redacted_even_without_the_redacting_provider()
    {
        var capture = new Capture();
        using var sp = new ServiceCollection().AddLogging(b => b.AddProvider(capture)).BuildServiceProvider();
        var audit = new AuditLogService(new ThrowingScopes(), sp.GetRequiredService<ILogger<AuditLogService>>());

        await audit.LogAsync("ops@example.com", "web", "server.exec", "server", $"mysql -p{Canary}", $"mysql -p{Canary}",
            details: JsonSerializer.Serialize(new { command = $"PGPASSWORD={Canary} psql", note = $"token={Canary}" }),
            serverId: "srv1");

        Assert.Contains("AUDIT WRITE FAILED", capture.All);
        AssertClean(capture.All);
    }

    // ---- outbound channels ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_outbound_channel_sends_and_logs_only_redacted_text()
    {
        var (sp, capture, handler) = Channels();
        await using var _ = sp;
        var inApp = new RecordingInApp();
        var dispatcher = new RecordingDispatcher();
        var composite = new CompositeNotificationService(sp.GetServices<INotificationChannel>(), inApp, new NoPrefs(),
            new ServiceCollection().AddSingleton<IAiTriggerDispatcher>(dispatcher).BuildServiceProvider(),
            sp.GetRequiredService<ILogger<CompositeNotificationService>>());

        var evt = new NotificationEvent
        {
            EventType = "log_alert", ContainerId = "c1", ContainerName = "web", ServerId = "srv1", ServerName = "prod",
            ImageInfo = "matched: " + string.Join("\n", SecretTexts),
        };
        await composite.SendAsync(evt);

        // Seven HTTP channels (e-mail is off: SMTP is not stubbed, and it formats the same redacted event).
        Assert.Equal(7, handler.Requests.Select(r => r.Uri.Host).Distinct().Count());
        Assert.All(handler.Requests, r => AssertClean(r.Body));
        Assert.All(handler.Requests, r => Assert.Contains("***", r.Body));
        AssertClean(Assert.Single(inApp.Events).ImageInfo);
        AssertClean(Assert.Single(dispatcher.Events).ImageInfo);
        Assert.Contains("matched", evt.ImageInfo);   // the producer's own object is left as it was
        Assert.Contains(Canary, evt.ImageInfo);

        // Three providers failed, with the token-bearing URL in the exception text; the log shows none of them.
        Assert.Contains("Failed to send Telegram notification", capture.All);
        Assert.Contains("Failed to send ntfy notification", capture.All);
        Assert.Contains("Failed to send webhook notification", capture.All);
        AssertClean(capture.All);
        foreach (var secret in ChannelSecrets) Assert.DoesNotContain(secret, capture.All);
    }

    [Fact]
    public async Task A_failed_channel_test_reports_no_token_bearing_url()
    {
        var (sp, _, _) = Channels();
        await using var __ = sp;
        var composite = new CompositeNotificationService(sp.GetServices<INotificationChannel>(), new RecordingInApp(),
            new NoPrefs(), sp, sp.GetRequiredService<ILogger<CompositeNotificationService>>());

        var error = await Assert.ThrowsAsync<AggregateException>(composite.SendTestAsync);

        Assert.Contains("Telegram", error.Message);
        foreach (var secret in ChannelSecrets) Assert.DoesNotContain(secret, error.Message);
    }

    [Theory]
    [MemberData(nameof(CapabilityUrls))]
    public void A_capability_url_keeps_its_host_and_loses_its_secret(string url, string secret)
    {
        var redacted = SecretRedactor.Redact($"POST {url} failed");

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("***", redacted);
        Assert.Equal(redacted, SecretRedactor.Redact(redacted));
    }

    public static TheoryData<string, string> CapabilityUrls() => new()
    {
        { TelegramUrl, TelegramToken },
        { $"https://discord.com/api/webhooks/123456/{Token('d')}", Token('d') },
        { $"https://hooks.slack.com/services/T0001/B0001/{Token('s')}", Token('s') },
        { $"https://chat.example.org/hooks/{Token('m')}", Token('m') },
    };

    // ---- trigger summary and approvals -------------------------------------------------------------------------

    [Fact]
    public async Task A_trigger_summary_is_redacted_before_it_is_sent()
    {
        var notify = new RecordingNotify();
        var policy = new GuardrailPolicy { MaxAutonomousLevel = McpPermissionLevels.Read };
        var sp = new ServiceCollection()
            .AddSingleton<IAgentService>(new SummaryAgents(
                new AgentEvent.AssistantDelta("Done. " + string.Join(" ", SecretTexts)),
                new AgentEvent.Failed($"LLM 401: api_key={Canary}")))
            .AddSingleton<IGuardrailStore>(new PolicyDecisionRecordTests.PresetStore("Watch",
                new GuardrailPreset { Name = "Watch", Policy = policy }))
            .AddSingleton<IAuditLogService>(new NullAudit())
            .AddSingleton<INotificationService>(notify)
            .AddSingleton<IAiTriggerAuthority>(new ReadOnlyRuns())
            .BuildServiceProvider();
        var dispatcher = new AiTriggerDispatcher(
            new TriggerSet(new AiTrigger { Id = "t1", Name = "watch", Enabled = true, EventTypes = ["unhealthy"], GuardrailPreset = "Watch" }),
            sp, new StaticAgentSettings());

        await dispatcher.OnEventAsync(new NotificationEvent
        {
            EventType = "unhealthy", ContainerId = "c1", ContainerName = "web", ServerId = "srv1",
        });
        var sent = await notify.Sent.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("agent_action", sent.EventType);
        Assert.Contains("Done.", sent.ImageInfo);
        AssertClean(sent.ImageInfo);
    }

    [Fact]
    public void An_approval_notification_carries_no_secret_argument()
    {
        var approval = new Approval
        {
            SessionId = "s", ToolCallId = "c", Actor = "key:k1", ActorType = "mcp-direct", ToolName = "execute_command",
            Reason = "r", Level = McpPermissionLevels.Admin,
            ParamsJson = JsonSerializer.Serialize(new { command = $"mysql -p{Canary}", password = Canary }),
        };

        var text = ApprovalDisplay.NotificationText(approval, ApprovalDisplay.NotifiedTarget("container", null));

        Assert.Contains("execute_command", text);
        AssertClean(text);
    }

    // ---- fixtures ------------------------------------------------------------------------------------------------

    // Built at run time: the secret guard must not mistake the test data for real tokens.
    private static string Token(char c) => "t" + new string(c, 31);
    private static readonly string TelegramToken = "7000001:" + new string('b', 35);
    private static readonly string TelegramUrl = $"https://api.telegram.org/bot{TelegramToken}/sendMessage";
    private static readonly string Unique = Guid.NewGuid().ToString("N");
    private static readonly string NtfyUrl = $"https://ntfy.example.org/alerts-{Unique}";
    private static readonly string WebhookUrl = $"https://inbox.example.org/in/{Unique}";
    private static readonly string MatrixToken = $"matrix-access-{Unique}";
    private static readonly string WebhookSecret = $"signing-key-{Unique}";

    private static readonly string[] ChannelSecrets =
        [TelegramToken, NtfyUrl, WebhookUrl, MatrixToken, WebhookSecret, Token('d'), Token('s'), Token('m')];

    private static (ILoggerFactory Factory, Capture Capture) Logging()
    {
        var capture = new Capture();
        var sp = new ServiceCollection()
            .AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace))
            .AddLogRedaction()
            .BuildServiceProvider();
        return (sp.GetRequiredService<ILoggerFactory>(), capture);
    }

    /// <summary>Every channel of the notifications module, enabled, behind one recording HTTP handler; the Telegram,
    /// ntfy and webhook endpoints fail with their full URL in the exception text.</summary>
    private static (ServiceProvider Services, Capture Capture, RecordingHandler Handler) Channels()
    {
        var capture = new Capture();
        var handler = new RecordingHandler("api.telegram.org", "ntfy.example.org", "inbox.example.org");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mattermost:Enabled"] = "true", ["Mattermost:WebhookUrl"] = $"https://chat.example.org/hooks/{Token('m')}",
            ["Matrix:Enabled"] = "true", ["Matrix:HomeserverUrl"] = "https://matrix.example.org",
            ["Matrix:AccessToken"] = MatrixToken, ["Matrix:RoomId"] = "!room:example.org",
            ["Telegram:Enabled"] = "true", ["Telegram:BotToken"] = TelegramToken, ["Telegram:ChatId"] = "42",
            ["Ntfy:Enabled"] = "true", ["Ntfy:ServerUrl"] = "https://ntfy.example.org", ["Ntfy:Topic"] = $"alerts-{Unique}",
            ["Discord:Enabled"] = "true", ["Discord:WebhookUrl"] = $"https://discord.com/api/webhooks/123456/{Token('d')}",
            ["Slack:Enabled"] = "true", ["Slack:WebhookUrl"] = $"https://hooks.slack.com/services/T0001/B0001/{Token('s')}",
            ["WebhookNotification:Enabled"] = "true", ["WebhookNotification:Url"] = WebhookUrl,
            ["WebhookNotification:Secret"] = WebhookSecret,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Trace));
        new NotificationsModule().ConfigureServices(services, config);
        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = handler));
        services.AddLogRedaction();
        return (services.BuildServiceProvider(), capture, handler);
    }

    private sealed class Capture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;
        public ConcurrentQueue<string> Messages { get; } = new();
        private readonly ConcurrentQueue<string> _texts = new();
        public string All => string.Join("\n", _texts);

        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        private sealed class Logger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                owner.Messages.Enqueue(message);
                owner._texts.Enqueue(message);
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    foreach (var (key, value) in values) owner._texts.Enqueue($"{key}={value}");
                if (exception is not null)
                {
                    owner._texts.Enqueue(exception.Message);
                    owner._texts.Enqueue(exception.ToString());
                }
                owner._scopes?.ForEachScope((scope, texts) =>
                {
                    texts.Enqueue(scope?.ToString() ?? "");
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        foreach (var (key, value) in pairs) texts.Enqueue($"{key}={value}");
                }, owner._texts);
            }
        }
    }

    /// <summary>A state whose values cannot be read — the redaction fails on it.</summary>
    private sealed class UnreadableState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public KeyValuePair<string, object?> this[int index] => throw new InvalidOperationException();
        public int Count => throw new InvalidOperationException();
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => $"password={Canary}";
    }

    private sealed class RecordingHandler(params string[] failingHosts) : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri Uri, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue((request.RequestUri!, body));
            if (failingHosts.Contains(request.RequestUri!.Host))
                throw new HttpRequestException($"{request.Method} {request.RequestUri} failed");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed class ThrowingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("database unavailable");
    }

    private sealed class RecordingInApp : IInAppNotificationStore
    {
        public List<NotificationEvent> Events { get; } = [];
        public IReadOnlyList<InAppNotification> Recent => [];
        public int UnreadCount => 0;
        public void Add(NotificationEvent evt) => Events.Add(evt);
        public void MarkAllRead() { }
        public void Clear() { }
        public event Action? Changed { add { } remove { } }
        public event Action<InAppNotification>? Added { add { } remove { } }
        public Task<List<InAppNotification>> QueryAsync(string? severity, string? eventType, int skip, int take,
            CancellationToken ct = default) => Task.FromResult(new List<InAppNotification>());
        public Task<int> CountAsync(string? severity, string? eventType, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoPrefs : IContainerNotificationPrefsService
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ContainerNotifEntry GetPrefs(string containerName) => new();
        public bool ShouldNotify(string containerName, string eventType) => true;
        public Task SavePrefsAsync(string containerName, ContainerNotifEntry entry) => Task.CompletedTask;
    }

    private sealed class RecordingDispatcher : IAiTriggerDispatcher
    {
        public ConcurrentQueue<NotificationEvent> Events { get; } = new();
        public Task OnEventAsync(NotificationEvent evt) { Events.Enqueue(evt); return Task.CompletedTask; }
    }

    private sealed class RecordingNotify : INotificationService
    {
        public TaskCompletionSource<NotificationEvent> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAsync(NotificationEvent evt) { Sent.TrySetResult(evt); return Task.CompletedTask; }
        public Task SendTestAsync() => Task.CompletedTask;
    }

    private sealed class SummaryAgents(params AgentEvent[] events) : IAgentService
    {
        public Task<IAgentSession> StartSessionAsync(AgentContext context, IReadOnlyList<AgentMessage>? seedHistory = null,
            CancellationToken ct = default) => Task.FromResult<IAgentSession>(new Session(events));

        public Task<IAgentSession?> ResumeSessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IAgentSession?>(null);

        private sealed class Session(AgentEvent[] events) : IAgentSession
        {
            public string SessionId => "run-1";
            public AgentRunState State => AgentRunState.Done;
            public IReadOnlyList<AgentMessage> History => [];

            public async IAsyncEnumerable<AgentEvent> SendAsync(string userMessage, string? imageBase64 = null,
                string? imageMediaType = null, [EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var e in events)
                {
                    await Task.Yield();
                    yield return e;
                }
            }

            public Task ResolveConfirmationAsync(string operationId, bool approved, CancellationToken ct = default) =>
                Task.CompletedTask;
        }
    }

    private sealed class ReadOnlyRuns : IAiTriggerAuthority
    {
        public Task<TriggerRunAuthority> BeginRunAsync(AiTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(new TriggerRunAuthority(McpPermissionLevels.Read, null));
        public Task<string?> RefusalAsync(string triggerId, Whiskers.Services.Mutations.Capability capability,
            Whiskers.Services.Mutations.MutationTarget target, CancellationToken ct) => Task.FromResult<string?>("read-only");
    }

    private sealed class NullAudit : IAuditLogService
    {
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.CompletedTask;
        public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.FromResult(true);
        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());
        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    private sealed class TriggerSet(params AiTrigger[] triggers) : IAiTriggerStore
    {
        public IReadOnlyList<AiTrigger> Triggers { get; } = triggers;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public long Revision => 0;
        public Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor, long readAtRevision) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class StaticAgentSettings : IOptionsMonitor<AgentSettings>
    {
        public AgentSettings CurrentValue { get; } = new() { Enabled = true };
        public AgentSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AgentSettings, string?> listener) => null;
    }
}

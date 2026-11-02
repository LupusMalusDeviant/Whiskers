using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Models.Cve;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Providers;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.AiChat;
using Whiskers.Services.AuditLog;
using Whiskers.Services.AutoPatch;
using Whiskers.Services.Cve;
using Whiskers.Services.Guardrails;
using Whiskers.Services.ImageUpdate;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using PresetStore = Whiskers.Tests.PolicyDecisionRecordTests.PresetStore;

namespace Whiskers.Tests;

/// <summary>INV-40 (R1-38): nothing waits without end. A provider that stalls before or during its answer is cut off
/// with a timeout the caller sees, an unattended run ends at its deadline and frees its slot, the advisor chat gives up
/// on a silent provider, a preview started from a tool is owned and stopped with the host, and a call stopped while it
/// ran never reads as "nothing happened".</summary>
public class ExecutionDeadlineTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // ---- Providers ---------------------------------------------------------

    [Fact]
    public async Task A_provider_that_sends_no_headers_times_out()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var provider = Provider(handler, new ProviderTimeouts(Short, Wait, Wait));

        var ex = await Assert.ThrowsAsync<ProviderTimeoutException>(() => Drain(provider));

        Assert.Contains("no response", ex.Message);
        Assert.True(handler.SawCancellation);
    }

    [Fact]
    public async Task A_provider_that_stops_sending_mid_answer_times_out_and_its_response_is_released()
    {
        var body = new DripStream(["data: {\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\n\n"], gap: TimeSpan.Zero,
            hangAfterwards: true);
        var provider = Provider(Responding(body), new ProviderTimeouts(Wait, Short, Wait));

        var ex = await Assert.ThrowsAsync<ProviderTimeoutException>(() => Drain(provider));

        Assert.Contains("stopped sending", ex.Message);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task A_provider_that_trickles_forever_hits_the_total_deadline()
    {
        var chunk = "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\n";
        var body = new DripStream(Enumerable.Repeat(chunk, 10_000), gap: TimeSpan.FromMilliseconds(20),
            hangAfterwards: false);
        var provider = Provider(Responding(body), new ProviderTimeouts(Wait, Wait, TimeSpan.FromMilliseconds(400)));

        var ex = await Assert.ThrowsAsync<ProviderTimeoutException>(() => Drain(provider));

        Assert.Contains("took longer", ex.Message);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task The_callers_cancellation_stays_a_cancellation_not_a_timeout()
    {
        var body = new DripStream([], gap: TimeSpan.Zero, hangAfterwards: true);
        var provider = Provider(Responding(body), new ProviderTimeouts(Wait, Wait, Wait));
        using var cts = new CancellationTokenSource(Short);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Drain(provider, cts.Token));

        Assert.IsNotType<ProviderTimeoutException>(ex);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task A_model_list_that_never_arrives_times_out()
    {
        var body = new DripStream([], gap: TimeSpan.Zero, hangAfterwards: true);
        var provider = Provider(Responding(body), new ProviderTimeouts(Wait, Wait, Short));

        await Assert.ThrowsAsync<ProviderTimeoutException>(() => provider.ListModelsAsync());
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task An_error_body_that_never_arrives_still_fails_the_call_with_its_status()
    {
        var body = new DripStream([], gap: TimeSpan.Zero, hangAfterwards: true);
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StreamContent(body),
        }));
        var provider = Provider(handler, new ProviderTimeouts(Wait, Short, Wait));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Drain(provider)).WaitAsync(Wait);

        Assert.Contains("502", ex.Message);
        Assert.True(body.Disposed);
    }

    // ---- Unattended trigger runs -------------------------------------------

    [Fact]
    public async Task A_stalled_trigger_run_is_cancelled_at_its_deadline_frees_its_slot_and_says_so()
    {
        var agents = new HangingAgents();
        var notify = new CapturingNotify();
        var dispatcher = TriggerDispatcher(agents, notify, Short);

        // As many stalled runs as there are slots: each ends at its deadline …
        foreach (var container in new[] { "c1", "c2", "c3" })
            await dispatcher.OnEventAsync(Event(container));
        for (var i = 0; i < 3; i++)
            await notify.Next(Wait);

        Assert.Equal(3, agents.Cancelled);
        Assert.All(notify.Summaries, s => Assert.Contains("timed out", s));

        // … and gives its slot back: the next run starts.
        await dispatcher.OnEventAsync(Event("c4"));
        await notify.Next(Wait);
        Assert.Equal(4, agents.Started);
    }

    [Fact]
    public async Task A_run_that_ends_quietly_on_its_deadline_is_still_reported_as_timed_out()
    {
        var agents = new HangingAgents(quiet: true);
        var notify = new CapturingNotify();
        var dispatcher = TriggerDispatcher(agents, notify, Short);

        await dispatcher.OnEventAsync(Event("c1"));
        var summary = await notify.Next(Wait);

        Assert.Equal(1, agents.Cancelled);
        Assert.Contains("timed out", summary);
    }

    // ---- Advisor chat ------------------------------------------------------

    [Fact]
    public async Task The_advisor_chat_gives_up_on_a_silent_provider()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var chat = AdvisorChat(handler);

        var answer = await chat.ChatAsync("hi").WaitAsync(Wait);

        Assert.Contains("did not answer", answer);
        Assert.True(handler.SawCancellation);
    }

    [Fact]
    public async Task Closing_the_chat_ends_the_advisor_call()
    {
        var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var chat = AdvisorChat(handler, timeout: Wait);
        using var cts = new CancellationTokenSource(Short);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.ChatAsync("hi", ct: cts.Token));
        Assert.True(handler.SawCancellation);
    }

    [Fact]
    public async Task The_advisor_takes_a_fresh_client_from_the_factory_for_each_answer()
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", Encoding.UTF8,
                "application/json"),
        }));
        var factory = new Factory(handler);
        var chat = AdvisorChat(factory);

        Assert.Equal("ok", await chat.ChatAsync("one"));
        Assert.Equal("ok", await chat.ChatAsync("two"));
        Assert.Equal(2, factory.Created);
        Assert.All(factory.Names, n => Assert.Equal(AiChatService.HttpClientName, n));
    }

    // ---- Gateway -----------------------------------------------------------

    [Fact]
    public async Task A_call_cancelled_while_it_ran_reports_an_unknown_effect()
    {
        var store = new PresetStore("Live", new GuardrailPreset { Name = "Live", Policy = Open() });
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store),
            new AgentToolTestHelpers.NullMutationAudit(), [], NullLogger<MutationGateway>.Instance,
            triggers: AgentToolTestHelpers.GrantAll.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancellation = false;
        using var cts = new CancellationTokenSource();

        var run = gateway.ExecuteAsync(StartCall(), async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { sawCancellation = true; throw; }
            return 0;
        }, cts.Token);
        await entered.Task.WaitAsync(Wait);
        await cts.CancelAsync();
        var outcome = await run.WaitAsync(Wait);

        Assert.True(sawCancellation);
        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Equal(VerificationStatus.NotRun, outcome.Verification);
        Assert.Contains("effect is unknown", outcome.Reason);
    }

    // ---- Owned AutoPatch preview -------------------------------------------

    [Fact]
    public async Task A_preview_is_owned_bounded_by_its_deadline_and_frees_the_cycle_afterwards()
    {
        var runner = new HangingRunner();
        var service = AutoPatch(runner);

        Assert.True(service.StartPreview("admin@example.com", Short));
        Assert.False(service.StartPreview("admin@example.com", Short));   // one cycle at a time
        await service.Preview!.WaitAsync(Wait);

        Assert.True(runner.SawCancellation);
        Assert.True(service.StartPreview("admin@example.com", Short));    // the slot came back
        await service.Preview!.WaitAsync(Wait);
    }

    [Fact]
    public async Task Stopping_the_host_cancels_a_running_preview_and_waits_for_it()
    {
        var runner = new HangingRunner();
        var service = AutoPatch(runner);

        Assert.True(service.StartPreview("admin@example.com"));
        await runner.Entered.Task.WaitAsync(Wait);
        await service.StopAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.True(runner.SawCancellation);
        Assert.True(service.Preview!.IsCompleted);
        Assert.False(service.StartPreview("admin@example.com"));         // a stopped service starts nothing
    }

    // ---- Helpers -----------------------------------------------------------

    private static OpenAiCompatibleProvider Provider(HttpMessageHandler handler, ProviderTimeouts timeouts) =>
        new(new HttpClient(handler), "openai", "k", "http://llm.test/v1/chat/completions", timeouts);

    private static async Task Drain(IAgentLlmProvider provider, CancellationToken ct = default)
    {
        var request = new AgentCompletionRequest("m", "system", [new AgentMessage(AgentRole.User, "hi")],
            Array.Empty<AgentToolDefinition>(), 100, 0.2, AgentToolChoice.Auto);
        await foreach (var _ in provider.StreamAsync(request, ct)) { }
    }

    private static Handler Responding(Stream body) => new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(body),
    }));

    private static AiChatService AdvisorChat(Handler handler, TimeSpan? timeout = null) =>
        AdvisorChat(new Factory(handler), timeout);

    private static AiChatService AdvisorChat(Factory factory, TimeSpan? timeout = null) =>
        new(factory, new StaticOptionsMonitor<AiChatSettings>(new AiChatSettings
        {
            Enabled = true, ApiKey = "k", Provider = "openai", Model = "m", ApiUrl = "http://llm.test/v1/chat/completions",
        }), new FakeDocker(), NullLogger<AiChatService>.Instance)
        {
            RequestTimeout = timeout ?? Short,
        };

    private static GuardrailPolicy Open() => new()
    {
        MaxAutonomousLevel = McpPermissionLevels.Write,
        RequireConfirmationForWrites = false,
    };

    private static MutationRequest StartCall()
    {
        var start = new CapabilityCatalog().Find("container.start")!;
        var trigger = new AgentPrincipal(AgentPrincipalKind.Trigger, "AI trigger 'restart'", McpPermissionLevels.Write,
            null, TriggerId: "t1");
        return new MutationRequest(start.Id, MutationPrincipals.Agent(trigger, "run-1"), MutationSurface.Agent,
            new MutationTarget("container", "c1", "web", "srv1"), new Dictionary<string, JsonElement>
            {
                ["containerId"] = JsonSerializer.SerializeToElement("web"),
                ["serverId"] = JsonSerializer.SerializeToElement("srv1"),
            }, SessionId: "run-1") { PinnedPolicy = PinnedPolicy.Of("Live", Open()) };
    }

    private static NotificationEvent Event(string container) => new()
    {
        EventType = "unhealthy", ContainerId = container, ContainerName = container, ServerId = "srv1",
    };

    private static AiTriggerDispatcher TriggerDispatcher(IAgentService agents, INotificationService notify,
        TimeSpan deadline)
    {
        var store = new PresetStore("Live", new GuardrailPreset { Name = "Live", Policy = Open() });
        var sp = new ServiceCollection()
            .AddSingleton(agents)
            .AddSingleton<IGuardrailStore>(store)
            .AddSingleton<IAuditLogService>(new NullAudit())
            .AddSingleton(notify)
            .AddSingleton<IAiTriggerAuthority>(new ReadOnlyRuns())
            .BuildServiceProvider();
        var triggers = new TriggerList(new AiTrigger
        {
            Id = "t1", Name = "restart", EventTypes = ["unhealthy"], GuardrailPreset = "Live", CooldownSeconds = 0,
        });
        return new AiTriggerDispatcher(triggers, sp,
            new StaticOptionsMonitor<AgentSettings>(new AgentSettings { Enabled = true }))
        {
            RunDeadline = deadline,
        };
    }

    private static AutoPatchService AutoPatch(IAutoPatchRunner runner)
    {
        var store = new CveFindingsStore(persistPath: AutoPatchTests.TempPath()) { LastScanAt = DateTime.UtcNow };
        store.Set(AutoPatchTests.Scan("a", "local", (CveSeverity.Critical, "1")));
        return AutoPatchTests.NewService(runner, store, new PassThroughAutomation(), new MemoryAutomationOwners(),
            new AutoPatchSettings { Enabled = true, DryRun = true }, AutoPatchTests.Compose("a"));
    }

    /// <summary>Answers each request with the given function; notes whether the request's token fired.</summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public bool SawCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try { return await respond(request, ct); }
            catch (OperationCanceledException) { SawCancellation = true; throw; }
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public int Created { get; private set; }
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Created++;
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    /// <summary>A response body that hands out its chunks one by one, <c>gap</c> apart, then ends or hangs until the
    /// reader gives up. Records whether the response that owned it was disposed.</summary>
    private sealed class DripStream(IEnumerable<string> chunks, TimeSpan gap, bool hangAfterwards) : Stream
    {
        private readonly IEnumerator<string> _chunks = chunks.GetEnumerator();
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_chunks.MoveNext())
            {
                if (gap > TimeSpan.Zero) await Task.Delay(gap, ct);
                var bytes = Encoding.UTF8.GetBytes(_chunks.Current);
                bytes.CopyTo(buffer);
                return bytes.Length;
            }
            if (hangAfterwards) await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Every run's session waits for its token and counts the cancellations. A <paramref name="quiet"/> session
    /// ends without throwing on the cancellation, as <see cref="AgentSession"/> does between two tool calls.</summary>
    private sealed class HangingAgents(bool quiet = false) : IAgentService
    {
        private readonly bool _quiet = quiet;
        private int _started, _cancelled;
        public int Started => Volatile.Read(ref _started);
        public int Cancelled => Volatile.Read(ref _cancelled);

        public Task<IAgentSession> StartSessionAsync(AgentContext context, IReadOnlyList<AgentMessage>? seedHistory = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _started);
            return Task.FromResult<IAgentSession>(new HangingSession(this));
        }

        public Task<IAgentSession?> ResumeSessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IAgentSession?>(null);

        private sealed class HangingSession(HangingAgents owner) : IAgentSession
        {
            public string SessionId => "s";
            public AgentRunState State => AgentRunState.Running;
            public IReadOnlyList<AgentMessage> History => [];

            public async IAsyncEnumerable<AgentEvent> SendAsync(string userMessage, string? imageBase64 = null,
                string? imageMediaType = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new AgentEvent.AssistantDelta("checking …");
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref owner._cancelled);
                    if (!owner._quiet) throw;
                }
            }

            public Task ResolveConfirmationAsync(string operationId, bool approved, CancellationToken ct = default) =>
                Task.CompletedTask;
        }
    }

    private sealed class CapturingNotify : INotificationService
    {
        private readonly System.Threading.Channels.Channel<string> _sent =
            System.Threading.Channels.Channel.CreateUnbounded<string>();
        public List<string> Summaries { get; } = [];

        public Task SendAsync(NotificationEvent evt)
        {
            lock (Summaries) Summaries.Add(evt.ImageInfo ?? "");
            _sent.Writer.TryWrite(evt.ImageInfo ?? "");
            return Task.CompletedTask;
        }

        public Task SendTestAsync() => Task.CompletedTask;
        public async Task<string> Next(TimeSpan wait) => await _sent.Reader.ReadAsync().AsTask().WaitAsync(wait);
    }

    private sealed class ReadOnlyRuns : IAiTriggerAuthority
    {
        public Task<TriggerRunAuthority> BeginRunAsync(AiTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(new TriggerRunAuthority(McpPermissionLevels.Read, null));
        public Task<string?> RefusalAsync(string triggerId, Capability capability, MutationTarget target, CancellationToken ct) =>
            Task.FromResult<string?>("read-only");
    }

    private sealed class NullAudit : IAuditLogService
    {
        public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.FromResult(true);
        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) => Task.CompletedTask;
        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());
        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    private sealed class TriggerList(params AiTrigger[] triggers) : IAiTriggerStore
    {
        public IReadOnlyList<AiTrigger> Triggers { get; } = triggers;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public long Revision => 0;
        public Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor, long readAtRevision) => Task.CompletedTask;
        public event Action? Changed { add { } remove { } }
    }

    /// <summary>Runs each patch with the token it is given, inside an operation scope as the gateway would.</summary>
    private sealed class PassThroughAutomation : IAutomationMutationRunner
    {
        public async Task<MutationOutcome<T>> RunAsync<T>(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            using var scope = MutationScope.Begin(capabilityId, "op");
            var value = await action(ct);
            return new MutationOutcome<T>("op", MutationStatus.Executed, value, null, VerificationStatus.NotRun);
        }

        public Task<MutationOutcome<bool>> RunAsync(AutomationActor actor, string capabilityId, object arguments,
            Func<CancellationToken, Task> action, CancellationToken ct = default) =>
            RunAsync<bool>(actor, capabilityId, arguments, async c => { await action(c); return true; }, ct);
    }

    /// <summary>A patch that never finishes on its own.</summary>
    private sealed class HangingRunner : IAutoPatchRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SawCancellation { get; private set; }

        public async Task<(AutoPatchOutcome Outcome, string LogTail)> PatchAsync(string serverId, string containerName,
            bool dryRun, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { SawCancellation = true; throw; }
            return (new AutoPatchOutcome(AutoPatchResultKind.WouldUpdate, ""), "");
        }
    }
}

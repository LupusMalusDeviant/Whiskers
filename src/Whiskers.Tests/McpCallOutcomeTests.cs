using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Whiskers.Configuration;
using Whiskers.Mcp;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Observability;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-20: the history of a direct MCP call records what the call came to — executed, denied, failed, waiting for
/// approval, failed verification — as the filter decided it, never the HTTP status of the transport. Each call of a
/// batch keeps its own outcome and operation id.
/// </summary>
public sealed class McpCallOutcomeTests : IDisposable
{
    private readonly McpFilterHarness _mcp = new();

    public void Dispose() => _mcp.Dispose();

    [Fact]
    public async Task An_executed_mutation_is_executed_with_its_operation_id()
    {
        var result = await _mcp.CallAsync("restart_container", McpFilterHarness.Ok);

        Assert.NotEqual(true, result.IsError);
        var outcome = Assert.Single(McpCallOutcomes.Get(_mcp.Http));
        Assert.Equal(McpCallResult.Executed, outcome.Result);
        Assert.True(outcome.Success);
        Assert.Equal("allow", outcome.Verdict);
        var row = Assert.Single(await _mcp.EventsAsync());
        Assert.Equal(row.OperationId, outcome.OperationId);
    }

    [Fact]
    public async Task A_denied_mutation_is_denied()
    {
        _mcp.Policy.ProtectedResources = ["web"];

        var ran = false;
        await _mcp.CallAsync("restart_container", _ => { ran = true; return McpFilterHarness.Ok(default); });

        Assert.False(ran);
        var outcome = Assert.Single(McpCallOutcomes.Get(_mcp.Http));
        Assert.Equal(McpCallResult.Denied, outcome.Result);
        Assert.Equal("deny", outcome.Verdict);
        Assert.NotNull(outcome.OperationId);
    }

    [Fact]
    public async Task A_tool_error_is_a_failure()
    {
        await _mcp.CallAsync("restart_container", _ => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "container not found" }],
        }));

        var outcome = Assert.Single(McpCallOutcomes.Get(_mcp.Http));
        Assert.Equal(McpCallResult.Failed, outcome.Result);
        Assert.Contains("container not found", outcome.Error);
        Assert.Equal("Failed", Assert.Single(await _mcp.EventsAsync()).Outcome);
    }

    [Fact]
    public async Task A_tool_that_throws_is_a_failure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _mcp.CallAsync("restart_container", _ => throw new InvalidOperationException("docker is gone")));

        var outcome = Assert.Single(McpCallOutcomes.Get(_mcp.Http));
        Assert.Equal(McpCallResult.Failed, outcome.Result);
        Assert.Equal("docker is gone", outcome.Error);
    }

    [Fact]
    public async Task A_mutation_waiting_for_approval_is_pending()
    {
        _mcp.Policy.RequireConfirmationForWrites = true;

        await _mcp.CallAsync("restart_container", McpFilterHarness.Ok);

        var outcome = Assert.Single(McpCallOutcomes.Get(_mcp.Http));
        Assert.Equal(McpCallResult.PendingApproval, outcome.Result);
        Assert.Equal("confirm", outcome.Verdict);
        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task A_mutation_whose_verification_failed_is_not_a_success()
    {
        _mcp.Verifiers.Add(new FailingVerifier("container.restart"));

        await _mcp.CallAsync("restart_container", McpFilterHarness.Ok);

        var outcome = Assert.Single(McpCallOutcomes.Get(_mcp.Http));
        Assert.Equal(McpCallResult.VerificationFailed, outcome.Result);
        Assert.False(outcome.Success);
        Assert.Equal("Failed", Assert.Single(await _mcp.EventsAsync()).Verification);
    }

    [Fact]
    public async Task A_read_refused_by_the_guardrails_is_denied()
    {
        _mcp.ReadVerdict = new DirectCallVerdict(false, GuardrailVerdict.Deny, "read-only mode");

        await _mcp.CallAsync("list_containers", McpFilterHarness.Ok);

        var outcome = Assert.Single(McpCallOutcomes.Get(_mcp.Http));
        Assert.Equal(McpCallResult.Denied, outcome.Result);
        Assert.Null(outcome.OperationId);
    }

    [Fact]
    public async Task A_read_that_answers_with_an_error_is_a_failure()
    {
        await _mcp.CallAsync("list_containers", _ => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "server unreachable" }],
        }));

        Assert.Equal(McpCallResult.Failed, Assert.Single(McpCallOutcomes.Get(_mcp.Http)).Result);
    }

    [Fact]
    public async Task A_failure_the_tool_reports_in_its_own_audit_fails_the_mutation_and_writes_no_second_row()
    {
        var legacy = new RecordingAuditLog();
        var tool = new ToolAuditLog(legacy);

        await _mcp.CallAsync("restart_container", async _ =>
        {
            await tool.LogAsync("someone", "mcp", "container.restart", "container", "web", "web", "exit code 1",
                "local", success: false);
            return new CallToolResult { Content = [new TextContentBlock { Text = "Restarted." }] };
        });

        Assert.Empty(legacy.Rows);
        Assert.Equal(McpCallResult.Failed, Assert.Single(McpCallOutcomes.Get(_mcp.Http)).Result);
        var row = Assert.Single(await _mcp.EventsAsync());
        Assert.Equal("Failed", row.Outcome);
        Assert.Contains("exit code 1", row.Details);
    }

    [Fact]
    public async Task Outside_a_mutation_the_tool_audit_is_an_ordinary_row()
    {
        var legacy = new RecordingAuditLog();

        await new ToolAuditLog(legacy).LogAsync("someone", "mcp", "report.export", "report", "r1", "r1");

        Assert.Equal("report.export", Assert.Single(legacy.Rows));
    }

    // ── The history (McpCallLogMiddleware) ─────────────────────────────────────

    [Fact]
    public async Task Transport_success_without_an_outcome_is_not_recorded_as_a_success()
    {
        var store = new RecordingCallLog();
        var http = Post(store, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"restart_container"}}""");

        await new McpCallLogMiddleware(ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; })
            .InvokeAsync(http);

        var entry = Assert.Single(store.Entries);
        Assert.False(entry.Success);
        Assert.Contains("No outcome", entry.Error);
    }

    [Fact]
    public async Task Each_call_of_a_batch_keeps_its_own_outcome_and_operation_id()
    {
        var store = new RecordingCallLog();
        var http = Post(store, """
            [{"jsonrpc":"2.0","id":"a","method":"tools/call","params":{"name":"restart_container","arguments":{"containerId":"web"}}},
             {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"restart_container","arguments":{"containerId":"db"}}},
             {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_containers"}}]
            """);

        await new McpCallLogMiddleware(ctx =>
        {
            // Reported out of order, as concurrent calls may finish.
            McpCallOutcomes.Add(ctx, new("3", "list_containers", McpCallResult.Executed, "allow", null, null));
            McpCallOutcomes.Add(ctx, new("2", "restart_container", McpCallResult.Denied, "deny", "op-db", "protected"));
            McpCallOutcomes.Add(ctx, new("a", "restart_container", McpCallResult.Executed, "allow", "op-web", null));
            return Task.CompletedTask;
        }).InvokeAsync(http);

        Assert.Equal(3, store.Entries.Count);
        var web = Assert.Single(store.Entries, e => e.CorrelationId == "op-web");
        Assert.True(web.Success);
        Assert.Contains("web", web.ParamsJson);
        var db = Assert.Single(store.Entries, e => e.CorrelationId == "op-db");
        Assert.False(db.Success);
        Assert.Equal("deny", db.Verdict);
        Assert.Contains("db", db.ParamsJson);
        Assert.True(Assert.Single(store.Entries, e => e.ToolName == "list_containers").Success);
    }

    [Fact]
    public async Task The_filter_reports_under_the_id_the_middleware_reads()
    {
        // The MCP server hands the filter a RequestId; its text must be what the middleware reads from the body.
        Assert.Equal("7", new RequestId(7).Id?.ToString());
        Assert.Equal("a", new RequestId("a").Id?.ToString());

        var store = new RecordingCallLog();
        var http = Post(store, """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"restart_container"}}""");

        await new McpCallLogMiddleware(async ctx =>
        {
            _mcp.Http = ctx;
            await _mcp.CallAsync("restart_container", McpFilterHarness.Ok, requestId: new RequestId(7).Id?.ToString());
        }).InvokeAsync(http);

        var entry = Assert.Single(store.Entries);
        Assert.True(entry.Success);
        Assert.Equal(Assert.Single(await _mcp.EventsAsync()).OperationId, entry.CorrelationId);
    }

    private static DefaultHttpContext Post(IMcpCallLogStore store, string body)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(store).BuildServiceProvider(),
        };
        http.Request.Method = "POST";
        http.Request.Path = "/mcp";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return http;
    }

    private sealed class FailingVerifier(string capabilityId) : IMutationVerifier
    {
        public string CapabilityId => capabilityId;

        public Task<VerificationStatus> VerifyAsync(MutationContext context, object? result, CancellationToken ct) =>
            Task.FromResult(VerificationStatus.Failed);
    }

    internal sealed class RecordingAuditLog : IAuditLogService
    {
        public List<string> Rows { get; } = [];

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            lock (Rows) Rows.Add(action);
            return Task.CompletedTask;
        }

        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) =>
            Task.FromResult(0);
    }

    private sealed class RecordingCallLog : IMcpCallLogStore
    {
        public List<McpToolCallEntity> Entries { get; } = [];

        public Task RecordAsync(McpToolCallEntity entry)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<List<McpToolCallEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actor = null,
            string? tool = null, string? verdict = null, bool writesOnly = false, DateTime? since = null) =>
            Task.FromResult(Entries.ToList());

        public Task<int> CountAsync(string? actor = null, string? tool = null, string? verdict = null,
            bool writesOnly = false, DateTime? since = null) => Task.FromResult(Entries.Count);
    }
}

/// <summary>Runs <see cref="McpGuardrailFilter"/> as a direct MCP call by an admin API key, against the real gateway
/// and audit sink on a SQLite database.</summary>
internal sealed class McpFilterHarness : IDisposable
{
    private readonly ApprovalDatabase _db = new();
    private readonly AgentToolTestHelpers.FixedGuardrailStore _store;
    private readonly ApprovalStore _approvals;

    public McpFilterHarness()
    {
        Policy = GuardrailPolicy.SafeDefault();
        Policy.ProtectedResources = [];
        Policy.ForbiddenArgPatterns = [];
        Policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        Policy.RequireConfirmationForWrites = false;
        _store = new AgentToolTestHelpers.FixedGuardrailStore(Policy);
        _approvals = new ApprovalStore(policy: _store, persistence: _db.Persistence());
    }

    public GuardrailPolicy Policy { get; }
    public HttpContext Http { get; set; } = new DefaultHttpContext();
    public List<IMutationVerifier> Verifiers { get; } = [];
    public DirectCallVerdict ReadVerdict { get; set; } = new(true, GuardrailVerdict.Allow, null);

    public static readonly AgentPrincipal Key =
        new(AgentPrincipalKind.McpKey, "ops-key", McpPermissionLevels.Admin, null, McpKeyId: "k1");

    public static ValueTask<CallToolResult> Ok(CancellationToken _) =>
        ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "done" }] });

    public Task<CallToolResult> CallAsync(string tool, Func<CancellationToken, ValueTask<CallToolResult>> next,
        object? arguments = null, string? requestId = null) =>
        McpGuardrailFilter.HandleAsync(Services(), new CallToolRequestParams
        {
            Name = tool,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(arguments ?? new { containerId = "web", serverId = "local" })),
        }, requestId, next, CancellationToken.None).AsTask();

    public async Task<List<AuditLogEntity>> EventsAsync()
    {
        using var scope = _db.Scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .OrderBy(a => a.Id).ToListAsync();
    }

    private IServiceProvider Services()
    {
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store),
            new AuditLogMutationSink(_db.Scopes), Verifiers, NullLogger<MutationGateway>.Instance,
            decisions: new DbPolicyDecisionStore(_db.Scopes), approvals: new ApprovalGate(_approvals, _store),
            journal: new DbOperationJournal(_db.Scopes));
        return new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = Http })
            .AddSingleton<IAgentPrincipalResolver>(new FixedResolver())
            .AddSingleton<IDirectMcpCallGuard>(new FixedGuard(this))
            .AddSingleton<ICapabilityCatalog>(new CapabilityCatalog())
            .AddSingleton<IMutationGateway>(gateway)
            .AddSingleton<ICapabilityOverrides>(new CodeLevels())
            .BuildServiceProvider();
    }

    public void Dispose() => _db.Dispose();

    private sealed class FixedResolver : IAgentPrincipalResolver
    {
        public AgentPrincipal Resolve(HttpContext httpContext) => Key;
    }

    private sealed class FixedGuard(McpFilterHarness harness) : IDirectMcpCallGuard
    {
        public Task<DirectCallVerdict> EvaluateAsync(AgentPrincipal principal, string toolName,
            IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct = default) =>
            Task.FromResult(harness.ReadVerdict);
    }

    private sealed class CodeLevels : ICapabilityOverrides
    {
        public CapabilityAvailability Resolve(Capability capability) => new(true, capability.RequiredLevel);
        public CapabilityAvailability ResolveTool(string toolName, string codeLevel) => new(true, codeLevel);
    }
}

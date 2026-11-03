using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Services.Observability.Correlation;
using Whiskers.Services.Persistence;
using Whiskers.Startup;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>
/// R1-53 (F-17): one request followed from the client's <c>X-Request-Id</c> through the Mutation Gateway's decision,
/// the approval, the execution and the audit row — by ids that stay the same in the log and in every store. The ids
/// pass the log redaction (R1-25) unchanged, a client-supplied id that is not safe is never used or echoed, and the
/// structured (JSON) console output is switched on by configuration alone.
/// </summary>
public sealed class OperationalCorrelationTests : IDisposable
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    /// <summary>A value that must never reach a log line; made at run time, it is no credential of anything.</summary>
    private static string Canary() => $"canary-{Guid.NewGuid():N}";

    private readonly ApprovalDatabase _db = new();
    private readonly GuardrailPolicy _policy;
    private readonly AgentToolTestHelpers.FixedGuardrailStore _store;
    private readonly ApprovalStore _approvals;
    private readonly LogCapture _capture = new();
    private readonly ServiceProvider _logging;
    private readonly ILoggerFactory _loggers;

    public OperationalCorrelationTests()
    {
        _policy = GuardrailPolicy.SafeDefault();
        _policy.ProtectedResources = [];
        _policy.ForbiddenArgPatterns = [];
        _policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        _policy.RequireConfirmationForWrites = true;
        _store = new AgentToolTestHelpers.FixedGuardrailStore(_policy);
        _approvals = new ApprovalStore(policy: _store, persistence: _db.Persistence());
        // The application's logging: every provider behind the redacting factory (R1-25).
        _logging = new ServiceCollection()
            .AddLogging(b => b.AddProvider(_capture).SetMinimumLevel(LogLevel.Trace))
            .AddLogRedaction()
            .BuildServiceProvider();
        _loggers = _logging.GetRequiredService<ILoggerFactory>();
    }

    public void Dispose()
    {
        _logging.Dispose();
        _db.Dispose();
    }

    private MutationGateway Gateway() =>
        new(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store), new AuditLogMutationSink(_db.Scopes), [],
            new Logger<MutationGateway>(_loggers), decisions: new DbPolicyDecisionStore(_db.Scopes),
            approvals: new ApprovalGate(_approvals, _store), journal: new DbOperationJournal(_db.Scopes));

    private static MutationRequest Restart() =>
        new("container.restart", Admin, MutationSurface.Mcp, new MutationTarget("container", "web", "web", "local"),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"containerId":"web","serverId":"local"}""")!);

    /// <summary>Runs <paramref name="work"/> as the endpoint of one HTTP request behind the correlation middleware;
    /// returns its result and the X-Request-Id the response carries.</summary>
    private async Task<(T Result, string Echoed)> InRequest<T>(string? clientId, Func<Task<T>> work)
    {
        var context = new DefaultHttpContext();
        if (clientId is not null) context.Request.Headers[RequestCorrelation.HeaderName] = clientId;
        T result = default!;
        var middleware = new RequestCorrelationMiddleware(async _ => result = await work(),
            new Logger<RequestCorrelationMiddleware>(_loggers));
        await middleware.InvokeAsync(context);
        return (result, context.Response.Headers[RequestCorrelation.HeaderName].ToString());
    }

    // ---------------------------------------------------------------- the path of one request

    [Fact]
    public async Task One_request_is_followed_through_decision_approval_execution_and_audit()
    {
        var gateway = Gateway();
        var docker = _loggers.CreateLogger("Whiskers.Services.Docker.DockerService");
        Task<int> Restarting(MutationContext _, CancellationToken __)
        {
            // A service the execution calls logs on its own logger: the operation's scope reaches it.
            docker.LogInformation("Restarting container {Container}", "web");
            return Task.FromResult(1);
        }

        // Request 1: the call asks, the policy wants a confirmation.
        var (asked, askEcho) = await InRequest("req-ask-1", () => gateway.ExecuteAsync(Restart(), Restarting));
        Assert.Equal(MutationStatus.PendingApproval, asked.Status);
        Assert.Equal("req-ask-1", askEcho);
        var operationId = asked.OperationId;

        // An administrator approves it on the Approvals page.
        Assert.Equal(ApprovalDecisionResult.Decided, await _approvals.DecideAsync(asked.ApprovalId!, true,
            ApprovalResolver.User("other-admin@example.com", AppRole.Admin)));

        // Request 2: the approved retry runs — the same operation.
        var (ran, runEcho) = await InRequest("req-run-2", () => gateway.ExecuteAsync(Restart(), Restarting));
        Assert.Equal(MutationStatus.Executed, ran.Status);
        Assert.Equal("req-run-2", runEcho);
        Assert.Equal(operationId, ran.OperationId);

        // The log: each request's decision names its request and the one operation id.
        var decisions = _capture.With("Verdict");
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, d => Assert.Equal(operationId, d.Value("OperationId")));
        Assert.Equal(new[] { "req-ask-1", "req-run-2" }, decisions.Select(d => d.Value("HttpRequestId")));
        Assert.Equal(new[] { asked.DecisionId, ran.DecisionId }, decisions.Select(d => d.Value("DecisionId")));

        // The wait for approval names the approval, under request 1's scope and the operation's scope.
        var waiting = Assert.Single(_capture.With("Status"), e => e.Value("Status") == nameof(MutationStatus.PendingApproval));
        Assert.Equal(asked.ApprovalId, waiting.Value("ApprovalId"));
        Assert.Equal("req-ask-1", waiting.Scoped("HttpRequestId"));
        Assert.Equal(operationId, waiting.Scoped("OperationId"));

        // The execution's own log line, written by another service, carries the operation and request 2.
        var execution = Assert.Single(_capture.Entries, e => e.Category == "Whiskers.Services.Docker.DockerService");
        Assert.Equal(operationId, execution.Scoped("OperationId"));
        Assert.Equal("container.restart", execution.Scoped("Capability"));
        Assert.Equal("req-run-2", execution.Scoped("HttpRequestId"));

        // The outcome line names the approval and the audit row.
        var outcome = Assert.Single(_capture.With("Status"), e => e.Value("Status") == nameof(MutationStatus.Executed));
        Assert.Equal(asked.ApprovalId, outcome.Value("ApprovalId"));
        Assert.Equal("req-run-2", outcome.Value("HttpRequestId"));
        var auditId = long.Parse(outcome.Value("AuditId")!);

        // The stores: decision, approval, journal and audit carry the same operation id and point at each other.
        Assert.Equal(operationId, _approvals.GetAll().Single(a => a.Id == asked.ApprovalId).CorrelationId);
        using var scope = _db.Scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        Assert.All(await db.PolicyDecisions.AsNoTracking().ToListAsync(), d => Assert.Equal(operationId, d.OperationId));
        var journal = await db.Operations.AsNoTracking().SingleAsync(o => o.Id == operationId);
        Assert.Equal(asked.ApprovalId, journal.ApprovalId);
        Assert.Equal(ran.DecisionId, journal.DecisionId);
        Assert.Equal(auditId, journal.AuditId);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(a => a.Id == auditId);
        Assert.Equal(operationId, audit.OperationId);
        Assert.Equal(asked.ApprovalId, audit.ApprovalId);
        Assert.Equal(ran.DecisionId, audit.DecisionId);
        Assert.Equal("outcome", audit.Phase);
    }

    [Fact]
    public async Task Work_outside_a_request_names_no_request_and_the_scope_ends_with_it()
    {
        var gateway = Gateway();
        _policy.RequireConfirmationForWrites = false;

        var outcome = await gateway.ExecuteAsync(Restart(), (_, _) => Task.FromResult(0));
        _loggers.CreateLogger("after").LogInformation("Afterwards");

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.NotEmpty(_capture.With("HttpRequestId"));
        Assert.All(_capture.With("HttpRequestId"), e => Assert.Equal("none", e.Value("HttpRequestId")));
        Assert.Null(RequestCorrelation.Current);
        var after = Assert.Single(_capture.Entries, e => e.Category == "after");
        Assert.Null(after.Scoped("OperationId"));
    }

    // ---------------------------------------------------------------- the id itself

    [Theory]
    [InlineData("req-1")]
    [InlineData("0af7651916cd43dd8448eb211c80319c")]
    [InlineData("client.retry_2-B")]
    public async Task A_safe_client_id_is_used_and_echoed(string clientId)
    {
        var (seen, echoed) = await InRequest(clientId, () => Task.FromResult(RequestCorrelation.Current));

        Assert.Equal(clientId, seen);
        Assert.Equal(clientId, echoed);
        Assert.Null(RequestCorrelation.Current);   // only for the request
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("line\r\nX-Injected: 1")]
    [InlineData("<script>")]
    [InlineData("key=value")]
    [InlineData("ümlaut")]
    public async Task An_unsafe_client_id_is_replaced_never_echoed_or_logged(string clientId)
    {
        var (seen, echoed) = await InRequest(clientId, () =>
        {
            _loggers.CreateLogger("work").LogInformation("Working");
            return Task.FromResult(RequestCorrelation.Current);
        });

        Assert.NotEqual(clientId, seen);
        Assert.Matches("^[0-9a-f]{32}$", seen!);
        Assert.Equal(seen, echoed);
        Assert.Equal(seen, Assert.Single(_capture.Entries).Scoped("HttpRequestId"));
        if (clientId.Length > 0) Assert.DoesNotContain(clientId, _capture.AllText);
    }

    [Fact]
    public async Task An_overlong_or_repeated_client_id_is_replaced()
    {
        var overlong = new string('a', RequestCorrelation.MaxLength + 1);
        var (seen, _) = await InRequest(overlong, () => Task.FromResult(RequestCorrelation.Current));
        Assert.NotEqual(overlong, seen);
        Assert.True(RequestCorrelation.IsAcceptable(new string('a', RequestCorrelation.MaxLength)));

        var context = new DefaultHttpContext();
        context.Request.Headers[RequestCorrelation.HeaderName] = new Microsoft.Extensions.Primitives.StringValues(["one", "two"]);
        await new RequestCorrelationMiddleware(_ => Task.CompletedTask, new Logger<RequestCorrelationMiddleware>(_loggers))
            .InvokeAsync(context);
        var chosen = context.Response.Headers[RequestCorrelation.HeaderName].ToString();
        Assert.Matches("^[0-9a-f]{32}$", chosen);
    }

    [Fact]
    public async Task A_client_id_the_redaction_would_change_is_never_used_echoed_or_logged()
    {
        // A secret Whiskers holds (R1-25 registers every channel credential) sent as the request id — by mistake or to
        // make Whiskers repeat it. It passes the charset, but the redaction would change it: it is not used.
        var held = Canary();
        KnownSecrets.Register(held);

        var (seen, echoed) = await InRequest(held, () =>
        {
            _loggers.CreateLogger("work").LogInformation("Working");
            return Task.FromResult(RequestCorrelation.Current);
        });

        Assert.NotEqual(held, seen);
        Assert.Equal(seen, echoed);
        Assert.DoesNotContain(held, _capture.AllText);
        Assert.False(RequestCorrelation.IsAcceptable(held));
    }

    [Fact]
    public async Task Without_a_client_id_the_trace_id_is_the_request_id()
    {
        // The framework's activity carries a valid inbound traceparent (or a new trace): its trace id correlates with
        // an upstream proxy's trace without a tracing backend.
        using var activity = new System.Diagnostics.Activity("request").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C);
        activity.SetParentId("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        activity.Start();

        var (seen, echoed) = await InRequest(null, () => Task.FromResult(RequestCorrelation.Current));

        Assert.Equal("0af7651916cd43dd8448eb211c80319c", seen);
        Assert.Equal(seen, echoed);
    }

    [Fact]
    public async Task Correlation_ids_pass_the_redaction_unchanged_and_secrets_beside_them_do_not()
    {
        var gateway = Gateway();
        _policy.RequireConfirmationForWrites = false;
        var held = Canary();
        var docker = _loggers.CreateLogger("Whiskers.Services.Docker.DockerService");

        var (outcome, _) = await InRequest("req-redact-1", () => gateway.ExecuteAsync(Restart(), (_, _) =>
        {
            docker.LogWarning("Registry login failed with password={Password} for {Container}", held, "web");
            return Task.FromResult(0);
        }));

        var line = Assert.Single(_capture.Entries, e => e.Category == "Whiskers.Services.Docker.DockerService");
        Assert.Equal(outcome.OperationId, line.Scoped("OperationId"));
        Assert.Equal("req-redact-1", line.Scoped("HttpRequestId"));
        Assert.Equal("***", line.Value("Password"));
        Assert.DoesNotContain(held, _capture.AllText);
        Assert.Contains("req-redact-1", _capture.AllText);
        Assert.Contains(outcome.OperationId, _capture.AllText);
    }

    // ---------------------------------------------------------------- structured output by configuration

    [Fact]
    public async Task Json_console_logs_are_switched_on_by_configuration_and_carry_the_correlation_ids()
    {
        // The supported switch: the standard console options, as environment variables or here as arguments
        // (Logging__Console__FormatterName=json, Logging__Console__FormatterOptions__IncludeScopes=true).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            Args = ["--Logging:Console:FormatterName=json", "--Logging:Console:FormatterOptions:IncludeScopes=true"],
        });
        var sink = new FormatterSink();
        builder.Logging.AddProvider(sink);
        builder.AddWhiskersLogRedaction();
        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;
        Assert.Equal(ConsoleFormatterNames.Json, options.FormatterName);
        sink.Formatter = app.Services.GetServices<ConsoleFormatter>().Single(f => f.Name == options.FormatterName);

        var loggers = app.Services.GetRequiredService<ILoggerFactory>();
        Assert.IsType<RedactingLoggerFactory>(loggers);
        var held = Canary();
        var context = new DefaultHttpContext();
        context.Request.Headers[RequestCorrelation.HeaderName] = "req-json-1";
        await new RequestCorrelationMiddleware(_ =>
        {
            var gatewayLog = loggers.CreateLogger<MutationGateway>();
            using (gatewayLog.BeginScope("Operation {OperationId} ({Capability})", "op-json-1", "container.restart"))
                loggers.CreateLogger("Whiskers.Services.Docker.DockerService")
                    .LogWarning("Restart of {Container} failed with password={Password}", "web", held);
            return Task.CompletedTask;
        }, loggers.CreateLogger<RequestCorrelationMiddleware>()).InvokeAsync(context);

        var output = sink.Output;
        Assert.DoesNotContain(held, output);
        var entry = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Single(e => e.GetProperty("Category").GetString() == "Whiskers.Services.Docker.DockerService");
        Assert.Equal("Warning", entry.GetProperty("LogLevel").GetString());
        Assert.Equal("web", entry.GetProperty("State").GetProperty("Container").GetString());
        Assert.Equal("***", entry.GetProperty("State").GetProperty("Password").GetString());
        var scopes = entry.GetProperty("Scopes").EnumerateArray().ToList();
        Assert.Contains(scopes, s => s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty("HttpRequestId", out var v) && v.GetString() == "req-json-1");
        Assert.Contains(scopes, s => s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty("OperationId", out var v) && v.GetString() == "op-json-1");
    }

    // ---------------------------------------------------------------- the pipeline

    [Fact]
    public void The_correlation_middleware_runs_first_and_leaves_the_auth_chain_in_its_order()
    {
        // PROTECTED: forwarded headers, then Antiforgery → Authentication → MCP bearer → Authorization. The correlation
        // middleware is added in front of all of it, so nothing in that chain moves.
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "src", "Whiskers", "Startup", "WhiskersPipelineExtensions.cs"));
        string[] order =
        [
            "app.UseMiddleware<Whiskers.Services.Observability.Correlation.RequestCorrelationMiddleware>()",
            "app.UseForwardedHeaders(",
            "app.UseAntiforgery()",
            "app.UseAuthentication()",
            "app.UseMiddleware<Whiskers.Mcp.McpBearerAuthMiddleware>()",
            "app.UseAuthorization()",
        ];
        var positions = order.Select(call => source.IndexOf(call, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Equal(positions[0], source.IndexOf("app.Use", StringComparison.Ordinal));
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }

    // ---------------------------------------------------------------- capture

    /// <summary>One written entry: its structured values and, innermost last, the values of its scopes.</summary>
    private sealed record Captured(string Category, string Message, IReadOnlyList<KeyValuePair<string, object?>> State,
        IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Scopes)
    {
        public string? Value(string key) =>
            State.Where(p => p.Key == key)
                .Select(p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture))
                .FirstOrDefault();

        public string? Scoped(string key) =>
            Scopes.SelectMany(s => s).Where(p => p.Key == key).Select(p => p.Value?.ToString()).LastOrDefault();
    }

    private sealed class LogCapture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;
        private readonly ConcurrentQueue<Captured> _entries = new();

        public IReadOnlyList<Captured> Entries => _entries.ToList();

        /// <summary>The entries with a structured value named <paramref name="key"/>, in order.</summary>
        public IReadOnlyList<Captured> With(string key) => Entries.Where(e => e.State.Any(p => p.Key == key)).ToList();

        public string AllText => string.Join("\n", Entries.Select(e =>
            e.Message + " " + string.Join(" ", e.State.Concat(e.Scopes.SelectMany(s => s)).Select(p => $"{p.Key}={p.Value}"))));

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        private sealed class Logger(LogCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
                owner._scopes?.ForEachScope((scope, list) =>
                    list.Add(scope is IEnumerable<KeyValuePair<string, object?>> pairs
                        ? pairs.ToList()
                        : [new("Message", scope?.ToString())]), scopes);
                var values = state is IEnumerable<KeyValuePair<string, object?>> kv
                    ? kv.ToList()
                    : new List<KeyValuePair<string, object?>>();
                owner._entries.Enqueue(new Captured(category, formatter(state, exception), values, scopes));
            }
        }
    }

    /// <summary>Writes every entry with the console formatter the configuration selected, as the console would.</summary>
    private sealed class FormatterSink : ILoggerProvider, ISupportExternalScope
    {
        private readonly StringWriter _output = new();
        private IExternalScopeProvider? _scopes;

        public ConsoleFormatter? Formatter { get; set; }

        public string Output
        {
            get { lock (_output) return _output.ToString(); }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public void Dispose() { }

        private sealed class Logger(FormatterSink owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                if (owner.Formatter is not { } f) return;
                lock (owner._output)
                    f.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter), owner._scopes,
                        owner._output);
            }
        }
    }
}

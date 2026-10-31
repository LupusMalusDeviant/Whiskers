using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Whiskers.Configuration;
using Whiskers.Mcp;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.AiChat;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Auth;
using Whiskers.Services.Server;
using Whiskers.Services.Persistence;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>R1-24 (INV-51): no persistent sink keeps a secret — neither whole nor a prefix of it — whoever wrote the
/// row: the database tables through the save interceptor, the agent transcript and the chat history in their stores.
/// Free-text payloads keep only their length; the classification covers every real MCP parameter name.</summary>
public sealed class SinkRedactionTests
{
    // Not token-shaped on purpose: the secret guard must not mistake the test data for a real secret.
    private const string Canary = "canary-value-r124";
    private const string CanaryPrefix = "canary-va";

    /// <summary>Secrets in every syntax a sink sees: keyed, SQL literal, URL user info, bearer header, multiline.</summary>
    private static readonly string[] SecretTexts =
    [
        $"password={Canary}",
        $"ALTER ROLE app PASSWORD '{Canary}';",
        $"git clone https://deploy:{Canary}@git.example.org/repo.git",
        $"Authorization: Bearer {Canary}",
        $"line one\nexport PGPASSWORD={Canary}\nline three",
    ];

    private static void AssertClean(string? stored)
    {
        Assert.NotNull(stored);
        Assert.DoesNotContain(CanaryPrefix, stored);
    }

    public static TheoryData<string> Secrets() => new(SecretTexts);

    // ── Arguments ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Payload_arguments_keep_only_their_length()
    {
        var stored = SinkRedaction.Arguments(JsonSerializer.Serialize(new
        {
            serverId = "local",
            command = $"echo {Canary}",
            composeContent = $"services: {{ a: {{ image: x, environment: [ K={Canary} ] }} }}",
            envVars = new Dictionary<string, string> { ["K"] = Canary },
        }));

        AssertClean(stored);
        using var document = JsonDocument.Parse(stored!);
        Assert.Equal("local", document.RootElement.GetProperty("serverId").GetString());
        Assert.Equal($"<withheld: {$"echo {Canary}".Length} chars>", document.RootElement.GetProperty("command").GetString());
        Assert.StartsWith("<withheld: ", document.RootElement.GetProperty("envVars").GetString());
    }

    [Theory]
    [InlineData("compose_content")]
    [InlineData("composeYaml")]
    [InlineData("docker-compose")]
    [InlineData("existingContent")]
    [InlineData("ENV_VARS")]
    [InlineData("Command")]
    public void Payload_names_match_whatever_their_spelling(string name)
    {
        Assert.True(SinkRedaction.IsPayload(name));
        AssertClean(SinkRedaction.Arguments($"{{\"{name}\":\"echo {Canary}\"}}"));
    }

    [Theory]
    [InlineData("<withheld: export PGPASSWORD=canary-value-r124>")]
    [InlineData("<withheld: 12 chars> canary-value-r124 <withheld: 3 chars>")]
    [InlineData("<withheld: canary-value-r124 chars>")]
    public void A_payload_dressed_as_the_marker_is_withheld_like_any_other(string payload)
    {
        var stored = SinkRedaction.Arguments(JsonSerializer.Serialize(new { command = payload, prompt = payload }));

        AssertClean(stored);
        using var document = JsonDocument.Parse(stored!);
        Assert.Equal($"<withheld: {payload.Length} chars>", document.RootElement.GetProperty("command").GetString());
        Assert.DoesNotContain(CanaryPrefix, SinkRedaction.WithoutPayloads(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { command = payload }))!)["command"]?.ToString());
    }

    [Fact]
    public void Only_the_exact_marker_counts_as_withheld()
    {
        var stored = SinkRedaction.Arguments(JsonSerializer.Serialize(new { command = "<withheld: 3 chars>\n" }));
        Assert.Contains("\"command\":\"<withheld: 20 chars>\"", stored);
    }

    [Fact]
    public void A_reason_loses_the_values_of_object_and_list_payloads_too()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            envVars = new Dictionary<string, string> { ["DB_PASS"] = Canary },
            volumes = new[] { $"/srv/{Canary}:/data" },
            containerId = "web",
        }))!;

        var reason = SinkRedaction.WithoutPayloadValues($"refused: {Canary} and /srv/{Canary}:/data on web", arguments);

        AssertClean(reason);
        Assert.Contains("on web", reason);
    }

    [Fact]
    public void Overlapping_payload_values_leave_no_rest_in_a_reason()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            volumes = new[] { "/srv/app", $"/srv/app-{Canary}:/data" },
        }))!;

        var reason = SinkRedaction.WithoutPayloadValues($"refused: /srv/app-{Canary}:/data", arguments);

        Assert.Equal("refused: <withheld>", reason);
    }

    [Fact]
    public void Payload_values_that_overlap_without_containing_each_other_go_as_one()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
        {
            volumes = new[] { $"/srv/app-{Canary}", $"{Canary}:/data" },
        }))!;

        var reason = SinkRedaction.WithoutPayloadValues($"a /srv/app-{Canary}:/data b {Canary}:/data", arguments);

        Assert.Equal("a <withheld> b <withheld>", reason);
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_secret_in_an_ordinary_argument_is_redacted(string secret) =>
        AssertClean(SinkRedaction.Arguments(JsonSerializer.Serialize(new { serverId = "local", description = secret })));

    [Fact]
    public void A_secret_named_key_hides_everything_below_it()
    {
        var stored = SinkRedaction.Arguments(JsonSerializer.Serialize(new
        {
            settings = new { apiKey = Canary, nested = new { credentials = new[] { Canary } } },
        }));

        AssertClean(stored);
        Assert.Contains("\"apiKey\":\"***\"", stored);
        Assert.Contains("\"credentials\":\"***\"", stored);
    }

    [Fact]
    public void A_shortened_json_document_stays_valid_and_redacts_again_to_itself()
    {
        var arguments = SinkRedaction.Arguments(JsonSerializer.Serialize(new
        {
            description = "rotate token: monthly " + new string('d', 3000),
            pattern = "password=",
            command = new string('c', 5000),
            serverId = "local",
        }))!;

        var capped = SinkRedaction.Cap(arguments, 400)!;

        Assert.True(capped.Length <= 400);
        using (var document = JsonDocument.Parse(capped))
            Assert.Equal("local", document.RootElement.GetProperty("serverId").GetString());
        Assert.Equal(capped, SinkRedaction.Json(capped));
        Assert.Equal(capped, SinkRedaction.Arguments(capped));
    }

    [Theory]
    [InlineData("plain text that is far too long for the column", 20)]
    [InlineData("{not json but starts like it, and it is long enough", 20)]
    [InlineData("[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20]", 12)]
    public void Cap_never_exceeds_its_limit(string text, int max) =>
        Assert.True(SinkRedaction.Cap(text, max)!.Length <= max);

    [Fact]
    public void Redaction_is_idempotent()
    {
        var once = SinkRedaction.Arguments(JsonSerializer.Serialize(new { command = $"echo {Canary}", note = SecretTexts[1] }));
        Assert.Equal(once, SinkRedaction.Arguments(once));
        var text = SinkRedaction.Json(SecretTexts[2]);
        Assert.Equal(text, SinkRedaction.Json(text));
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Free_text_and_json_documents_are_redacted(string secret)
    {
        AssertClean(SinkRedaction.Text(secret));
        AssertClean(SinkRedaction.Json(JsonSerializer.Serialize(new { output = secret, lines = new[] { secret } })));
    }

    [Fact]
    public void A_url_token_without_a_user_is_removed()
    {
        AssertClean(SinkRedaction.Text($"cloned https://{Canary}@github.com/org/repo"));
    }

    // ── Database sinks ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Every_sink_column_is_redacted_on_save(string secret)
    {
        using var db = Context();
        var args = JsonSerializer.Serialize(new { command = secret, description = secret });
        var audit = new AuditLogEntity { Action = "x", Details = secret, TargetId = secret, TargetName = secret, Arguments = args };
        var call = new McpToolCallEntity { ToolName = "x", ParamsJson = args, ResultSummary = secret, Error = secret };
        var approval = new ApprovalRecordEntity { ParamsJson = args, Reason = secret, Diff = secret };
        var decision = new PolicyDecisionEntity { Reason = secret };
        var run = new TaskRunHistoryEntity { Output = secret, Error = secret };
        var task = new ScheduledTaskEntity { LastResult = secret };
        var identity = new AuditLogEntity
        {
            Action = "x", Details = JsonSerializer.Serialize(new { principal = new { kind = "ApiKey", id = "key:k1", keyId = "k1" } }),
        };
        db.AddRange(audit, call, approval, decision, run, task, identity);

        SinkRedactionInterceptor.Redact(db);

        // The credential's id is no secret and stays readable.
        Assert.Contains("\"keyId\":\"k1\"", identity.Details);

        foreach (var value in new[]
                 {
                     audit.Details, audit.TargetId, audit.TargetName, audit.Arguments, call.ParamsJson, call.ResultSummary, call.Error,
                     approval.ParamsJson, approval.Reason, approval.Diff, decision.Reason, run.Output, run.Error,
                     task.LastResult,
                 })
            AssertClean(value);
    }

    [Fact]
    public void A_row_already_redacted_is_left_byte_identical()
    {
        // The audit chain adds no link for a write that left the row as it was: redacting again must change nothing.
        using var db = Context();
        var audit = new AuditLogEntity
        {
            Action = "x", TargetId = "web-prod", TargetName = SecretTexts[2],
            Details = JsonSerializer.Serialize(new { result = SecretTexts[1], note = "it's done" }),
            Arguments = JsonSerializer.Serialize(new { command = $"echo {Canary}", sql = SecretTexts[1], serverId = "local" }),
        };
        db.Add(audit);
        SinkRedactionInterceptor.Redact(db);
        var first = (audit.TargetId, audit.TargetName, audit.Details, audit.Arguments);

        SinkRedactionInterceptor.Redact(db);

        Assert.Equal(first, (audit.TargetId, audit.TargetName, audit.Details, audit.Arguments));
        Assert.Equal("web-prod", audit.TargetId);
    }

    [Fact]
    public async Task An_audit_row_redacted_on_save_is_chained_as_stored_and_the_trail_verifies()
    {
        // The chain hashes the row as it was stored: redaction on save must never look like tampering.
        using var trail = new AuditTrailFixture(AuditHashChainTests.VaultKey, redacting: true);
        Assert.True(await trail.Audit.TryLogAtAsync(DateTime.UtcNow, "admin@local.test", "mcp", "server.exec", "command",
            $"export PGPASSWORD={Canary}", $"git clone https://deploy:{Canary}@git.example.org/r.git",
            details: JsonSerializer.Serialize(new { output = SecretTexts[1] }), serverId: "s1"));
        await trail.Chain.CheckpointAsync();

        using (var scope = trail.Scope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().SingleAsync();
            AssertClean(row.TargetId);
            AssertClean(row.TargetName);
            AssertClean(row.Details);
        }
        var report = await trail.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(1, report.LinksChecked);
    }

    [Fact]
    public async Task An_audit_row_changed_later_is_redacted_and_chained_as_stored()
    {
        // The outcome write path: an existing row changed under the chain's UpdateAsync, with the interceptor on.
        using var trail = new AuditTrailFixture(AuditHashChainTests.VaultKey, redacting: true);
        var id = Assert.Single(await trail.LogAsync(1));
        using (var scope = trail.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await trail.Chain.UpdateAsync(db, [id], async token =>
            {
                var row = await db.AuditLog.SingleAsync(a => a.Id == id, token);
                row.Details = JsonSerializer.Serialize(new { result = $"failed: password={Canary}" });
                await db.SaveChangesAsync(token);
                return (true, (IReadOnlyCollection<long>)[id]);
            });
        }
        await trail.Chain.CheckpointAsync();

        using (var scope = trail.Scope())
            AssertClean((await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().SingleAsync()).Details);
        var report = await trail.Chain.VerifyAsync();
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.Equal(2, report.LinksChecked);
    }

    [Fact]
    public async Task An_executed_command_reaches_the_audit_log_only_as_its_length()
    {
        // No pattern knows curl's "-u user:pass": the command must not be stored at all, not even cut.
        var command = $"curl -u deploy:{Canary}{new string('x', 200)} https://api.example.org/hook";
        var audit = new CapturingAudit();
        var http = new DefaultHttpContextAccessor(AuthConstants.AgentSyntheticScheme);

        await Whiskers.Mcp.Tools.ServerTools.ExecuteCommand(http, null!, new OkExecutor(), audit, "local", command);

        var (targetId, targetName, details) = Assert.Single(audit.Calls);
        Assert.Equal($"<withheld: {command.Length} chars>", targetId);
        Assert.Equal(targetId, targetName);
        AssertClean(details ?? "");
    }

    private sealed class CapturingAudit : IToolAuditLog
    {
        public List<(string TargetId, string TargetName, string? Details)> Calls { get; } = [];

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            Calls.Add((targetId, targetName, details));
            return Task.CompletedTask;
        }
    }

    private sealed class OkExecutor : IHostCommandExecutor
    {
        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null) =>
            Task.FromResult(new CommandResult { ExitCode = 0, Output = "ok" });
    }

    /// <summary>An in-process agent call at Admin level, as the agent's synthetic context carries it.</summary>
    private sealed class DefaultHttpContextAccessor : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        public DefaultHttpContextAccessor(string scheme) =>
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(AuthConstants.McpLevelClaim, McpPermissionLevels.Admin)], scheme)),
            };

        public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }
    }

    [Fact]
    public void An_approval_keeps_its_payload_readable_for_the_approver()
    {
        using var db = Context();
        var approval = new ApprovalRecordEntity
        {
            ParamsJson = JsonSerializer.Serialize(new { sql = $"ALTER ROLE app PASSWORD '{Canary}';" }),
        };
        db.Add(approval);

        SinkRedactionInterceptor.Redact(db);

        AssertClean(approval.ParamsJson);
        Assert.Contains("ALTER ROLE app PASSWORD '***';", approval.ParamsJson);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void The_registered_context_carries_the_interceptor(string provider)
    {
        var data = Directory.CreateTempSubdirectory("sw-sink-");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WHISKERS_DB_PROVIDER"] = provider,
                ["WHISKERS_DB_CONNECTION"] = provider == "postgres" ? "Host=localhost;Database=none" : null,
            });
            builder.AddWhiskersDatabase(new DataPathOptions(data.FullName));
            using var services = builder.Services.BuildServiceProvider();
            using var db = services.GetRequiredService<MetricsDbContext>();

            var interceptors = db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
            Assert.Contains(SinkRedactionInterceptor.Instance, interceptors ?? []);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            data.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_row_saved_through_the_registered_context_is_stored_redacted()
    {
        var data = Directory.CreateTempSubdirectory("sw-sink-");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.AddWhiskersDatabase(new DataPathOptions(data.FullName));
            using var services = builder.Services.BuildServiceProvider();
            await using (var db = services.GetRequiredService<MetricsDbContext>())
            {
                await db.Database.EnsureCreatedAsync();
                db.McpToolCalls.Add(new McpToolCallEntity
                {
                    ToolName = "execute_command", Timestamp = DateTime.UtcNow,
                    ParamsJson = JsonSerializer.Serialize(new { command = $"echo {Canary}" }),
                    Error = $"failed: password={Canary}",
                });
                await db.SaveChangesAsync();
            }

            await using (var read = services.GetRequiredService<MetricsDbContext>())
            {
                var row = await read.McpToolCalls.AsNoTracking().SingleAsync();
                AssertClean(row.ParamsJson);
                AssertClean(row.Error);
            }

            // A tracked row changed later (an outcome written to an existing row) is redacted on that save too.
            await using (var update = services.GetRequiredService<MetricsDbContext>())
            {
                var tracked = await update.McpToolCalls.SingleAsync();
                tracked.ResultSummary = $"done, token: {Canary}";
                await update.SaveChangesAsync();
            }
            await using var reread = services.GetRequiredService<MetricsDbContext>();
            AssertClean((await reread.McpToolCalls.AsNoTracking().SingleAsync()).ResultSummary);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            data.Delete(recursive: true);
        }
    }

    // ── File sinks ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Secrets))]
    public async Task The_agent_transcript_redacts_every_role_and_tool_argument(string secret)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sw-sink-agent-" + Guid.NewGuid().ToString("N"));
        var store = new AgentTranscriptStore(dir);
        await store.SaveAsync("u@example.com",
        [
            new(AgentRole.System, secret),
            new(AgentRole.User, secret),
            new(AgentRole.Assistant, secret, ToolCalls:
                [new AgentToolCall("c1", "execute_command", JsonSerializer.Serialize(new { command = secret, description = secret }))]),
            new(AgentRole.Tool, secret, ToolCallId: "c1", ToolName: "execute_command"),
            new(AgentRole.User, secret),
            new(AgentRole.Assistant, secret, ToolCalls: [new AgentToolCall("c2", "x", JsonSerializer.Serialize(new { note = secret }))]),
            new(AgentRole.Tool, secret, ToolCallId: "c2", IsError: true, ToolName: "x"),
        ]);

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
            AssertClean(await File.ReadAllTextAsync(file));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task A_json_tool_result_keeps_what_follows_a_redacted_value()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sw-sink-agent-" + Guid.NewGuid().ToString("N"));
        var store = new AgentTranscriptStore(dir);
        var output = JsonSerializer.Serialize(new { note = $"token: {Canary}", containers = new[] { "web", "db" } });
        await store.SaveAsync("u@example.com",
        [
            new(AgentRole.User, "list"),
            new(AgentRole.Assistant, null, ToolCalls: [new AgentToolCall("c1", "list_containers", "{}")]),
            new(AgentRole.Tool, output, ToolCallId: "c1", ToolName: "list_containers"),
        ]);

        var tool = (await store.LoadAsync("u@example.com")).Single(m => m.Role == AgentRole.Tool).Text!;
        AssertClean(tool);
        using var document = JsonDocument.Parse(tool);
        Assert.Equal(2, document.RootElement.GetProperty("containers").GetArrayLength());
        Directory.Delete(dir, recursive: true);
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public async Task The_chat_history_redacts_every_message(string secret)
    {
        var data = Directory.CreateTempSubdirectory("sw-sink-chat-");
        var store = new ChatHistoryStore(new DataPathOptions(data.FullName));
        await store.SaveAsync("u@example.com",
        [
            new ChatMessage { Role = "system", Content = secret },
            new ChatMessage { Role = "user", Content = secret },
            new ChatMessage { Role = "assistant", Content = secret },
        ]);

        var files = Directory.EnumerateFiles(data.FullName, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
            AssertClean(await File.ReadAllTextAsync(file));
        data.Delete(recursive: true);
    }

    // ── Classification of the real MCP parameters ───────────────────────────────────────────────────────

    /// <summary>Every free-text parameter of a real MCP tool: its value is withheld (length only).</summary>
    private static readonly HashSet<string> PayloadParameters = new(StringComparer.Ordinal)
    {
        "command", "composeContent", "content", "envVars", "prompt", "query", "volumes",
    };

    /// <summary>Every other text parameter: short identifiers and descriptions, redacted pattern by pattern. A new
    /// parameter fails this test until it is put in one of the two lists — the classification is a decision.</summary>
    private static readonly HashSet<string> TextParameters = new(StringComparer.Ordinal)
    {
        "action", "appName", "certName", "container", "containerId", "containerName", "cronExpression", "database",
        "description", "driver", "eventType", "from", "image", "minSeverity", "name", "network", "networkId",
        "pattern", "period", "port", "ports", "priority", "projectName", "protocol", "reason", "restartPolicy",
        "server", "serverId", "serverType", "serviceName", "severity", "siteName", "table", "tag", "targetId",
        "taskId", "taskType", "type", "value", "volumeName", "zone",
    };

    [Fact]
    public void Every_text_parameter_of_a_real_mcp_tool_is_classified()
    {
        var names = typeof(McpToolParameters).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(m => m.GetParameters())
            .Where(p => !p.ParameterType.IsValueType)   // strings, lists and maps: what can carry text
            .Where(p => p.ParameterType != typeof(CancellationToken) && !typeof(IServiceProvider).IsAssignableFrom(p.ParameterType)
                        && p.ParameterType.Namespace?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) != true
                        && !p.ParameterType.IsInterface)   // injected services
            .Select(p => p.Name!)
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = names.Where(n => !PayloadParameters.Contains(n) && !TextParameters.Contains(n)).Order().ToList();
        Assert.True(unclassified.Count == 0, "Unclassified MCP parameters: " + string.Join(", ", unclassified));
        foreach (var name in PayloadParameters) Assert.True(SinkRedaction.IsPayload(name), $"{name} must be withheld");
        foreach (var name in TextParameters) Assert.False(SinkRedaction.IsPayload(name), $"{name} must stay readable");
    }

    private static MetricsDbContext Context()
    {
        var options = new DbContextOptionsBuilder<MetricsDbContext>().UseSqlite("Data Source=:memory:").Options;
        return new MetricsDbContext(options);
    }
}

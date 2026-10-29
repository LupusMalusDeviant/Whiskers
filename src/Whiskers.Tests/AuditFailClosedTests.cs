using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.HealthChecks;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-19 (INV-48): no mutation runs and no configuration is published without a durable audit intent; an outcome that
/// cannot be written leaves a recoverable incomplete event — kept, written again, settled from the operation journal
/// after a restart — and the <c>audit</c> health check is Degraded until it is complete.
/// </summary>
public sealed class AuditFailClosedTests : IDisposable
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private readonly ApprovalDatabase _db = new();
    private readonly AgentToolTestHelpers.FixedGuardrailStore _store;
    private readonly string _settings = Path.Combine(Path.GetTempPath(), $"sw-audit-settings-{Guid.NewGuid():N}.json");
    private readonly ShiftedClock _time = new();
    private int _runs;

    public AuditFailClosedTests()
    {
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        _store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_settings);
    }

    private MutationGateway Gateway(IMutationAuditSink? sink = null, IAuditRecovery? recovery = null,
        IOperationJournal? journal = null) =>
        new(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store), sink ?? new AuditLogMutationSink(_db.Scopes), [],
            NullLogger<MutationGateway>.Instance, decisions: new DbPolicyDecisionStore(_db.Scopes),
            journal: journal ?? new DbOperationJournal(_db.Scopes), recovery: recovery);

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static MutationRequest Restart(string operationId) =>
        new("container.restart", Admin, MutationSurface.Ui, new MutationTarget("container", "web", "web", "local"),
            Args(new { containerId = "web", serverId = "local" }), OperationId: operationId);

    private static MutationRequest SaveSettings(string operationId) =>
        new("config.save", Admin, MutationSurface.Ui, new MutationTarget("config", "Dns", "Dns", null),
            Args(new { section = "Dns" }), OperationId: operationId);

    private async Task Sql(string sql)
    {
        using var scope = _db.Scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<List<AuditLogEntity>> Events()
    {
        using var scope = _db.Scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().ToListAsync();
    }

    private AuditRecovery Recovery(IMutationAuditSink? sink = null, IOperationJournal? journal = null) =>
        new(sink ?? new AuditLogMutationSink(_db.Scopes), _db.Scopes, NullLogger<AuditRecovery>.Instance,
            journal ?? new DbOperationJournal(_db.Scopes), _time);

    // ---------------------------------------------------------------- no intent, no mutation

    [Fact]
    public async Task No_execution_without_a_durable_intent()
    {
        await Sql("DROP TABLE \"AuditLog\"");

        var outcome = await Gateway().ExecuteAsync(Restart("op-1"), (_, _) => Task.FromResult(Interlocked.Increment(ref _runs)));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, _runs);
        using var scope = _db.Scopes.CreateScope();
        // Nothing claimed either: the operation never started.
        Assert.False(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Operations.AnyAsync());
    }

    [Fact]
    public async Task No_configuration_is_published_without_a_durable_intent()
    {
        await Sql("DROP TABLE \"AuditLog\"");
        var store = new AppSettingsStore(_settings);

        var outcome = await Gateway().ExecuteAsync(SaveSettings("op-cfg"), async (_, _) =>
        {
            await store.SaveSectionAsync("Dns", new { Enabled = true });
            return 1;
        });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.False(File.Exists(_settings));
    }

    [Fact]
    public async Task A_configuration_is_published_once_its_intent_is_on_record()
    {
        var store = new AppSettingsStore(_settings);

        var outcome = await Gateway().ExecuteAsync(SaveSettings("op-cfg"), async (_, _) =>
        {
            await store.SaveSectionAsync("Dns", new { Enabled = true });
            return 1;
        });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.True(File.Exists(_settings));
        Assert.Equal("Executed", Assert.Single(await Events()).Outcome);
    }

    // ---------------------------------------------------------------- an outcome that cannot be written

    /// <summary>The real sink, whose outcome write fails while <see cref="Broken"/>.</summary>
    private sealed class FlakySink(IMutationAuditSink inner) : IMutationAuditSink
    {
        public bool Broken { get; set; }

        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct) => inner.BeginAsync(operationId, request, capability, approval, ct);

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct) =>
            Broken
                ? throw new InvalidOperationException("database is locked")
                : inner.CompleteAsync(auditId, operationId, status, verification, reason, duration, ct);

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct) =>
            RecordDeniedAsync(operationId, request, capability, status, reason, null, ct);

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, string? approvalId, CancellationToken ct) =>
            Broken
                ? throw new InvalidOperationException("database is locked")
                : inner.RecordDeniedAsync(operationId, request, capability, status, reason, approvalId, ct);
    }

    [Fact]
    public async Task A_failed_outcome_write_leaves_a_visible_incomplete_event_that_is_written_again()
    {
        var sink = new FlakySink(new AuditLogMutationSink(_db.Scopes)) { Broken = true };
        var recovery = Recovery(sink);
        var check = new AuditTrailCheck(recovery);

        var outcome = await Gateway(sink, recovery).ExecuteAsync(Restart("op-2"), (_, _) => Task.FromResult(1));

        // The mutation ran and the caller is told so; its event is still the intent, and the trail says it is incomplete.
        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal("intent", Assert.Single(await Events()).Phase);
        Assert.False(recovery.Health.Complete);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            (await check.CheckHealthAsync(new())).Status);

        // Still broken: kept.
        await recovery.RecoverAsync();
        Assert.Equal(1, recovery.Health.PendingOutcomes);

        sink.Broken = false;
        await recovery.RecoverAsync();
        var row = Assert.Single(await Events());
        Assert.Equal("outcome", row.Phase);
        Assert.Equal("Executed", row.Outcome);
        Assert.True(recovery.Health.Complete);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
            (await check.CheckHealthAsync(new())).Status);
    }

    [Fact]
    public async Task A_refusal_that_could_not_be_recorded_is_recorded_again()
    {
        var sink = new FlakySink(new AuditLogMutationSink(_db.Scopes)) { Broken = true };
        var recovery = Recovery(sink);

        var outcome = await Gateway(sink, recovery).ExecuteAsync(Restart("op-3") with { CapabilityId = "no.such" },
            (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(await Events());
        Assert.False(recovery.Health.Complete);

        sink.Broken = false;
        await recovery.RecoverAsync();
        Assert.Equal("Denied", Assert.Single(await Events()).Outcome);
        Assert.True(recovery.Health.Complete);
    }

    [Fact]
    public async Task After_a_restart_stale_intents_are_settled_from_the_operation_journal()
    {
        // Three operations of a process that stopped before their outcomes were written: one finished, one was cut
        // off while it ran, one never got its claim.
        var before = new DbOperationJournal(_db.Scopes);
        var sink = new FlakySink(new AuditLogMutationSink(_db.Scopes)) { Broken = true };
        var gateway = Gateway(sink, journal: before);
        await gateway.ExecuteAsync(Restart("op-done"), (_, _) => Task.FromResult(1));
        var running = new TaskCompletionSource<int>();
        _ = gateway.ExecuteAsync(Restart("op-cut"), (_, _) => running.Task);
        await Task.Delay(300);
        using (var scope = _db.Scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.AuditLog.Add(new AuditLogEntity
            {
                Action = "container.restart", Phase = "intent", OperationId = "op-never", Timestamp = _time.GetUtcNow().UtcDateTime,
                Details = "{}",
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(3, (await Events()).Count(e => e.Phase == "intent"));

        // The next process: a new journal instance, memory lost. Too early: a recent intent may still be running.
        var recovery = Recovery(journal: new DbOperationJournal(_db.Scopes));
        await recovery.RecoverAsync();
        Assert.Equal(3, (await Events()).Count(e => e.Phase == "intent"));

        _time.Advance(AuditRecovery.IntentGrace + TimeSpan.FromMinutes(1));
        await recovery.RecoverAsync();
        var events = (await Events()).ToDictionary(e => e.OperationId!);
        Assert.Equal("Executed", events["op-done"].Outcome);
        Assert.Equal("Unknown", events["op-cut"].Outcome);
        Assert.Equal("Denied", events["op-never"].Outcome);
        Assert.All(events.Values, e => Assert.Equal("outcome", e.Phase));
        Assert.True(recovery.Health.Complete);
        running.SetResult(1);
    }

    [Fact]
    public async Task An_intent_running_in_this_process_is_left_alone()
    {
        var journal = new DbOperationJournal(_db.Scopes);
        var recovery = Recovery(journal: journal);
        var running = new TaskCompletionSource<int>();
        var run = Gateway(recovery: recovery, journal: journal).ExecuteAsync(Restart("op-live"), (_, _) => running.Task);
        await Task.Delay(300);

        _time.Advance(AuditRecovery.IntentGrace + TimeSpan.FromMinutes(1));
        await recovery.RecoverAsync();
        Assert.Equal("intent", Assert.Single(await Events()).Phase);

        running.SetResult(1);
        await run;
        Assert.Equal("Executed", Assert.Single(await Events()).Outcome);
    }

    /// <summary>The system clock, moved forward by the test.</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        private TimeSpan _shift;
        public void Advance(TimeSpan by) => _shift += by;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _shift;
    }

    [Fact]
    public void A_lost_row_of_the_older_audit_shows_in_the_health_check()
    {
        var recovery = Recovery();
        Assert.True(recovery.Health.Complete);

        recovery.LegacyWriteFailed();
        Assert.True(recovery.Health.LegacyWriteFailed);

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.True(recovery.Health.Complete);
    }
}

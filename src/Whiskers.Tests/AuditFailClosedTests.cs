using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.HealthChecks;
using Whiskers.Models;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-19 (INV-48): no mutation runs and no configuration is published without a durable audit intent; an event that
/// cannot be written stays recoverable — an outcome is kept and written again, a refusal is kept in the outbox, intents
/// left by a restart are settled from the operation journal — and the <c>audit</c> health check is Degraded until the
/// trail is complete, also right after a restart.
/// </summary>
public sealed class AuditFailClosedTests : IDisposable
{
    private static readonly MutationPrincipal Admin =
        new(MutationPrincipalKind.Human, "user:admin@example.com", "admin@example.com", McpPermissionLevels.Admin);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sw-audit-{Guid.NewGuid():N}");
    private readonly AuditWrites _writes = new();
    private readonly OperationReads _reads = new();
    private readonly ServiceProvider _sp;
    private readonly IServiceScopeFactory _scopes;
    private readonly AuditOutbox _outbox;
    private readonly AgentToolTestHelpers.FixedGuardrailStore _store;
    private readonly ShiftedClock _time = new();
    private int _runs;

    public AuditFailClosedTests()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o
            .UseSqlite($"Data Source={Path.Combine(_root, "metrics.db")};Pooling=False")
            // The sink redaction as the app registers it (R1-24), then the fault injectors.
            .AddInterceptors(SinkRedactionInterceptor.Instance, _writes, _writes.Commits, _reads));
        _sp = services.BuildServiceProvider();
        _scopes = _sp.GetRequiredService<IServiceScopeFactory>();
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.Database.EnsureCreated();
            // Readers and a writer at the same time, as on a busy server: a test may write while a pass reads.
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        }
        _outbox = new AuditOutbox(new DataPathOptions(_root));

        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        policy.ForbiddenArgPatterns = [];
        policy.MaxAutonomousLevel = McpPermissionLevels.Admin;
        policy.RequireConfirmationForWrites = false;
        _store = new AgentToolTestHelpers.FixedGuardrailStore(policy);
    }

    public void Dispose()
    {
        _sp.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Makes the audit's own writes fail: before the database has them, or after it committed them (the
    /// answer is lost). Other tables are written normally.</summary>
    private sealed class AuditWrites : SaveChangesInterceptor
    {
        public bool FailBefore { get; set; }
        public bool FailAfter { get; set; }

        private static bool Audit(DbContext? db) =>
            db?.ChangeTracker.Entries<AuditLogEntity>().Any(e => e.State is EntityState.Added or EntityState.Modified) == true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            FailBefore && Audit(eventData.Context)
                ? throw new InvalidOperationException("database is locked")
                : base.SavingChangesAsync(eventData, result, cancellationToken);

        // Outside a transaction the save is the commit; inside one (the audit chain's, R1-23) the commit is.
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) =>
            FailAfter && eventData.Context?.Database.CurrentTransaction is null
                      && eventData.Context.ChangeTracker.Entries<AuditLogEntity>().Any()
                ? throw new InvalidOperationException("connection reset after commit")
                : base.SavedChangesAsync(eventData, result, cancellationToken);

        public DbTransactionInterceptor Commits => new CommitAnswers(this);

        private sealed class CommitAnswers(AuditWrites writes) : DbTransactionInterceptor
        {
            public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
                TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
                writes.FailAfter && eventData.Context?.ChangeTracker.Entries<AuditLogEntity>().Any() == true
                    ? throw new InvalidOperationException("connection reset after commit")
                    : base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    /// <summary>Runs <see cref="Before"/> once, right before the next read of the operation journal.</summary>
    private sealed class OperationReads : DbCommandInterceptor
    {
        public Func<Task>? Before { get; set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Operations\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _before, null) is { } before)
                await before();
            return result;
        }

        private Func<Task>? _before;
        public void Once(Func<Task> before) => _before = before;

        private Func<Task>? _after;
        /// <summary>Runs <paramref name="after"/> once, right after the next read of the operation journal.</summary>
        public void OnceAfter(Func<Task> after) => _after = after;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Operations\"", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _after, null) is { } after)
                await after();
            return result;
        }
    }

    /// <summary>The system clock, moved forward by the test.</summary>
    private sealed class ShiftedClock : TimeProvider
    {
        private TimeSpan _shift;
        public void Advance(TimeSpan by) => _shift += by;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + _shift;
    }

    // One sink, as in the app: the gateway's and the recovery's view of the intents in flight is the same.
    private AuditLogMutationSink? _sink;
    private AuditLogMutationSink Sink() => _sink ??= new(_scopes, outbox: _outbox);

    private MutationGateway Gateway(IAuditRecovery? recovery = null, IOperationJournal? journal = null) =>
        new(new CapabilityCatalog(), AgentToolTestHelpers.Policy(_store), Sink(), [],
            NullLogger<MutationGateway>.Instance, decisions: new DbPolicyDecisionStore(_scopes),
            journal: journal ?? new DbOperationJournal(_scopes), recovery: recovery);

    private AuditRecovery Recovery(IOperationJournal? journal = null) =>
        new(Sink(), _scopes, NullLogger<AuditRecovery>.Instance, journal ?? new DbOperationJournal(_scopes), _time, _outbox);

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
        using var scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<List<AuditLogEntity>> Events()
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().ToListAsync();
    }

    /// <summary>R1-23: every row the gateway, the outbox drain or recovery wrote is on the audit chain, in its
    /// latest version.</summary>
    private async Task AssertTrailIntact()
    {
        using var scope = _scopes.CreateScope();
        var report = await Whiskers.Services.AuditLog.AuditChain.Shared.VerifyAsync(
            scope.ServiceProvider.GetRequiredService<MetricsDbContext>(), CancellationToken.None);
        Assert.True(report.Intact, string.Join("; ", report.Problems));
        Assert.True(report.LinksChecked >= report.RowsChecked);
    }

    private static async Task<HealthStatus> Status(IAuditRecovery recovery) =>
        (await new AuditTrailCheck(recovery).CheckHealthAsync(new())).Status;

    // ---------------------------------------------------------------- no intent, no mutation

    [Fact]
    public async Task No_execution_without_a_durable_intent()
    {
        await Sql("DROP TABLE \"AuditLog\"");

        var outcome = await Gateway().ExecuteAsync(Restart("op-1"), (_, _) => Task.FromResult(Interlocked.Increment(ref _runs)));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, _runs);
        using var scope = _scopes.CreateScope();
        // Nothing claimed either: the operation never started.
        Assert.False(await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Operations.AnyAsync());
    }

    [Fact]
    public async Task No_execution_when_the_intent_insert_fails()
    {
        _writes.FailBefore = true;

        var outcome = await Gateway().ExecuteAsync(Restart("op-1"), (_, _) => Task.FromResult(Interlocked.Increment(ref _runs)));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, _runs);
    }

    [Fact]
    public async Task No_configuration_is_published_without_a_durable_intent()
    {
        await Sql("DROP TABLE \"AuditLog\"");
        var settings = Path.Combine(_root, "app-settings.json");
        var store = new AppSettingsStore(settings);

        var outcome = await Gateway().ExecuteAsync(SaveSettings("op-cfg"), async (_, _) =>
        {
            await store.SaveSectionAsync("Dns", new { Enabled = true });
            return 1;
        });

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.False(File.Exists(settings));
    }

    [Fact]
    public async Task A_configuration_is_published_once_its_intent_is_on_record()
    {
        var settings = Path.Combine(_root, "app-settings.json");
        var store = new AppSettingsStore(settings);

        var outcome = await Gateway().ExecuteAsync(SaveSettings("op-cfg"), async (_, _) =>
        {
            await store.SaveSectionAsync("Dns", new { Enabled = true });
            return 1;
        });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.True(File.Exists(settings));
        Assert.Equal("Executed", Assert.Single(await Events()).Outcome);
    }

    // ---------------------------------------------------------------- an event that cannot be written

    [Fact]
    public async Task A_failed_outcome_write_leaves_a_visible_incomplete_event_that_is_written_again()
    {
        var recovery = Recovery();
        await recovery.RecoverAsync();
        Assert.Equal(HealthStatus.Healthy, await Status(recovery));

        // The intent is written; then the database fails.
        var outcome = await Gateway(recovery).ExecuteAsync(Restart("op-2"), (_, _) =>
        {
            _writes.FailBefore = true;
            return Task.FromResult(1);
        });

        // The mutation ran and the caller is told so; its event is still the intent, and the trail is incomplete.
        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal("intent", Assert.Single(await Events()).Phase);
        Assert.Equal(HealthStatus.Degraded, await Status(recovery));

        // Still broken: kept.
        await recovery.RecoverAsync();
        Assert.Equal(1, recovery.Health.PendingEvents);

        _writes.FailBefore = false;
        await recovery.RecoverAsync();
        var row = Assert.Single(await Events());
        Assert.Equal("outcome", row.Phase);
        Assert.Equal("Executed", row.Outcome);
        Assert.Equal(HealthStatus.Healthy, await Status(recovery));
        await AssertTrailIntact();
    }

    [Fact]
    public async Task A_refusal_that_could_not_be_recorded_is_kept_in_the_outbox_and_survives_a_restart()
    {
        _writes.FailBefore = true;

        var outcome = await Gateway().ExecuteAsync(Restart("op-3") with { CapabilityId = "no.such" },
            (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Empty(await Events());
        Assert.Equal(1, _outbox.Count);

        // A new process: nothing in memory, the outbox is still there.
        var recovery = Recovery();
        Assert.Equal(HealthStatus.Degraded, await Status(recovery));
        await recovery.RecoverAsync();
        Assert.Equal(1, recovery.Health.PendingEvents);

        _writes.FailBefore = false;
        await recovery.RecoverAsync();
        var row = Assert.Single(await Events());
        Assert.Equal("Denied", row.Outcome);
        Assert.Equal("op-3", row.OperationId);
        Assert.Equal(0, _outbox.Count);
        Assert.Equal(HealthStatus.Healthy, await Status(recovery));
        await AssertTrailIntact();
    }

    [Fact]
    public async Task A_refusal_kept_in_the_outbox_holds_no_secret_below_a_secret_named_key()
    {
        _writes.FailBefore = true;
        var request = Restart("op-sec") with
        {
            CapabilityId = "no.such",
            Arguments = Args(new { containerId = "web", options = new { credentials = new[] { "hunter2" }, apiKey = new { value = "hunter3" } } }),
        };

        await Gateway().ExecuteAsync(request, (_, _) => Task.FromResult(1));

        var kept = Directory.EnumerateFiles(Path.Combine(_root, "audit-outbox"), "*.json").Single();
        var text = await File.ReadAllTextAsync(kept);
        Assert.DoesNotContain("hunter", text);
        Assert.Contains("containerId", text);
    }

    [Fact]
    public async Task A_refusal_kept_in_the_outbox_holds_no_secret_in_its_target_or_payload()
    {
        // R1-24: the outbox file is written as the row was built — the interceptor never sees it.
        _writes.FailBefore = true;
        var request = Restart("op-sec2") with
        {
            CapabilityId = "no.such",
            Target = new MutationTarget("repo", "https://deploy:hunter4@git.example.org/r.git",
                "git clone https://hunter5@git.example.org/r.git", "local"),
            Arguments = Args(new { containerId = "web", command = "curl -u deploy:hunter6 https://api.example.org" }),
        };

        await Gateway().ExecuteAsync(request, (_, _) => Task.FromResult(1));

        var kept = Directory.EnumerateFiles(Path.Combine(_root, "audit-outbox"), "*.json").Single();
        var text = await File.ReadAllTextAsync(kept);
        Assert.DoesNotContain("hunter", text);
        Assert.Contains("git.example.org", text);
    }

    [Fact]
    public async Task An_event_keeps_every_argument_through_the_save_redaction()
    {
        // R1-24: the arguments are a JSON string inside the details; redacting them again on save must not run a
        // pattern across their quotes and drop what follows a "token: …" or a bare "password=".
        var request = Restart("op-args") with
        {
            CapabilityId = "no.such",
            Arguments = Args(new { description = "rotate token: monthly", pattern = "password=", containerId = "web", serverId = "local" }),
        };

        await Gateway().ExecuteAsync(request, (_, _) => Task.FromResult(1));

        var details = Assert.Single(await Events()).Details!;
        Assert.Contains("rotate token=***", details);
        Assert.Contains("password=", details);
        Assert.Contains("containerId", details);
        Assert.Contains("serverId", details);
        await AssertTrailIntact();
    }

    [Fact]
    public async Task A_refusal_whose_commit_answer_was_lost_is_one_event()
    {
        _writes.FailAfter = true;

        await Gateway().ExecuteAsync(Restart("op-4") with { CapabilityId = "no.such" }, (_, _) => Task.FromResult(1));

        _writes.FailAfter = false;
        Assert.Single(await Events());
        Assert.Equal(0, _outbox.Count);
        await AssertTrailIntact();
    }

    [Fact]
    public async Task An_outbox_event_already_in_the_table_is_not_inserted_twice()
    {
        await Gateway().ExecuteAsync(Restart("op-5") with { CapabilityId = "no.such" }, (_, _) => Task.FromResult(1));
        // The same event also in the outbox (its insert committed, but the sink could not tell).
        Assert.True(_outbox.TryKeep(Assert.Single(await Events())));

        var recovery = Recovery();
        await recovery.RecoverAsync();

        Assert.Single(await Events());
        Assert.Equal(0, _outbox.Count);
        Assert.True(recovery.Health.Complete);
    }

    [Fact]
    public async Task After_a_restart_intents_are_settled_from_the_operation_journal_and_shown_until_then()
    {
        // Three operations of a process that stopped before their outcomes were written: one finished, one was cut
        // off while it ran, one never got its claim.
        var gateway = Gateway(journal: new DbOperationJournal(_scopes));
        await gateway.ExecuteAsync(Restart("op-done"), (_, _) =>
        {
            _writes.FailBefore = true;
            return Task.FromResult(1);
        });
        _writes.FailBefore = false;
        var running = new TaskCompletionSource<int>();
        var started = new TaskCompletionSource();
        _ = gateway.ExecuteAsync(Restart("op-cut"), (_, _) => { started.TrySetResult(); return running.Task; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));   // intent and journal claim are written by then
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            // Written as every intent is: through the chain (recovery never settles a row the chain does not know).
            await Whiskers.Services.AuditLog.AuditChain.Of(scope.ServiceProvider).WriteAsync(db, async token =>
            {
                var intent = new AuditLogEntity
                {
                    Action = "container.restart", Phase = "intent", OperationId = "op-never", EventId = "e-never",
                    Timestamp = _time.GetUtcNow().UtcDateTime, Details = "{}",
                };
                db.AuditLog.Add(intent);
                await db.SaveChangesAsync(token);
                return (0, (IReadOnlyCollection<long>)[intent.Id]);
            });
        }
        Assert.Equal(3, (await Events()).Count(e => e.Phase == "intent"));

        // The next process: a new sink and journal, memory lost. Not complete before it has looked.
        _sink = null;
        var recovery = Recovery(new DbOperationJournal(_scopes));
        Assert.Equal(HealthStatus.Degraded, await Status(recovery));

        // None of them is this process's: all are settled at once.
        await recovery.RecoverAsync();
        var events = (await Events()).ToDictionary(e => e.OperationId!);
        Assert.Equal("Executed", events["op-done"].Outcome);
        Assert.Equal("Unknown", events["op-cut"].Outcome);
        Assert.Equal("Denied", events["op-never"].Outcome);
        Assert.All(events.Values, e => Assert.Equal("outcome", e.Phase));
        Assert.Equal(HealthStatus.Healthy, await Status(recovery));
        running.SetResult(1);
    }

    [Fact]
    public async Task Recovery_never_settles_an_intent_changed_in_the_database()
    {
        // Someone rewrites an intent's attribution and waits for recovery to settle it into a new, valid version.
        var gateway = Gateway(journal: new DbOperationJournal(_scopes));
        await gateway.ExecuteAsync(Restart("op-forged"), (_, _) =>
        {
            _writes.FailBefore = true;
            return Task.FromResult(1);
        });
        _writes.FailBefore = false;
        using (var scope = _scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog
                .Where(a => a.OperationId == "op-forged").ExecuteUpdateAsync(s => s.SetProperty(a => a.Actor, "someone-else"));

        _sink = null;
        var recovery = Recovery(new DbOperationJournal(_scopes));
        await recovery.RecoverAsync();

        var row = Assert.Single(await Events());
        Assert.Equal("intent", row.Phase);   // left as it is: the change stays a finding
        Assert.Equal(HealthStatus.Degraded, await Status(recovery));
    }

    [Fact]
    public async Task Recovery_never_settles_from_a_row_put_back_after_it_read_it()
    {
        // Forged details while the pass reads the row, the genuine ones back before the settlement is checked: the
        // outcome computed from what was read must not be written.
        var gateway = Gateway(journal: new DbOperationJournal(_scopes));
        await gateway.ExecuteAsync(Restart("op-swap"), (_, _) =>
        {
            _writes.FailBefore = true;
            return Task.FromResult(1);
        });
        _writes.FailBefore = false;
        string genuine;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            genuine = (await db.AuditLog.AsNoTracking().SingleAsync()).Details!;
            await db.AuditLog.ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, "{\"forged\":true}"));
        }
        _reads.OnceAfter(async () =>
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, genuine));
        });

        _sink = null;
        var recovery = Recovery(new DbOperationJournal(_scopes));
        await recovery.RecoverAsync();

        var row = Assert.Single(await Events());
        Assert.Equal("intent", row.Phase);
        Assert.DoesNotContain("forged", row.Details);
    }

    [Fact]
    public async Task An_intent_running_in_this_process_is_left_alone()
    {
        var journal = new DbOperationJournal(_scopes);
        var recovery = Recovery(journal);
        var running = new TaskCompletionSource<int>();
        var started = new TaskCompletionSource();
        var run = Gateway(recovery, journal).ExecuteAsync(Restart("op-live"), (_, _) => { started.TrySetResult(); return running.Task; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));   // intent and journal claim are written by then

        _time.Advance(AuditRecovery.IntentGrace + TimeSpan.FromMinutes(1));
        await recovery.RecoverAsync();
        Assert.Equal("intent", Assert.Single(await Events()).Phase);
        Assert.True(recovery.Health.Complete);

        running.SetResult(1);
        await run;
        Assert.Equal("Executed", Assert.Single(await Events()).Outcome);
    }

    [Fact]
    public async Task A_refused_attempt_whose_intent_could_not_be_written_is_still_one_event()
    {
        _writes.FailBefore = true;
        var recovery = Recovery();

        var outcome = await Gateway(recovery).ExecuteAsync(Restart("op-6"), (_, _) => Task.FromResult(Interlocked.Increment(ref _runs)));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(0, _runs);
        Assert.Empty(await Events());
        Assert.Equal(HealthStatus.Degraded, await Status(recovery));

        _writes.FailBefore = false;
        await recovery.RecoverAsync();
        var row = Assert.Single(await Events());
        Assert.Equal("op-6", row.OperationId);
        Assert.Equal("Denied", row.Outcome);
        Assert.Equal("outcome", row.Phase);
        Assert.Equal(HealthStatus.Healthy, await Status(recovery));
    }

    [Fact]
    public async Task An_intent_whose_commit_answer_was_lost_runs_once_and_is_one_event()
    {
        // The intent lands, its answer is lost: the sink finds it by its event id, the mutation runs once.
        _writes.FailAfter = true;
        var outcome = await Gateway().ExecuteAsync(Restart("op-7"), (_, _) =>
        {
            _writes.FailAfter = false;
            return Task.FromResult(Interlocked.Increment(ref _runs));
        });

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(1, _runs);
        Assert.Equal("Executed", Assert.Single(await Events()).Outcome);
        Assert.Equal(0, _outbox.Count);
        await AssertTrailIntact();
    }

    [Fact]
    public async Task A_claimed_operation_of_this_process_that_is_not_running_any_more_is_not_complete()
    {
        // The journal kept it Running (its finish failed) and the outcome write failed beyond what memory keeps: the
        // trail cannot say how it ended.
        var journal = new DbOperationJournal(_scopes);
        await Gateway(journal: journal).ExecuteAsync(Restart("op-8"), async (_, _) =>
        {
            using (var scope = _scopes.CreateScope())
                await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database
                    .ExecuteSqlRawAsync("CREATE TRIGGER keep_running BEFORE UPDATE ON Operations BEGIN SELECT RAISE(ABORT, 'locked'); END;");
            _writes.FailBefore = true;
            return 1;
        });
        _writes.FailBefore = false;

        var recovery = Recovery(journal);
        await recovery.RecoverAsync();

        Assert.Equal("intent", Assert.Single(await Events()).Phase);
        Assert.False(recovery.Health.Complete);
        Assert.Equal(HealthStatus.Degraded, await Status(recovery));
    }

    private sealed class BrokenRecovery : IAuditRecovery
    {
        public void OutcomeNotWritten(PendingAuditOutcome outcome) { }
        public void LegacyWriteFailed() { }
        public AuditHealth Health => throw new IOException("audit-outbox: access denied");
        public Task RecoverAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task An_audit_state_that_cannot_be_read_is_degraded_not_unhealthy()
    {
        Assert.Equal(HealthStatus.Degraded, await Status(new BrokenRecovery()));
    }

    [Fact]
    public async Task Recovery_never_overwrites_an_outcome_written_while_it_was_looking()
    {
        // An intent this process is writing; the pass reads it, then — before it reads the journal — the gateway
        // completes it. The pass's picture ("no claim, not in flight") is stale and must not win.
        var sink = Sink();
        var request = Restart("op-race");
        var auditId = await sink.BeginAsync("op-race", request, new CapabilityCatalog().Find("container.restart")!, null, default);
        _reads.Once(() => sink.CompleteAsync(auditId, "op-race", MutationStatus.Executed, VerificationStatus.NotVerifiable,
            null, TimeSpan.FromMilliseconds(5), default));

        await Recovery().RecoverAsync();

        var row = Assert.Single(await Events());
        Assert.Equal("Executed", row.Outcome);
        Assert.DoesNotContain("Recovered", row.Details);
    }

    [Fact]
    public async Task Recovery_never_settles_on_a_journal_picture_that_changed_while_it_was_looking()
    {
        // The pass reads the journal (no claim yet). Then the operation is claimed, runs, its journal entry is
        // finished and its outcome write fails. The pass's "never claimed" is stale: it must not record "did not run".
        var sink = Sink();
        var journal = new DbOperationJournal(_scopes);
        var request = Restart("op-stale");
        var auditId = await sink.BeginAsync("op-stale", request, new CapabilityCatalog().Find("container.restart")!, null, default);
        _reads.OnceAfter(async () =>
        {
            var claim = await journal.ClaimAsync(new OperationClaimRequest("op-stale", null, "container.restart", Admin.Id,
                DbOperationJournal.InputHashOf(request), null, null, auditId));
            await journal.FinishAsync("op-stale", claim.Token!, MutationStatus.Executed, VerificationStatus.NotVerifiable);
            _writes.FailBefore = true;
            await Assert.ThrowsAnyAsync<Exception>(() => sink.CompleteAsync(auditId, "op-stale", MutationStatus.Executed,
                VerificationStatus.NotVerifiable, null, TimeSpan.Zero, default));
            _writes.FailBefore = false;
        });

        var looking = Recovery(journal);
        await looking.RecoverAsync();
        Assert.Equal("intent", Assert.Single(await Events()).Phase);
        // Skipped, so still incomplete — and shown as such.
        Assert.Equal(HealthStatus.Degraded, await Status(looking));

        // A restart before anything wrote the outcome again: the next process settles it by the journal.
        _sink = null;
        await Recovery(new DbOperationJournal(_scopes)).RecoverAsync();
        Assert.Equal("Executed", Assert.Single(await Events()).Outcome);
    }

    [Fact]
    public async Task An_outbox_that_cannot_be_listed_is_not_empty()
    {
        var outbox = new AuditOutbox(new DataPathOptions(Path.Combine(_root, "bad dir")));
        Assert.Equal(1, outbox.Count);
        var recovery = new AuditRecovery(Sink(), _scopes, NullLogger<AuditRecovery>.Instance,
            new DbOperationJournal(_scopes), _time, outbox);
        await Assert.ThrowsAnyAsync<Exception>(() => recovery.RecoverAsync());
        Assert.Equal(HealthStatus.Degraded, await Status(recovery));
    }

    [Fact]
    public async Task A_lost_row_of_the_older_audit_shows_in_the_health_check()
    {
        var recovery = Recovery();
        await recovery.RecoverAsync();
        Assert.True(recovery.Health.Complete);

        recovery.LegacyWriteFailed();
        Assert.Equal(HealthStatus.Degraded, await Status(recovery));

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.True(recovery.Health.Complete);
    }
}

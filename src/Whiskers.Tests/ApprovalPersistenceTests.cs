using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>A Whiskers database on a temporary SQLite file, shared by the approval tests that restart the store.</summary>
public sealed class ApprovalDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sw-approvals-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;

    public ApprovalDatabase()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_path}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    /// <summary>A store as a freshly started Whiskers has it: new memory, the same database.</summary>
    public async Task<ApprovalStore> StartAsync(TimeProvider? time = null)
    {
        var store = new ApprovalStore(persistence: Persistence(), time: time);
        await store.LoadAsync();
        return store;
    }

    public IApprovalPersistence Persistence() => new DbApprovalPersistence(_sp.GetRequiredService<IServiceScopeFactory>());

    public async Task<ApprovalRecordEntity?> RowAsync(string id)
    {
        using var scope = _sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Approvals.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}

/// <summary>R1-14 (INV-30/35): approvals, their decisions and their single use are in the database; a restart loses
/// none of them, and the history is never cut short.</summary>
public sealed class ApprovalPersistenceTests : IDisposable
{
    private readonly ApprovalDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static Dictionary<string, JsonElement> Args(string container) =>
        new() { ["containerId"] = JsonSerializer.SerializeToElement(container) };

    private static ApprovalSubject Retry(string container = "web-1") =>
        new("restart_container", McpPermissionLevels.Write, "key:k1", "ci", "mcp-direct", Args(container))
        {
            Binding = "binding-" + container,
            Requesters = ["key:k1"],
        };

    private static ApprovalRequest Request(string toolCallId, bool interactive = false) => new(
        "s1", toolCallId, "ops@example.com", "agent-web", "restart_container", McpPermissionLevels.Write, null,
        "needs a human", Binding: "binding-" + toolCallId, Interactive: interactive,
        Requesters: ["user:ops@example.com"]);

    [Fact]
    public async Task An_approved_retry_survives_a_restart_and_then_runs_once()
    {
        var before = await _db.StartAsync();
        var raised = await new ApprovalGate(before).CheckAsync(Retry(), "needs a human", []);
        Assert.True(await before.ResolveAsync(raised.ApprovalId!, true, "admin@example.com", McpPermissionLevels.Admin));

        var after = await _db.StartAsync();                        // restart: new memory, same database
        var gate = new ApprovalGate(after);
        var retry = await gate.CheckAsync(Retry(), "needs a human", []);
        var used = await gate.TryConsumeAsync(retry.ApprovalId!, "op-1");
        var again = await gate.CheckAsync(Retry(), "needs a human", []);

        Assert.True(retry.Approved);
        Assert.Equal(raised.ApprovalId, retry.ApprovalId);
        Assert.Equal("admin@example.com", retry.ApprovedBy);
        Assert.True(used);
        Assert.False(again.Approved);
        Assert.NotEqual(raised.ApprovalId, again.ApprovalId);
        var row = await _db.RowAsync(raised.ApprovalId!);
        Assert.Equal("op-1", row!.ConsumedBy);
    }

    [Fact]
    public async Task A_used_approval_stays_used_after_a_restart()
    {
        var before = await _db.StartAsync();
        var gate = new ApprovalGate(before);
        var raised = await gate.CheckAsync(Retry(), "needs a human", []);
        await before.ResolveAsync(raised.ApprovalId!, true, "admin@example.com", McpPermissionLevels.Admin);
        Assert.True(await gate.TryConsumeAsync(raised.ApprovalId!, "op-1"));

        var after = await _db.StartAsync();
        var retry = await new ApprovalGate(after).CheckAsync(Retry(), "needs a human", []);

        Assert.False(retry.Approved);
        Assert.NotEqual(raised.ApprovalId, retry.ApprovalId);
        Assert.False(await after.TryConsumeAsync(raised.ApprovalId!, "op-2", DateTime.UtcNow));
    }

    [Fact]
    public async Task A_restart_cancels_a_pending_agent_approval_and_keeps_a_pending_retry_approval()
    {
        var before = await _db.StartAsync();
        var agent = await before.CreateAsync(Request("agent-call", interactive: true), _ => Task.CompletedTask);
        var retry = await before.CreateAsync(Request("retry-call"), _ => Task.CompletedTask);

        var after = await _db.StartAsync();

        Assert.Equal(ApprovalStatus.Cancelled, after.Get(agent.Id)!.Status);
        Assert.Equal(nameof(ApprovalStatus.Cancelled), (await _db.RowAsync(agent.Id))!.Status);
        Assert.Equal(ApprovalStatus.Pending, after.Get(retry.Id)!.Status);
        // The pending retry approval can still be decided after the restart.
        Assert.True(await after.ResolveAsync(retry.Id, true, "admin@example.com", McpPermissionLevels.Admin));
        Assert.Equal(nameof(ApprovalStatus.Approved), (await _db.RowAsync(retry.Id))!.Status);
    }

    [Fact]
    public async Task The_history_is_complete_however_many_approvals_were_decided()
    {
        const int count = 260;                                     // more than the store keeps in memory
        var store = await _db.StartAsync();
        var grant = await store.CreateAsync(Request("the-grant"), _ => Task.CompletedTask);
        await store.ResolveAsync(grant.Id, true, "admin@example.com", McpPermissionLevels.Admin);
        for (var i = 0; i < count; i++)
        {
            var a = await store.CreateAsync(Request($"call-{i}"), _ => Task.CompletedTask);
            await store.ResolveAsync(a.Id, false, "admin@example.com", McpPermissionLevels.Admin);
        }

        var history = await store.GetHistoryAsync(0, 10_000);
        var afterRestart = await (await _db.StartAsync()).GetHistoryAsync(0, 10_000);

        Assert.Equal(count + 1, history.Count);
        Assert.Equal(count + 1, afterRestart.Count);
        Assert.Equal(count, afterRestart.Count(a => a.Status == ApprovalStatus.Rejected));
        // Trimming the page's memory never drops an approval that can still be used.
        Assert.NotNull(store.Get(grant.Id));
        Assert.True(await store.TryConsumeAsync(grant.Id, "op-1", DateTime.UtcNow));
    }

    [Fact]
    public async Task An_approval_that_cannot_be_recorded_is_never_raised()
    {
        var store = new ApprovalStore(persistence: new FailingPersistence());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync(Request("call-1"), _ => Task.CompletedTask));
        Assert.Empty(store.GetAll());
        // The gate turns that into a refusal, not into a call that runs.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ApprovalGate(store).CheckAsync(Retry(), "needs a human", []));
    }

    [Fact]
    public async Task An_approval_whose_decision_cannot_be_recorded_stays_pending()
    {
        var persistence = new FailingPersistence { FailInsert = false };
        var store = new ApprovalStore(persistence: persistence);
        var resolved = false;
        var approval = await store.CreateAsync(Request("call-1"), _ => { resolved = true; return Task.CompletedTask; });

        Assert.False(await store.ResolveAsync(approval.Id, true, "admin@example.com", McpPermissionLevels.Admin));
        Assert.Equal(ApprovalStatus.Pending, store.Get(approval.Id)!.Status);
        Assert.False(resolved);
    }

    private sealed class FailingPersistence : IApprovalPersistence
    {
        private readonly InMemoryApprovalPersistence _inner = new();
        public bool FailInsert { get; init; } = true;

        public Task InsertAsync(Approval approval, CancellationToken ct = default) =>
            FailInsert ? throw new InvalidOperationException("database unavailable") : _inner.InsertAsync(approval, ct);

        public Task<bool> ResolveAsync(string approvalId, ApprovalStatus status, DateTime at, string? by,
            DateTime? grantExpiresAt, bool selfApproved = false, CancellationToken ct = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<bool> ExpireLateDecisionAsync(string approvalId, ApprovalStatus decided, DateTime at,
            CancellationToken ct = default) => throw new InvalidOperationException("database unavailable");

        public Task<bool> ConsumeAsync(string approvalId, string operationId, DateTime now, CancellationToken ct = default) =>
            throw new InvalidOperationException("database unavailable");

        public Task<IReadOnlyList<Approval>> LoadOpenAsync(CancellationToken ct = default) => _inner.LoadOpenAsync(ct);

        public Task<IReadOnlyList<Approval>> LoadHistoryAsync(int skip, int take, CancellationToken ct = default) =>
            _inner.LoadHistoryAsync(skip, take, ct);
    }
}

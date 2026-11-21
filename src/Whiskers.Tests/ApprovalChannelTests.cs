using System.Reflection;
using System.Text.Json;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using static Whiskers.Tests.ComponentTests.ServersKubeconfigSaveTests;

namespace Whiskers.Tests;

/// <summary>Contract 0019 (D6, INV-33): where a human decided an approval — the Approvals page, the dialog of the action,
/// the agent chat — is recorded on the approval row (<c>DecidedVia</c>) and in the execution's audit details, next to
/// the self-approval mark. Rows from before contract 0019 and system expiries carry no channel.</summary>
public sealed class ApprovalChannelTests
{
    private const string Admin = InlineApprovalRunnerTests.AdminEmail;

    private static ApprovalRequest WebRequest(string requester = Admin) =>
        new("mcp-direct", Guid.NewGuid().ToString("N"), requester, "web", "server.onboard", McpPermissionLevels.Admin, "{}",
            "control plane", CorrelationId: "op-1", Requesters: [PrincipalIds.User(requester)]);

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public async Task DecidedVia_is_persisted_on_both_providers(string provider)
    {
        // Both providers' migrations add the nullable column, additively (paired migrations, ADR-0004).
        using (var model = Migrations(provider))
        {
            var migrations = model.GetService<IMigrationsAssembly>();
            var id = Assert.Single(migrations.Migrations.Keys, k => k.EndsWith("_ApprovalDecidedVia", StringComparison.Ordinal));
            var migration = migrations.CreateMigration(migrations.Migrations[id], model.Database.ProviderName!);
            var add = Assert.Single(migration.UpOperations.OfType<AddColumnOperation>());
            Assert.Equal(("Approvals", "DecidedVia", true), (add.Table, add.Name, add.IsNullable));
            Assert.Empty(migration.UpOperations.Where(o => o is not AddColumnOperation));
        }
        if (provider != "sqlite") return;   // the round trip on a real PostgreSQL is a container test

        // SQLite: a database migrated by the shipped migrations keeps the channel of a decision.
        var path = Path.Combine(Path.GetTempPath(), $"sw-channel-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={path}",
                s => s.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly)));
            await using var sp = services.BuildServiceProvider();
            using (var scope = sp.CreateScope())
                await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.MigrateAsync();
            var persistence = new DbApprovalPersistence(sp.GetRequiredService<IServiceScopeFactory>());
            var store = new ApprovalStore(persistence: persistence);
            var approval = await store.CreateAsync(WebRequest(), _ => Task.CompletedTask);

            Assert.Equal(ApprovalDecisionResult.Decided,
                await store.DecideAsync(approval.Id, true, ApprovalResolver.User(Admin, AppRole.Admin), ApprovalChannel.Dialog));

            using var read = sp.CreateScope();
            var row = await read.ServiceProvider.GetRequiredService<MetricsDbContext>().Approvals.AsNoTracking()
                .SingleAsync(a => a.Id == approval.Id);
            Assert.Equal("dialog", row.DecidedVia);
            var reloaded = new ApprovalStore(persistence: persistence);
            await reloaded.LoadAsync();
            Assert.Equal("dialog", reloaded.Get(approval.Id)!.DecidedVia);
        }
        finally
        {
            TestSqlite.ClearPools(Path.GetDirectoryName(path)!);
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static MetricsDbContext Migrations(string provider) => new(provider == "sqlite"
        ? new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite("Data Source=:memory:", o => o.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly)).Options
        : new DbContextOptionsBuilder<MetricsDbContext>()
            .UseNpgsql("Host=localhost;Database=channel-check;Username=none",
                o => o.MigrationsAssembly("Whiskers.Migrations.Postgres")).Options);

    [Fact]
    public async Task Legacy_rows_read_as_unknown_channel()
    {
        using var db = new ApprovalDatabase();
        var persistence = db.Persistence();
        var legacy = new Approval
        {
            SessionId = "mcp-direct", ToolCallId = "c1", Actor = "ops", ActorType = "web", ToolName = "server.onboard",
            Level = McpPermissionLevels.Admin, Reason = "control plane", Requesters = [PrincipalIds.User(Admin)],
            Status = ApprovalStatus.Approved, ResolvedBy = Admin, ResolvedAt = DateTime.UtcNow,
            GrantExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };
        await persistence.InsertAsync(legacy);   // a row as written before contract 0019: no channel

        var store = await db.StartAsync();

        Assert.Null(store.Get(legacy.Id)!.DecidedVia);
        Assert.Null((await db.RowAsync(legacy.Id))!.DecidedVia);
        // A system expiry records no channel either.
        var time = new ManualClock();
        var expiring = new ApprovalStore(time: time);
        var pending = await expiring.CreateAsync(WebRequest(), _ => Task.CompletedTask);
        time.Now += TimeSpan.FromMinutes(16);
        Assert.Empty(expiring.GetPending());
        Assert.Equal(ApprovalStatus.Expired, expiring.Get(pending.Id)!.Status);
        Assert.Null(expiring.Get(pending.Id)!.DecidedVia);
    }

    [Fact]
    public async Task The_page_records_page_and_the_chat_records_chat()
    {
        // The store's original overload is the Approvals page's.
        var store = new ApprovalStore();
        var byOverload = await store.CreateAsync(WebRequest("other@example.com"), _ => Task.CompletedTask);
        Assert.Equal(ApprovalDecisionResult.Decided,
            await store.DecideAsync(byOverload.Id, true, ApprovalResolver.User(Admin, AppRole.Admin)));
        Assert.Equal("page", store.Get(byOverload.Id)!.DecidedVia);

        // The Approvals page itself, through its real handler.
        await using (var ctx = new BunitContext())
        {
            ctx.Services.AddLogging();
            ctx.Services.AddLocalization();
            ctx.Services.AddMudServices();
            ctx.Services.AddSingleton<IKeyInterceptorService>(NullProxy<IKeyInterceptorService>.Create());
            ctx.Services.AddSingleton<ICurrentUserService>(new InlineApprovalRunnerTests.Person());
            ctx.Services.AddSingleton<IApprovalStore>(store);
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            var onPage = await store.CreateAsync(WebRequest("other@example.com"), _ => Task.CompletedTask);
            var page = ctx.Render<ApprovalsView>();
            await page.InvokeAsync(() => (Task)typeof(ApprovalsView)
                .GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page.Instance, [onPage.Id, true])!);
            Assert.Equal("page", store.Get(onPage.Id)!.DecidedVia);
        }

        // The two chats decide their in-band card via "chat" (the same store call, only the channel differs).
        foreach (var chat in new[] { "Components/Pages/AgentView.razor", "Components/Shared/AiChat.razor" })
        {
            var source = DeploymentScripts.ReadRepoFile(["src", "Whiskers", .. chat.Split('/')]);
            Assert.Contains("ApprovalStore.DecideAsync(", source);
            Assert.All(System.Text.RegularExpressions.Regex.Matches(source, @"ApprovalStore\.DecideAsync\([^;]*;"),
                m => Assert.Contains("ApprovalChannel.Chat", m.Value));
        }
    }

    [Fact]
    public async Task A_self_decision_in_the_dialog_is_recorded_as_such()
    {
        using var db = new ApprovalDatabase();
        var store = new ApprovalStore(persistence: db.Persistence());
        var approval = await store.CreateAsync(WebRequest(), _ => Task.CompletedTask);

        Assert.Equal(ApprovalDecisionResult.Decided,
            await store.DecideAsync(approval.Id, true, ApprovalResolver.User(Admin, AppRole.Admin), ApprovalChannel.Dialog));

        var row = (await db.RowAsync(approval.Id))!;
        Assert.True(row.SelfApproved);
        Assert.Equal("dialog", row.DecidedVia);
        Assert.Equal(Admin, row.ResolvedBy);
        Assert.True(store.Get(approval.Id)!.SelfApproved);
    }

    [Fact]
    public async Task The_execution_audit_names_channel_and_self_approval()
    {
        using var db = new ApprovalDatabase();
        var policy = Whiskers.Configuration.GuardrailPolicy.SafeDefault();
        var guardrails = new AgentToolTestHelpers.FixedGuardrailStore(policy);
        var approvals = new ApprovalStore(persistence: db.Persistence(), policy: guardrails);
        var server = InlineApprovalRunnerTests.Edge();
        var gateway = AuditTrail.Gateway(db.Scopes, guardrails, approvals,
            TestTargets.Resolver(new MutationTargetResolver(new TestServices(new FakeServerConfig(server)))));
        var prompt = new InlineApprovalRunnerTests.ScriptedPrompt().Then(async (r, _) =>
        {
            await approvals.DecideAsync(r.ApprovalId, true, ApprovalResolver.User(Admin, AppRole.Admin), ApprovalChannel.Dialog);
            return new InlineApprovalResult(InlineApprovalOutcome.ApprovedHere);
        });
        var runner = new UiMutationRunner(gateway, new CapabilityCatalog(), new InlineApprovalRunnerTests.Person(),
            prompt: prompt, approvals: approvals);

        await runner.RunAsync("server.onboard", InlineApprovalRunnerTests.OnboardArgs,
            (ResolvedTarget _, CancellationToken _) => Task.FromResult(1));

        var approvalId = prompt.Asked.Single().ApprovalId;
        var rows = await AuditTrail.RowsAsync(db.Scopes);
        var executed = Assert.Single(rows, r => r.Outcome == nameof(MutationStatus.Executed));
        using var details = JsonDocument.Parse(executed.Details!);
        var recorded = FindApproval(details.RootElement)!.Value;
        Assert.Equal(approvalId, recorded.GetProperty("id").GetString());
        Assert.Equal("dialog", recorded.GetProperty("via").GetString());
        Assert.True(recorded.GetProperty("selfApproved").GetBoolean());
        // The refusal that raised it names the approval, without a channel (nothing was decided yet).
        var pending = Assert.Single(rows, r => r.Outcome == nameof(MutationStatus.PendingApproval));
        Assert.Equal(executed.OperationId, pending.OperationId);
        Assert.DoesNotContain("\"via\"", pending.Details);
    }

    /// <summary>The <c>approval</c> object anywhere in the details (the intent is nested in the outcome row).</summary>
    private static JsonElement? FindApproval(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("approval", out var approval) && approval.ValueKind == JsonValueKind.Object
                && approval.TryGetProperty("via", out _))
                return approval;
            foreach (var property in element.EnumerateObject())
                if (FindApproval(property.Value) is { } found) return found;
        }
        return null;
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new NoTimer();

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

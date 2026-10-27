using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-03 / INV-01, INV-06: principals keep a stable identity — user, key, trigger, agent session and the delegation
/// between them — from the entry point to the gateway and on into automation, built from typed ids and never from
/// display names. Owners recorded before the scheme are migrated explicitly; ambiguous ones stay suspended.
/// </summary>
public sealed class PrincipalPropagationTests : IDisposable
{
    private static AgentContext Context(AgentPrincipal principal, string sessionId, AgentOrigin origin) =>
        new(sessionId, principal, origin, GuardrailPolicy.SafeDefault());

    private static AgentPrincipal User(string email, string display) =>
        new(AgentPrincipalKind.WebUser, display, McpPermissionLevels.Write, null, UserEmail: email);

    private static AgentPrincipal Key(string keyId, string name) =>
        new(AgentPrincipalKind.McpKey, name, McpPermissionLevels.Write, ["restart_container"], McpKeyId: keyId);

    private static AgentPrincipal Trigger(string triggerId, string name) =>
        new(AgentPrincipalKind.Trigger, $"AI trigger '{name}'", McpPermissionLevels.Write, null, TriggerId: triggerId);

    [Fact]
    public async Task Human_to_agent_to_automation_keeps_user_and_session_identity()
    {
        var human = User("Ops@Example.com", "Olivia (Ops)");

        var agent = AgentToolInvoker.PrincipalOf(Context(human, "sess-1", AgentOrigin.WebUi));

        Assert.Equal(MutationPrincipalKind.Agent, agent.Kind);
        Assert.Equal("agent:sess-1", agent.Id);
        Assert.Equal("sess-1", agent.SessionId);
        Assert.Equal(MutationPrincipalKind.Human, agent.OnBehalfOf!.Kind);
        Assert.Equal("user:ops@example.com", agent.OnBehalfOf.Id);
        Assert.Equal("sess-1", agent.OnBehalfOf.SessionId);

        // A task the agent creates is owned by the user, and its runs act on behalf of that user.
        var owner = AutomationOwners.OfCaller(agent);
        Assert.Equal("user:ops@example.com", owner);

        var f = AutomationMutationRunnerTests.Build();
        f.Whitelist.Allowed.Add("ops@example.com");
        f.Roles.Map["ops@example.com"] = AppRole.Operator;
        var outcome = await f.Runner.RunAsync(new AutomationActor("task:t1", "Scheduled task 'nightly'", owner),
            "container.restart", new { containerId = "web-1" }, _ => Task.CompletedTask);

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        var run = Assert.Single(f.Audit.Principals);
        Assert.Equal(MutationPrincipalKind.Automation, run.Kind);
        Assert.Equal("task:t1", run.Id);
        Assert.Equal("user:ops@example.com", run.OnBehalfOf!.Id);
    }

    [Fact]
    public void A_display_name_never_changes_an_identity()
    {
        var before = AgentToolInvoker.PrincipalOf(Context(User("ops@example.com", "Olivia"), "s", AgentOrigin.WebUi));
        var after = AgentToolInvoker.PrincipalOf(Context(User("ops@example.com", "key:admin"), "s", AgentOrigin.WebUi));

        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.OnBehalfOf!.Id, after.OnBehalfOf!.Id);
        Assert.Equal(MutationPrincipalKind.Human, after.OnBehalfOf.Kind);   // "key:admin" as a name makes no key
    }

    [Fact]
    public void ApiKey_to_agent_keeps_key_and_credential_identity_across_a_rename()
    {
        var before = Key("k1", "CI");
        var renamed = Key("k1", "CI (renamed)");

        var direct = McpGuardrailFilterPrincipal(renamed);
        var agent = AgentToolInvoker.PrincipalOf(Context(renamed, "sess-2", AgentOrigin.McpTool));

        Assert.Equal("key:k1", MutationPrincipals.Of(before).Id);
        Assert.Equal("key:k1", direct.Id);
        Assert.Equal("k1", direct.CredentialId);
        Assert.Equal(MutationPrincipalKind.ApiKey, agent.OnBehalfOf!.Kind);
        Assert.Equal("key:k1", agent.OnBehalfOf.Id);
        Assert.Equal("k1", agent.CredentialId);
        Assert.Equal("agent:sess-2", agent.Id);
        Assert.Equal(["restart_container"], agent.AllowedTools);
        Assert.Equal("key:k1", AutomationOwners.OfCaller(agent));
        Assert.Equal("key:k1", DirectMcpCallGuard.ActorOf(renamed));

        // The engine sees the same key again.
        var back = MutationPrincipals.ToAgent(agent.OnBehalfOf);
        Assert.Equal(AgentPrincipalKind.McpKey, back.Kind);
        Assert.Equal("k1", back.McpKeyId);
    }

    [Fact]
    public void Two_triggers_with_the_same_name_are_distinct_principals()
    {
        var first = Trigger("t-1", "restart on OOM");
        var second = Trigger("t-2", "restart on OOM");

        var a = AgentToolInvoker.PrincipalOf(Context(first, "sess-a", AgentOrigin.Trigger));
        var b = AgentToolInvoker.PrincipalOf(Context(second, "sess-b", AgentOrigin.Trigger));

        Assert.Equal("trigger:t-1", a.OnBehalfOf!.Id);
        Assert.Equal("trigger:t-2", b.OnBehalfOf!.Id);
        Assert.Equal(MutationPrincipalKind.Automation, a.OnBehalfOf.Kind);
        Assert.Null(a.CredentialId);   // no shared pseudo key any more
        Assert.NotEqual(DirectMcpCallGuard.ActorOf(first), DirectMcpCallGuard.ActorOf(second));

        // Renaming a trigger keeps its identity; the engine sees it as that trigger again.
        Assert.Equal("trigger:t-1", MutationPrincipals.Of(Trigger("t-1", "renamed")).Id);
        var back = MutationPrincipals.ToAgent(a.OnBehalfOf);
        Assert.Equal(AgentPrincipalKind.Trigger, back.Kind);
        Assert.Equal("t-1", back.TriggerId);

        // A trigger has no human or key behind it: what it creates gets no owner rather than a guessed one.
        Assert.Null(AutomationOwners.OfCaller(a));
    }

    [Fact]
    public void A_principal_without_an_identity_is_anonymous_and_owns_nothing()
    {
        var nobody = new AgentPrincipal(AgentPrincipalKind.WebUser, "someone@example.com", McpPermissionLevels.Read, null);

        var principal = MutationPrincipals.Of(nobody);

        Assert.Equal(PrincipalIds.Anonymous, principal.Id);   // the display name is not promoted to an identity
        Assert.Null(AutomationOwners.OfCaller(principal));
    }

    // --- legacy owner ids --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Ops@Example.com", "user:ops@example.com")]
    [InlineData("user:ops@example.com", "user:ops@example.com")]
    [InlineData("key:k1", "key:k1")]
    [InlineData("", "")]
    [InlineData("Claude Code", "Claude Code")]          // a key name: ambiguous, unchanged
    [InlineData("a@b@c.example", "a@b@c.example")]      // not one email
    [InlineData("trigger:t-1", "trigger:t-1")]          // not an owner: unchanged, suspended
    [InlineData("ops @example.com", "ops @example.com")]
    public void Legacy_owner_ids_are_migrated_only_when_unambiguous(string stored, string expected)
    {
        Assert.Equal(expected, AutomationOwners.MigrateLegacy(stored));
    }

    [Theory]
    [InlineData("ops@example.com")]     // a bare email that was not migrated
    [InlineData("Claude Code")]
    [InlineData("trigger:t-1")]
    public void An_owner_outside_the_scheme_is_suspended(string ownerId)
    {
        var f = AutomationMutationRunnerTests.Build();
        f.Whitelist.Allowed.Add("ops@example.com");
        f.Roles.Map["ops@example.com"] = AppRole.Admin;
        var resolver = new AutomationOwnerResolver(f.Roles, f.Whitelist, f.Keys);

        var owner = resolver.Resolve(ownerId);

        Assert.Equal(McpPermissionLevels.Read, owner.PermissionLevel);
        Assert.Contains("suspended", owner.DisplayName);
    }

    [Fact]
    public void The_owner_store_migrates_a_bare_email_when_it_loads()
    {
        var dir = NewDir();
        var paths = new DataPathOptions(dir);
        File.WriteAllText(paths.AutomationOwnersJson,
            """
            { "autopatch": { "OwnerId": "Admin@Example.com", "ConfirmedAt": "2026-10-01T00:00:00Z" },
              "autoupdate": { "OwnerId": "Claude Code", "ConfirmedAt": "2026-10-01T00:00:00Z" } }
            """);

        var store = new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance);

        Assert.Equal("user:admin@example.com", store.Get(IAutomationOwnerStore.AutoPatch)?.OwnerId);
        Assert.Equal("Claude Code", store.Get(IAutomationOwnerStore.AutoUpdate)?.OwnerId);
    }

    [Fact]
    public async Task The_database_migration_rewrites_bare_email_task_owners_only()
    {
        var path = Path.Combine(NewDir(), "metrics.db");
        await using (var db = Ctx(path))
        {
            var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();
            var all = db.Database.GetMigrations().ToList();
            var ours = all.Single(m => m.EndsWith("_MigrateLegacyTaskOwnerIds", StringComparison.Ordinal));
            await migrator.MigrateAsync(all[all.IndexOf(ours) - 1]);

            foreach (var (id, owner) in new[]
                     {
                         ("t1", "Ops@Example.com"), ("t2", "key:k1"), ("t3", ""), ("t4", "Claude Code"),
                         ("t5", "a@b@c.example"), ("t6", "user:x@example.com"),
                     })
            {
                db.ScheduledTasks.Add(new ScheduledTaskEntity
                {
                    TaskId = id, Name = id, CronExpression = "0 2 * * *", TaskType = ScheduledTaskType.ContainerRestart,
                    TargetId = "web", CreatedBy = owner,
                });
            }
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
        }

        await using var check = Ctx(path);
        var owners = await check.ScheduledTasks.ToDictionaryAsync(t => t.TaskId, t => t.CreatedBy);
        Assert.Equal("user:ops@example.com", owners["t1"]);
        Assert.Equal("key:k1", owners["t2"]);
        Assert.Equal("", owners["t3"]);
        Assert.Equal("Claude Code", owners["t4"]);
        Assert.Equal("a@b@c.example", owners["t5"]);
        Assert.Equal("user:x@example.com", owners["t6"]);
    }

    // ----------------------------------------------------------------------------------------------------------

    private static MutationPrincipal McpGuardrailFilterPrincipal(AgentPrincipal p) =>
        Whiskers.Mcp.McpGuardrailFilter.PrincipalOf(p);

    private static MetricsDbContext Ctx(string dataSource) =>
        new(new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite($"Data Source={dataSource}", sql => sql.MigrationsAssembly("Whiskers.Migrations.Sqlite")).Options);

    private readonly List<string> _dirs = [];

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"principal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

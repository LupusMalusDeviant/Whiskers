using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Auth;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Scheduler;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0020 on the running app: the real gateway, stores, scope guard and <see cref="OwnerAccounts"/> with a mixed
/// sign-in configuration (local accounts and OIDC). A handover is one <c>automation.handover</c> decision of an Admin.
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public sealed class AutomationHandoverWireTests
{
    private const string Admin = "admin@corp.test";
    private const string Gone = "gone@corp.test";
    private const string NewOwner = "new@corp.test";
    private const string Password = "CorrectHorse12!!";
    private static readonly string GoneId = PrincipalIds.User(Gone);
    private static readonly string NewId = PrincipalIds.User(NewOwner);

    private static readonly List<KeyValuePair<string, string?>> Config =
    [
        new("Auth:Disabled", "false"),
        new("Oidc:Enabled", "true"), new("Oidc:Authority", "https://id.example.com"),
        new("Oidc:ClientId", "whiskers"), new("Oidc:ClientSecret", "test-only"),
        new("WHISKERS_DISABLE_LOCAL_DOCKER", "true"),
    ];

    private sealed class Person(string email, AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>(email);
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    /// <summary>The app with Gone's automation: a task and a webhook (database), a trigger and the AutoPatch owner
    /// (written before the boot, as a running instance would have them), an endpoint check (database).</summary>
    private static async Task WithAppAsync(Func<IServiceProvider, string, Task> body)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-0020-wire-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var previous = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        try
        {
            var paths = new DataPathOptions(dataDir);
            await new AutomationOwnerStore(paths, NullLogger<AutomationOwnerStore>.Instance)
                .SetAsync(IAutomationOwnerStore.AutoPatch, GoneId);
            await File.WriteAllTextAsync(paths.AiTriggersJson, $$"""
                { "Triggers": [ { "Id": "g1", "Name": "fix", "Enabled": true, "EventTypes": ["unhealthy"],
                                  "GuardrailPreset": "Standard", "WriteCapabilities": ["container.restart"],
                                  "WriteTargets": ["web"], "OwnerId": "{{GoneId}}" } ] }
                """);

            using var settings = BootSettings.Apply(Config);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(Config));
            });
            using var client = factory.CreateClient();   // forces the host to build and run startup
            var services = factory.Services;

            var roles = services.GetRequiredService<IRoleService>();
            await GatewayArrange.RunAsync("config.roles", async () =>
            {
                await roles.SetRoleAsync(Admin, AppRole.Admin);
                await roles.SetRoleAsync(NewOwner, AppRole.Admin);
                await roles.SetRoleAsync(Gone, AppRole.Admin);   // the role entry outlives the deleted account
            });

            // Gone had a local account, which an Admin deleted.
            var owners = services.GetRequiredService<IOwnerAccounts>();
            using (var scope = services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                Assert.True((await users.CreateAsync(new AppUser { UserName = Gone, Email = Gone }, Password)).Succeeded);
                // The new owner has a local account: present now.
                Assert.True((await users.CreateAsync(new AppUser { UserName = NewOwner, Email = NewOwner }, Password)).Succeeded);
                owners.RememberLocalAccounts();
                Assert.True((await users.DeleteAsync((await users.FindByEmailAsync(Gone))!)).Succeeded);
            }

            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
                db.ScheduledTasks.Add(new ScheduledTaskEntity
                {
                    TaskId = "t1", Name = "nightly backup", CronExpression = "0 2 * * *", TaskType = ScheduledTaskType.SelfBackup,
                    Enabled = true, CreatedBy = GoneId, CreatedVia = GoneId,
                });
                db.Webhooks.Add(new WebhookEntity { WebhookId = "h1", Name = "ci", TargetId = "web", Owner = GoneId, Secret = "unused" });
                db.EndpointChecks.Add(new EndpointCheckEntity
                {
                    Id = "c1", ServerId = "local", Name = "shop", Kind = EndpointCheckKind.Tcp, Target = "example.com", Port = 443,
                    OwnerId = GoneId,
                });
                await db.SaveChangesAsync();
            }
            await body(services, dataDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previous);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly AutomationItemRef[] AllItems =
    [
        new(AutomationKind.ScheduledTask, "t1"), new(AutomationKind.Webhook, "h1"), new(AutomationKind.AiTrigger, "g1"),
        new(AutomationKind.EndpointCheck, "c1"), new(AutomationKind.FleetFeature, IAutomationOwnerStore.AutoPatch),
    ];

    /// <summary>What the dialog does on its "Hand over" button: one decision of <paramref name="role"/>.</summary>
    private static Task<HandoverResult> HandOverAsync(IServiceProvider services, AppRole role = AppRole.Admin,
        string email = Admin)
    {
        var page = new UiMutationRunner(services.GetRequiredService<IMutationGateway>(),
            services.GetRequiredService<ICapabilityCatalog>(), new Person(email, role));
        var handover = services.GetRequiredService<IAutomationHandover>();
        var items = AllItems;
        return page.RunAsync("automation.handover",
            new { fromOwner = GoneId, toOwner = NewId, items = items.Select(i => new { kind = i.Kind.ToString(), id = i.Id }).ToArray() },
            (ResolvedRecord rt, CancellationToken ct) => handover.HandOverAsync(rt.Id, NewId, items, ct));
    }

    private static async Task<List<AuditLogEntity>> HandoverRowsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
            .Where(r => r.Action == "automation.handover").OrderBy(r => r.Id).ToListAsync();
    }

    private static async Task<ScheduledTaskEntity> TaskAsync(IServiceProvider services) =>
        (await services.GetRequiredService<ISchedulerService>().GetTasksAsync()).Single(t => t.TaskId == "t1");

    [Fact]
    public async Task The_automation_hold_is_registered_for_the_account_service()
    {
        await WithAppAsync((services, _) =>
        {
            // Without it the account service creates no account (fail closed); the running app must have it.
            // (The scope guard wraps the registered AutomationHold, so the resolved object is its proxy.)
            Assert.NotNull(services.GetService<IAutomationHold>());
            return System.Threading.Tasks.Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_deleted_local_owner_signing_in_by_oidc_stays_deleted_and_a_handover_moves_task_webhook_trigger_check_and_autopatch()
    {
        await WithAppAsync(async (services, _) =>
        {
            // Gone signs in by OIDC: the deleted local account outranks the federated sign-in.
            services.GetRequiredService<IOwnerAccounts>().RememberFederated(Gone);
            var resolver = services.GetRequiredService<IAutomationOwnerResolver>();
            Assert.Equal(OwnerState.AccountDeleted, resolver.Describe(GoneId).State);
            var unusable = Assert.Single(await services.GetRequiredService<IAutomationHandover>().UnusableOwnersAsync());
            Assert.Equal(GoneId, unusable.OwnerId);

            var result = await HandOverAsync(services);

            Assert.All(result.Items, i => Assert.Equal(HandoverItemStatus.HandedOver, i.Status));
            var task = await TaskAsync(services);
            Assert.Equal((NewId, false), (task.CreatedBy, task.Enabled));
            var hook = (await services.GetRequiredService<IWebhookService>().GetWebhooksAsync()).Single();
            Assert.Equal((NewId, false), (hook.Owner, hook.Enabled));
            var trigger = Assert.Single(services.GetRequiredService<IAiTriggerStore>().Triggers);
            Assert.Equal((NewId, false), (trigger.OwnerId, trigger.Enabled));
            var check = services.GetRequiredService<IEndpointCheckStore>().List().Single();
            Assert.Equal((NewId, false), (check.OwnerId, check.Enabled));
            var fleet = services.GetRequiredService<IAutomationOwnerStore>();
            Assert.Equal(NewId, fleet.GetBlocked(IAutomationOwnerStore.AutoPatch)?.OwnerId);

            var row = Assert.Single(await HandoverRowsAsync(services));
            Assert.True(row.Success);
            // One result per item (the audit row shrinks long fields to fit; every item stays named).
            Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(row.Details!, "\"targetType\":\"automation-item\"").Count);
            Assert.Contains("\"targetName\":\"autopatch\"", row.Details);
            Assert.Single(services.GetRequiredService<IHandoverNotices>().For(NewId));
        });
    }

    [Fact]
    public async Task The_kill_switch_refuses_a_handover_and_writes_nothing()
    {
        await WithAppAsync(async (services, _) =>
        {
            var guardrails = services.GetRequiredService<IGuardrailStore>();
            var policy = guardrails.Current.Clone();
            policy.ReadOnlyMode = true;
            await GatewayArrange.RunAsync("guardrails.save", () => guardrails.SaveAsync(policy,
                new AgentPrincipal(AgentPrincipalKind.WebUser, Admin, McpPermissionLevels.Admin, null, UserEmail: Admin)));

            await Assert.ThrowsAsync<MutationRefusedException>(() => HandOverAsync(services));

            Assert.Equal(GoneId, (await TaskAsync(services)).CreatedBy);
            Assert.False(Assert.Single(await HandoverRowsAsync(services)).Success);
            Assert.Empty(services.GetRequiredService<IHandoverNotices>().For(NewId));
        });
    }

    [Fact]
    public async Task An_operator_is_refused_by_level_with_a_denied_event()
    {
        await WithAppAsync(async (services, _) =>
        {
            await Assert.ThrowsAsync<MutationRefusedException>(() => HandOverAsync(services, AppRole.Operator, NewOwner));

            Assert.Equal(GoneId, (await TaskAsync(services)).CreatedBy);
            var row = Assert.Single(await HandoverRowsAsync(services));
            Assert.False(row.Success);
            Assert.Contains("Denied", row.Details);
        });
    }

    [Fact]
    public async Task After_resume_the_next_scheduled_run_executes_as_the_new_owner_names_the_resumer_and_is_refused_after_their_demotion()
    {
        await WithAppAsync(async (services, _) =>
        {
            await HandOverAsync(services);
            var scheduler = services.GetRequiredService<ISchedulerService>();
            var page = new UiMutationRunner(services.GetRequiredService<IMutationGateway>(),
                services.GetRequiredService<ICapabilityCatalog>(), new Person(Admin, AppRole.Admin));
            var task = await TaskAsync(services);
            task.Enabled = true;
            await page.RunAsync("scheduler.update", new { taskId = task.TaskId },
                (ResolvedTarget _, CancellationToken _) => scheduler.UpdateTaskAsync(task));

            var runner = services.GetRequiredService<IAutomationMutationRunner>();
            task = await TaskAsync(services);
            var actor = AutomationActor.ForTask(task.TaskId, task.Name, task.CreatedBy, task.CreatedVia);
            string? note = null;
            var first = await runner.RunAsync(actor, "selfbackup.create", new { label = "nightly" },
                (ResolvedTarget _, CancellationToken _) =>
                {
                    note = MutationScope.Current!.Results.Select(r => r.Details).FirstOrDefault(d => d?.StartsWith("handover = ") == true);
                    return Task.CompletedTask;
                });
            Assert.Equal(MutationStatus.Executed, first.Status);
            Assert.Contains($"to: {NewId}", note);
            Assert.Contains($"resumedBy: {PrincipalIds.User(Admin)}", note);

            await GatewayArrange.RunAsync("config.roles",
                () => services.GetRequiredService<IRoleService>().SetRoleAsync(NewOwner, AppRole.Viewer));
            var demoted = await runner.RunAsync(actor, "selfbackup.create", new { label = "nightly" },
                (ResolvedTarget _, CancellationToken _) => Task.CompletedTask);
            Assert.Equal(MutationStatus.Denied, demoted.Status);
        });
    }
}

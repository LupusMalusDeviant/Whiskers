using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using PresetStore = Whiskers.Tests.PolicyDecisionRecordTests.PresetStore;

namespace Whiskers.Tests;

/// <summary>PC-D6 / PC-D7 for AI triggers (R1-13, INV-04, INV-27): a trigger is read-only unless an Admin granted it
/// capabilities and targets; a granted write runs only while the owner of the grant holds the authority, re-checked
/// for every mutation; out-of-grant writes and every unattended Confirm are refused; <c>trigger:&lt;id&gt;</c> stays on
/// every audit row of the run.</summary>
public sealed class TriggerAuthorityTests : IDisposable
{
    private const string Admin = "user:admin@example.com";
    private static readonly Capability Restart = new CapabilityCatalog().Find("container.restart")!;
    private static readonly Capability Remove = new CapabilityCatalog().Find("container.remove")!;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whiskers-trigger-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _sp;

    public TriggerAuthorityTests()
    {
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_dir, "m.db")}"));
        _sp = services.BuildServiceProvider();
        using var scope = _sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sp.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- Read-only by default ---------------------------------------------

    [Fact]
    public async Task A_new_trigger_is_read_only()
    {
        var trigger = new AiTrigger { OwnerId = Admin };

        var start = await Authority(new MemoryTriggers(trigger), new Owners()).BeginRunAsync(trigger);

        Assert.False(new AiTrigger().HasWriteGrant);
        Assert.False(trigger.HasWriteGrant);
        Assert.Equal((McpPermissionLevels.Read, (string?)null), (start.Level, start.Refusal));
    }

    [Fact]
    public async Task A_granted_run_holds_no_more_than_the_grant_needs_even_with_an_admin_owner()
    {
        var trigger = Granted();                                    // an admin owner, one capability
        var expected = McpPermissionLevels.Normalize(Restart.RequiredLevel);

        var start = await Authority(new MemoryTriggers(trigger), new Owners()).BeginRunAsync(trigger);

        Assert.Null(start.Refusal);
        Assert.Equal(expected, start.Level);
        Assert.NotEqual(McpPermissionLevels.Admin, start.Level);
    }

    [Fact]
    public async Task A_trigger_without_an_owner_is_suspended_at_its_next_run_read_only_or_not()
    {
        var trigger = new AiTrigger { Id = "t1", Name = "legacy" };
        var notify = new Notify();
        var services = new ServiceCollection().AddSingleton<INotificationService>(notify).BuildServiceProvider();

        var start = await Authority(new MemoryTriggers(trigger), new Owners(), services).BeginRunAsync(trigger);

        Assert.Contains("no owner", start.Refusal);
        Assert.False(trigger.Enabled);
        Assert.Equal("automation_suspended", Assert.Single(notify.Events).EventType);
    }

    [Fact]
    public async Task A_read_only_trigger_whose_owner_lost_sign_in_is_suspended_too()
    {
        var trigger = new AiTrigger { Id = "t1", Name = "watch", OwnerId = Admin };
        var owners = new Owners();
        owners.States[Admin] = OwnerState.NotAllowedToSignIn;

        var start = await Authority(new MemoryTriggers(trigger), owners).BeginRunAsync(trigger);

        Assert.NotNull(start.Refusal);
        Assert.False(trigger.Enabled);
    }

    [Fact]
    public async Task An_owner_demoted_below_a_granted_capability_suspends_the_trigger_before_its_run_starts()
    {
        // container.remove is Admin-only; the owner is an Operator now. The run must not start at write level and
        // fail later on a level check that would never suspend the trigger.
        var trigger = Granted();
        trigger.WriteCapabilities.Add(Remove.Id);
        var owners = new Owners();
        owners.Levels[Admin] = McpPermissionLevels.Write;

        var start = await Authority(new MemoryTriggers(trigger), owners).BeginRunAsync(trigger);

        Assert.Contains("no longer holds 'admin'", start.Refusal);
        Assert.False(trigger.Enabled);
    }

    [Fact]
    public async Task A_trigger_saved_before_R1_13_is_read_only_whatever_its_MaxLevel()
    {
        var path = Path.Combine(_dir, "ai-triggers.json");
        await File.WriteAllTextAsync(path, """
            { "Triggers": [ { "Id": "t1", "Name": "fix", "Enabled": true, "EventTypes": ["unhealthy"],
                              "GuardrailPreset": "Standard", "MaxLevel": "admin", "OwnerId": "user:admin@example.com" } ] }
            """);
        var store = new AiTriggerStore(path);
        await store.InitializeAsync();

        var trigger = Assert.Single(store.Triggers);
        Assert.False(trigger.HasWriteGrant);
        Assert.Equal(McpPermissionLevels.Read, (await Authority(store, new Owners()).BeginRunAsync(trigger)).Level);
        var outcome = await Gateway(store, new Owners()).ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TriggerNotAllowed, outcome.ReasonCode);
    }

    // ---- Ownership is the store's ------------------------------------------

    [Fact]
    public async Task Granting_writes_makes_the_admin_the_owner_and_a_sent_owner_is_ignored()
    {
        var store = new AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        var forged = Granted();
        forged.OwnerId = "user:someone-else@example.com";
        forged.SuspendedReason = null;

        await store.SaveAsync([forged], Editor("admin@example.com"), store.Revision);
        Assert.Equal(Admin, Assert.Single(store.Triggers).OwnerId);

        // Another admin saves without touching the grant: the owner stays.
        await store.SaveAsync([Copy(store.Triggers[0], t => t.Prompt = "changed")], Editor("other@example.com"), store.Revision);
        Assert.Equal(Admin, store.Triggers[0].OwnerId);

        // Changing the grant makes the editor the owner; a read-only trigger keeps an owner too (PC-D6).
        await store.SaveAsync([Copy(store.Triggers[0], t => t.WriteTargets = ["web", "api"])], Editor("other@example.com"), store.Revision);
        Assert.Equal("user:other@example.com", store.Triggers[0].OwnerId);
        await store.SaveAsync([Copy(store.Triggers[0], t => t.WriteCapabilities = [])], Editor("admin@example.com"), store.Revision);
        Assert.Equal(Admin, store.Triggers[0].OwnerId);
        Assert.False(store.Triggers[0].HasWriteGrant);
    }

    [Fact]
    public async Task A_stale_copy_cannot_undo_a_suspension_or_a_narrowed_grant()
    {
        var store = new AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        await store.SaveAsync([Granted()], Editor("admin@example.com"), store.Revision);
        var readAt = store.Revision;
        var staleCopy = store.Triggers.Select(AiTriggerStore.Normalized).ToList();

        // Meanwhile the trigger is suspended; the stale copy still shows it on.
        Assert.True(await store.SuspendAsync("t1", Admin, "owner demoted"));

        await Assert.ThrowsAsync<AiTriggersChangedException>(
            () => store.SaveAsync(staleCopy, Editor("other@example.com"), readAt));
        Assert.False(store.Triggers[0].Enabled);
        Assert.Equal("owner demoted", store.Triggers[0].SuspendedReason);
        Assert.Equal(Admin, store.Triggers[0].OwnerId);

        // Meanwhile another admin narrows the grant; a stale wide copy cannot bring it back.
        readAt = store.Revision;
        var wide = store.Triggers.Select(AiTriggerStore.Normalized).ToList();
        await store.SaveAsync([Copy(store.Triggers[0], c => c.WriteTargets = ["web"])], Editor("admin@example.com"), store.Revision);
        await Assert.ThrowsAsync<AiTriggersChangedException>(
            () => store.SaveAsync(wide, Editor("other@example.com"), readAt));
        Assert.Equal(["web"], store.Triggers[0].WriteTargets);
    }

    [Fact]
    public async Task The_store_drops_capabilities_a_trigger_can_never_be_granted()
    {
        var store = new AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        var trigger = Granted();
        trigger.WriteCapabilities.Add("hetzner.snapshot_delete");

        await store.SaveAsync([trigger], Editor("admin@example.com"), store.Revision);

        Assert.DoesNotContain("hetzner.snapshot_delete", store.Triggers[0].WriteCapabilities);
    }

    [Fact]
    public async Task A_trigger_saved_before_owners_were_recorded_is_taken_over_only_when_an_admin_switches_it_on()
    {
        var path = Path.Combine(_dir, "ai-triggers.json");
        await File.WriteAllTextAsync(path, """
            { "Triggers": [ { "Id": "t1", "Name": "on", "Enabled": true }, { "Id": "t2", "Name": "off", "Enabled": false } ] }
            """);
        var store = new AiTriggerStore(path);
        await store.InitializeAsync();

        // Saving the page leaves them ownerless: nobody took them over.
        await store.SaveAsync(store.Triggers.Select(AiTriggerStore.Normalized).ToList(), Editor("admin@example.com"), store.Revision);
        Assert.All(store.Triggers, t => Assert.Null(t.OwnerId));

        // Switching the disabled one on takes it over; the enabled one is taken over once its first run suspended it.
        await store.SaveAsync(store.Triggers.Select(t => Copy(t, c => c.Enabled = true)).ToList(), Editor("admin@example.com"), store.Revision);
        Assert.Null(store.Triggers.Single(t => t.Id == "t1").OwnerId);
        Assert.Equal(Admin, store.Triggers.Single(t => t.Id == "t2").OwnerId);
        Assert.True(await store.SuspendAsync("t1", null, "it has no owner"));
        await store.SaveAsync(store.Triggers.Select(t => Copy(t, c => c.Enabled = true)).ToList(), Editor("admin@example.com"), store.Revision);
        Assert.Equal(Admin, store.Triggers.Single(t => t.Id == "t1").OwnerId);
    }

    [Fact]
    public async Task Switching_a_suspended_trigger_on_again_is_a_takeover()
    {
        var store = new AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        await store.SaveAsync([Granted()], Editor("admin@example.com"), store.Revision);
        Assert.True(await store.SuspendAsync("t1", Admin, "its owner has no authority left"));
        Assert.False(store.Triggers[0].Enabled);
        Assert.Equal("its owner has no authority left", store.Triggers[0].SuspendedReason);

        // A save that leaves it off keeps the suspension (and the owner).
        await store.SaveAsync([Copy(store.Triggers[0], t => t.SuspendedReason = null)], Editor("other@example.com"), store.Revision);
        Assert.NotNull(store.Triggers[0].SuspendedReason);
        Assert.Equal(Admin, store.Triggers[0].OwnerId);

        await store.SaveAsync([Copy(store.Triggers[0], t => t.Enabled = true)], Editor("other@example.com"), store.Revision);
        Assert.Null(store.Triggers[0].SuspendedReason);
        Assert.Equal("user:other@example.com", store.Triggers[0].OwnerId);
    }

    [Fact]
    public async Task A_suspension_for_an_owner_who_was_replaced_meanwhile_does_nothing()
    {
        var store = new AiTriggerStore(Path.Combine(_dir, "ai-triggers.json"));
        await store.SaveAsync([Granted()], Editor("other@example.com"), store.Revision);

        Assert.False(await store.SuspendAsync("t1", Admin, "lost"));
        Assert.True(store.Triggers[0].Enabled);
    }

    // ---- The gateway enforces the grant ------------------------------------

    [Fact]
    public async Task A_granted_capability_on_a_granted_target_runs()
    {
        var outcome = await Gateway(new MemoryTriggers(Granted()), new Owners())
            .ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
    }

    [Theory]
    [InlineData("container.remove", "web")]      // a capability that was not granted
    [InlineData("container.restart", "db")]      // a target that was not granted
    [InlineData("container.restart", "web2")]    // a pattern is matched whole, not as a prefix
    public async Task A_write_outside_the_grant_is_refused(string capability, string target)
    {
        var ran = false;
        var outcome = await Gateway(new MemoryTriggers(Granted()), new Owners())
            .ExecuteAsync(AgentCall(new CapabilityCatalog().Find(capability)!, target), (_, _) => { ran = true; return Task.FromResult(1); });

        Assert.False(ran);
        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TriggerNotAllowed, outcome.ReasonCode);
    }

    [Theory]
    [InlineData("db")]          // granted by name
    [InlineData("cid-db")]      // granted by id
    public async Task What_an_operation_also_touches_must_be_granted_too_by_any_of_its_names(string grant)
    {
        // Disconnecting "db" from a granted network: the network is granted, the container it cuts off is not.
        var disconnect = new CapabilityCatalog().Find("network.disconnect")!;
        var trigger = Granted();
        trigger.WriteCapabilities.Add(disconnect.Id);
        trigger.WriteTargets.Add("app-net");
        var gateway = Gateway(new MemoryTriggers(trigger), new Owners());
        MutationRequest Call() => AgentCall(disconnect, "app-net") with
        {
            Target = new MutationTarget("network", "net-1", "app-net", "srv1")
            {
                Related = ["cid-db", "db", "cid-api", "api"], RelatedResources = [["cid-db", "db"], ["cid-api", "api"]],
                Resolved = true,
            },
        };

        var refused = await gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(1));
        trigger.WriteTargets.Add(grant);
        var stillRefused = await gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(1));   // "api" is not granted
        trigger.WriteTargets.Add("api");
        var allowed = await gateway.ExecuteAsync(Call(), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, refused.Status);
        Assert.Contains("also touches", refused.Reason);
        Assert.Equal(MutationStatus.Denied, stillRefused.Status);
        Assert.Contains("api", stillRefused.Reason);
        Assert.Equal(MutationStatus.Executed, allowed.Status);
    }

    [Fact]
    public async Task A_dns_record_name_cannot_unlock_another_zone_through_the_real_resolver()
    {
        // Granted: set_dns_record on the zone "allowed.example". The call names the zone "other.example" and a record
        // called "allowed.example" in it.
        var set = new CapabilityCatalog().Find("dns.record_set")!;
        var trigger = Granted();
        trigger.WriteCapabilities.Add(set.Id);
        trigger.WriteTargets.Add("allowed.example");
        var gateway = new MutationGateway(new CapabilityCatalog(), new AllowAll(), new Audit(), [],
            NullLogger<MutationGateway>.Instance, targets: new MutationTargetResolver(new ServiceCollection().BuildServiceProvider()),
            triggers: Authority(new MemoryTriggers(trigger), new Owners()));
        MutationRequest Call(string zone)
        {
            var args = new Dictionary<string, JsonElement>
            {
                ["zone"] = JsonSerializer.SerializeToElement(zone),
                ["name"] = JsonSerializer.SerializeToElement("allowed.example"),
                ["type"] = JsonSerializer.SerializeToElement("A"),
            };
            return AgentCall(set, "x") with { Target = MutationTargets.FromArguments(set, args), Arguments = args };
        }

        var other = await gateway.ExecuteAsync(Call("other.example"), (_, _) => Task.FromResult(1));
        var granted = await gateway.ExecuteAsync(Call("allowed.example"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, other.Status);
        Assert.Equal(MutationReasonCodes.TriggerNotAllowed, other.ReasonCode);
        Assert.Equal(MutationStatus.Executed, granted.Status);
    }

    [Theory]
    [InlineData("net-1", "web")]     // network granted by id; the container is called like the network
    [InlineData("web", "net-1")]     // network granted by name; the container is called like the network's id
    public async Task A_related_resource_sharing_a_name_with_the_target_still_needs_its_own_grant(string grant,
        string containerName)
    {
        // The network (id "net-1", name "web") is granted by one of its names; the container it would cut off
        // carries the other one as its own name.
        var disconnect = new CapabilityCatalog().Find("network.disconnect")!;
        var trigger = Granted();
        trigger.WriteCapabilities.Add(disconnect.Id);
        trigger.WriteTargets.Clear();
        trigger.WriteTargets.Add(grant);
        var request = AgentCall(disconnect, "web") with
        {
            Target = new MutationTarget("network", "net-1", "web", "srv1")
            {
                Related = ["cid-db", containerName], RelatedResources = [["cid-db", containerName]], Resolved = true,
            },
        };

        var outcome = await Gateway(new MemoryTriggers(trigger), new Owners()).ExecuteAsync(request, (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("also touches", outcome.Reason);
    }

    [Theory]
    [InlineData("net-1", "disconnect")]   // network granted by id; the container is named like the network
    [InlineData("safe-net", "disconnect")]
    [InlineData("net-1", "connect")]
    [InlineData("safe-net", "connect")]
    [InlineData("net-1", "remove")]       // removal cuts off every attached container
    public async Task Network_operations_through_the_real_resolver_need_a_grant_for_the_container(string grant, string operation)
    {
        // Network "safe-net" (id "net-1"). Its attached container "db" (id "cid-db") has nothing granted, and the
        // container "net-1" (id "cid-x") is named after the network's id.
        var docker = new FakeDocker(
            new ContainerInfo { Id = "cid-db", Name = "db", ServerId = "local" },
            new ContainerInfo { Id = "cid-x", Name = "net-1", ServerId = "local" });
        docker.Networks.Add(new NetworkInfo
        {
            Id = "net-1", Name = "safe-net", ServerId = "local",
            Containers = [new NetworkContainer { ContainerId = "cid-db", Name = "db" }],
        });
        var capability = new CapabilityCatalog().Find("network." + operation)!;
        var trigger = Granted();
        trigger.WriteCapabilities.Add(capability.Id);
        trigger.WriteTargets.Clear();
        trigger.WriteTargets.Add(grant);
        var gateway = new MutationGateway(new CapabilityCatalog(), new AllowAll(), new Audit(), [],
            NullLogger<MutationGateway>.Instance,
            targets: new MutationTargetResolver(new TestServices(docker,
                new FakeServerConfig(new ServerConfig { Id = "local", Name = "Local", IsDefault = true }))),
            triggers: Authority(new MemoryTriggers(trigger), new Owners()));
        var args = new Dictionary<string, JsonElement> { ["networkId"] = JsonSerializer.SerializeToElement("safe-net") };
        // A removal carrying a stray containerId must still be checked against every attached container.
        args["containerId"] = JsonSerializer.SerializeToElement(operation == "connect" ? "net-1" : operation == "remove" ? "net-1" : "db");
        var request = AgentCall(capability, "x") with { Target = MutationTargets.FromArguments(capability, args), Arguments = args };

        var ran = false;
        var outcome = await gateway.ExecuteAsync(request, (_, _) => { ran = true; return Task.FromResult(1); });

        // "net-1" is granted as a name only when the grant is "net-1" itself: then connecting the container named
        // "net-1" is what was granted. Everything else touches an ungranted container.
        var expectRun = grant == "net-1" && operation == "connect";
        Assert.Equal(expectRun, ran);
        Assert.Equal(expectRun ? MutationStatus.Executed : MutationStatus.Denied, outcome.Status);
    }

    [Fact]
    public async Task A_snapshot_deletion_is_never_granted_to_a_trigger()
    {
        var delete = new CapabilityCatalog().Find("hetzner.snapshot_delete")!;
        var trigger = Granted();
        trigger.WriteCapabilities.Add(delete.Id);
        trigger.WriteTargets.Add("*");
        var request = AgentCall(delete, "srv1") with
        {
            Target = new MutationTarget("cloud-server", "srv1", "srv1", "srv1") { Resolved = true },
        };

        var ran = false;
        var outcome = await Gateway(new MemoryTriggers(trigger), new Owners())
            .ExecuteAsync(request, (_, _) => { ran = true; return Task.FromResult(1); });

        Assert.False(ran);
        Assert.Equal(MutationReasonCodes.TriggerNotAllowed, outcome.ReasonCode);
    }

    [Theory]
    [InlineData("web\n")]
    [InlineData("web\r\n")]
    [InlineData("web2")]
    public void A_grant_matches_the_whole_name_only(string name) =>
        Assert.False(TriggerAuthority.GlobMatch("web", name));

    [Theory]
    [InlineData("dns-zone", "other.example", "allowed.example")]   // a record name is not the zone
    [InlineData("host", "srv2", "allowed-project")]                // a project name is not the host
    [InlineData("container", "new-db", "allowed")]                 // a creation's name is the caller's
    public async Task A_name_the_caller_typed_never_stands_in_for_the_target(string kind, string id, string name)
    {
        var anyCapability = new CapabilityCatalog().All.First(c => c.TargetKind == kind);
        var trigger = Granted();
        trigger.WriteCapabilities.Add(anyCapability.Id);
        trigger.WriteTargets.Add(name);
        var request = AgentCall(anyCapability, name) with
        {
            Target = new MutationTarget(kind, id, name, "srv1") { Resolved = kind != "container" },
        };

        var outcome = await Gateway(new MemoryTriggers(trigger), new Owners()).ExecuteAsync(request, (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Contains("not among the targets", outcome.Reason);
    }

    [Fact]
    public async Task A_mutation_nested_in_a_trigger_call_is_held_to_the_trigger_grant_and_owner()
    {
        // The trigger may run a task on "web"; the task (owned by a person) then restarts "db" and "web". The person's
        // authority would allow both, but the trigger's grant does not reach "db", and a lost owner stops both.
        var triggers = new MemoryTriggers(Granted());
        var owners = new Owners();
        var gateway = Gateway(triggers, owners);
        var nested = new List<MutationStatus>();

        await gateway.ExecuteAsync(AgentCall(Restart, "web"), async (_, _) =>
        {
            nested.Add((await gateway.ExecuteAsync(PersonsCall(Restart, "db", MutationSurface.Automation), (_, _) => Task.FromResult(1))).Status);
            nested.Add((await gateway.ExecuteAsync(PersonsCall(Restart, "web", MutationSurface.Automation), (_, _) => Task.FromResult(1))).Status);
            owners.Levels[Admin] = McpPermissionLevels.Read;
            nested.Add((await gateway.ExecuteAsync(PersonsCall(Restart, "web", MutationSurface.Automation), (_, _) => Task.FromResult(1))).Status);
            return 1;
        });

        Assert.Equal([MutationStatus.Denied, MutationStatus.Executed, MutationStatus.Denied], nested);
        Assert.False(triggers.Items[0].Enabled);
    }

    [Fact]
    public async Task A_switched_off_or_deleted_trigger_cannot_write_even_mid_run()
    {
        var triggers = new MemoryTriggers(Granted());
        var gateway = Gateway(triggers, new Owners());

        triggers.Items[0].Enabled = false;
        var off = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));
        triggers.Items.Clear();
        var gone = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, off.Status);
        Assert.Equal(MutationStatus.Denied, gone.Status);
    }

    [Fact]
    public async Task Without_a_trigger_authority_every_trigger_write_is_refused()
    {
        var gateway = new MutationGateway(new CapabilityCatalog(), new AllowAll(), new Audit(), [],
            NullLogger<MutationGateway>.Instance);

        var outcome = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Denied, outcome.Status);
        Assert.Equal(MutationReasonCodes.TriggerNotAllowed, outcome.ReasonCode);
    }

    // ---- The owner's authority, per mutation -------------------------------

    [Fact]
    public async Task The_owners_authority_is_rechecked_for_every_mutation_and_its_loss_suspends_the_trigger()
    {
        var triggers = new MemoryTriggers(Granted());
        var owners = new Owners();
        var notify = new Notify();
        var services = new ServiceCollection().AddSingleton<INotificationService>(notify).BuildServiceProvider();
        var gateway = Gateway(triggers, owners, services);

        var first = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));
        owners.Levels[Admin] = McpPermissionLevels.Read;                 // the admin was demoted during the run
        var second = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));

        Assert.Equal(MutationStatus.Executed, first.Status);
        Assert.Equal(MutationStatus.Denied, second.Status);
        Assert.Contains("no longer holds 'write'", second.Reason);
        Assert.False(triggers.Items[0].Enabled);
        Assert.NotNull(triggers.Items[0].SuspendedReason);
        var sent = Assert.Single(notify.Events);
        Assert.Equal("automation_suspended", sent.EventType);
    }

    [Theory]
    [InlineData(OwnerState.NotAllowedToSignIn)]
    [InlineData(OwnerState.AccountDeleted)]
    public async Task An_owner_who_can_no_longer_sign_in_suspends_the_trigger_before_its_run_starts(OwnerState state)
    {
        var triggers = new MemoryTriggers(Granted());
        var owners = new Owners();
        owners.States[Admin] = state;

        var start = await Authority(triggers, owners).BeginRunAsync(triggers.Items[0]);

        Assert.NotNull(start.Refusal);
        Assert.False(triggers.Items[0].Enabled);
    }

    [Fact]
    public async Task A_granted_run_starts_with_its_owners_current_level_never_more()
    {
        var triggers = new MemoryTriggers(Granted());
        var owners = new Owners();
        owners.Levels[Admin] = McpPermissionLevels.Write;

        var start = await Authority(triggers, owners).BeginRunAsync(triggers.Items[0]);

        Assert.Equal((McpPermissionLevels.Write, (string?)null), (start.Level, start.Refusal));
    }

    // ---- Unattended Confirm ------------------------------------------------

    [Fact]
    public async Task Every_unattended_Confirm_is_refused_and_never_reaches_the_approval_gate()
    {
        var approvals = new CountingApprovals();
        var gateway = new MutationGateway(new CapabilityCatalog(), new ConfirmAll(), new Audit(), [],
            NullLogger<MutationGateway>.Instance, approvals: approvals,
            triggers: Authority(new MemoryTriggers(Granted()), new Owners()));

        var executed = await gateway.ExecuteAsync(AgentCall(Restart, "web"), (_, _) => Task.FromResult(1));
        var decided = await gateway.DecideAsync(AgentCall(Restart, "web"));
        // A mutation nested in the trigger's one (here: made by the person-owned task it ran) is unattended as well.
        MutationOutcome<int>? nested = null;
        var outer = await new MutationGateway(new CapabilityCatalog(), new AllowAll(), new Audit(), [],
            NullLogger<MutationGateway>.Instance, approvals: approvals,
            triggers: Authority(new MemoryTriggers(Granted()), new Owners()))
            .ExecuteAsync(AgentCall(Restart, "web"), async (_, _) =>
            {
                nested = await gateway.ExecuteAsync(PersonsCall(Restart, "web", MutationSurface.Ui), (_, _) => Task.FromResult(1));
                return 1;
            });

        Assert.Equal(MutationStatus.Denied, executed.Status);
        Assert.Equal(MutationReasonCodes.UnattendedConfirm, executed.ReasonCode);
        Assert.Equal(MutationVerdict.Deny, decided!.Verdict);
        Assert.Equal(MutationStatus.Executed, outer.Status);
        Assert.Equal(MutationStatus.Denied, nested!.Status);
        Assert.Equal(0, approvals.Checks);
    }

    // ---- trigger:<id> on every audit row ------------------------------------

    [Fact]
    public async Task Every_audit_row_of_a_trigger_run_names_the_trigger_nested_mutations_included()
    {
        var gateway = new MutationGateway(new CapabilityCatalog(), new AllowAll(),
            new AuditLogMutationSink(_sp.GetRequiredService<IServiceScopeFactory>()), [], NullLogger<MutationGateway>.Instance,
            triggers: Authority(new MemoryTriggers(Granted()), new Owners()));

        await gateway.ExecuteAsync(AgentCall(Restart, "web"), async (_, _) =>
        {
            await gateway.ExecuteAsync(PersonsCall(Restart, "web", MutationSurface.Automation), (_, _) => Task.FromResult(1));
            return 1;
        });
        await gateway.ExecuteAsync(AgentCall(Remove, "web"), (_, _) => Task.FromResult(1));    // refused

        using var scope = _sp.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking().ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row =>
        {
            using var details = JsonDocument.Parse(row.Details!);
            Assert.Equal("trigger:t1", details.RootElement.GetProperty("trigger").GetString());
        });
    }

    // ---- Helpers -----------------------------------------------------------

    private static AiTrigger Granted() => new()
    {
        Id = "t1", Name = "restart web", EventTypes = ["unhealthy"], GuardrailPreset = "Standard",
        WriteCapabilities = [Restart.Id], WriteTargets = ["web", "shop-*"], OwnerId = Admin,
    };

    private static AiTrigger Copy(AiTrigger t, Action<AiTrigger> change)
    {
        var copy = AiTriggerStore.Normalized(t);
        change(copy);
        return copy;
    }

    private static AgentPrincipal Editor(string email) =>
        new(AgentPrincipalKind.WebUser, email, McpPermissionLevels.Admin, null, UserEmail: email);

    /// <summary>The agent of trigger t1's run asking for <paramref name="capability"/> on container <paramref name="name"/>.</summary>
    internal static MutationRequest AgentCall(Capability capability, string name)
    {
        var trigger = new AgentPrincipal(AgentPrincipalKind.Trigger, "AI trigger 'restart web'", McpPermissionLevels.Admin,
            null, TriggerId: "t1");
        return new MutationRequest(capability.Id, MutationPrincipals.Agent(trigger, "run-1"), MutationSurface.Agent,
            new MutationTarget("container", "id-" + name, name, "srv1") { Resolved = true },
            new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement(name) }, SessionId: "run-1");
    }

    private static MutationRequest PersonsCall(Capability capability, string name, MutationSurface surface) => new(capability.Id,
        new MutationPrincipal(MutationPrincipalKind.Human, "user:ops@example.com", "ops@example.com", McpPermissionLevels.Admin),
        surface, new MutationTarget("container", "id-" + name, name, "srv1") { Resolved = true },
        new Dictionary<string, JsonElement> { ["containerId"] = JsonSerializer.SerializeToElement(name) });

    internal static AiTriggerAuthority Authority(IAiTriggerStore triggers, IAutomationOwnerResolver owners,
        IServiceProvider? sp = null) => new(triggers, owners, new CapabilityCatalog(), sp: sp);

    private static MutationGateway Gateway(IAiTriggerStore triggers, IAutomationOwnerResolver owners,
        IServiceProvider? sp = null) =>
        new(new CapabilityCatalog(), new AllowAll(), new Audit(), [], NullLogger<MutationGateway>.Instance,
            triggers: Authority(triggers, owners, sp));

    internal sealed class MemoryTriggers(params AiTrigger[] triggers) : IAiTriggerStore
    {
        public List<AiTrigger> Items { get; } = triggers.ToList();
        public IReadOnlyList<AiTrigger> Triggers => Items;
        public event Action? Changed { add { } remove { } }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public long Revision => 0;
        public Task SaveAsync(List<AiTrigger> triggers, AgentPrincipal editor, long readAtRevision) => throw new NotSupportedException();

        public Task<bool> SuspendAsync(string triggerId, string? expectedOwnerId, string reason)
        {
            var t = Items.FirstOrDefault(x => x.Id == triggerId && x.OwnerId == expectedOwnerId && x.Enabled);
            if (t is null) return Task.FromResult(false);
            t.Enabled = false;
            t.SuspendedReason = reason;
            return Task.FromResult(true);
        }
    }

    internal sealed class Owners : IAutomationOwnerResolver
    {
        public Dictionary<string, string> Levels { get; } = new() { [Admin] = McpPermissionLevels.Admin };
        public Dictionary<string, OwnerState> States { get; } = new();

        public MutationPrincipal Resolve(string ownerId) => new(MutationPrincipalKind.Human, ownerId, ownerId,
            States.GetValueOrDefault(ownerId, OwnerState.Active) == OwnerState.Active
                ? Levels.GetValueOrDefault(ownerId, McpPermissionLevels.Read)
                : McpPermissionLevels.Read);

        public OwnerDescription Describe(string ownerId) => new(ownerId, States.GetValueOrDefault(ownerId, OwnerState.Active));
    }

    private sealed class AllowAll : IMutationPolicy
    {
        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability) =>
            new(MutationVerdict.Allow, "ok", []);
    }

    private sealed class ConfirmAll : IMutationPolicy
    {
        public MutationPolicyDecision Evaluate(MutationRequest request, Capability capability) =>
            new(MutationVerdict.Confirm, "needs a human", ["confirm-writes"]);
    }

    private sealed class CountingApprovals : IApprovalGate
    {
        public int Checks;

        public Task<ApprovalGateResult> CheckAsync(ApprovalSubject subject, string reason, IReadOnlyList<string> matchedRules,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref Checks);
            throw new InvalidOperationException("an unattended Confirm must never reach the approval gate");
        }

        public bool TryConsume(string approvalId) => false;
    }

    private sealed class Audit : IMutationAuditSink
    {
        public Task<long> BeginAsync(string operationId, MutationRequest request, Capability capability,
            ApprovalGateResult? approval, CancellationToken ct) => Task.FromResult(1L);

        public Task CompleteAsync(long auditId, string operationId, MutationStatus status, VerificationStatus verification,
            string? reason, TimeSpan duration, CancellationToken ct) => Task.CompletedTask;

        public Task RecordDeniedAsync(string operationId, MutationRequest request, Capability? capability,
            MutationStatus status, string reason, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Notify : INotificationService
    {
        public List<NotificationEvent> Events { get; } = [];
        public Task SendAsync(NotificationEvent evt) { lock (Events) Events.Add(evt); return Task.CompletedTask; }
        public Task SendTestAsync() => Task.CompletedTask;
    }
}

using System.Reflection;
using System.Text.RegularExpressions;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.EndpointChecks;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mutations;
using Whiskers.Services.Scheduler;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0020 (INV-66): the store methods that re-own or hold an item are marked with their capability and called
/// only from the handover service (inside <c>automation.handover</c>) or the hold (inside <c>account.create</c>); a
/// resume is marked with its kind's own save capability; no MCP or agent tool reaches the handover.
/// </summary>
public sealed class AutomationHandoverArchitectureTests
{
    private static readonly Type[] Stores =
    [
        typeof(ISchedulerService), typeof(IWebhookService), typeof(IGitDeployService), typeof(IAiTriggerStore),
        typeof(IEndpointCheckStore), typeof(IAutomationOwnerStore),
    ];

    private static string? CapabilityOf(MethodInfo method) => method.GetCustomAttribute<MutatingOperationAttribute>()?.CapabilityId;

    private static IEnumerable<(string Path, string Text)> Sources()
    {
        var src = Path.Combine(AutomationActorSchemeTests.SourceRoot(), "src", "Whiskers");
        return Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText(f)));
    }

    /// <summary>The files that call <paramref name="method"/> on something (a declaration is not a call).</summary>
    private static List<string> CallersOf(string method)
    {
        var call = new Regex($@"\.{method}\(");
        return Sources().Where(s => call.IsMatch(s.Text)).Select(s => s.Path).Distinct().OrderBy(p => p).ToList();
    }

    [Fact]
    public void The_store_handover_methods_are_marked_and_called_only_inside_automation_handover()
    {
        foreach (var store in Stores)
        {
            var method = store.GetMethod("HandOverAsync")
                         ?? throw new InvalidOperationException($"{store.Name} has no HandOverAsync.");
            Assert.Equal("automation.handover", CapabilityOf(method));
        }
        Assert.Equal("automation.handover", CapabilityOf(typeof(IAutomationHandover).GetMethod(nameof(IAutomationHandover.HandOverAsync))!));

        // The service calls the six store methods; the dialog calls the service inside RunAsync("automation.handover").
        Assert.Equal(["Components/Shared/AutomationHandoverDialog.razor", "Services/Mutations/AutomationHandover.cs"],
            CallersOf("HandOverAsync"));
        var dialog = Sources().Single(s => s.Path == "Components/Shared/AutomationHandoverDialog.razor").Text;
        Assert.Matches(@"RunAsync\(""automation\.handover"",[\s\S]*?Handover\.HandOverAsync\(", dialog);
    }

    [Fact]
    public void The_resume_methods_are_marked_with_their_kinds_capability()
    {
        Assert.Equal("gitdeploy.save", CapabilityOf(typeof(IGitDeployService).GetMethod("ResumeAsync")!));
        Assert.Equal("endpointcheck.save", CapabilityOf(typeof(IEndpointCheckStore).GetMethod("ResumeAsync")!));
        Assert.Equal("automation.owner_set", CapabilityOf(typeof(IAutomationOwnerStore).GetMethod("ResumeAsync")!));
        // The other kinds resume through their existing toggles, which keep their capabilities.
        Assert.Equal("webhook.set_enabled", CapabilityOf(typeof(IWebhookService).GetMethod(nameof(IWebhookService.SetEnabledAsync))!));
        Assert.Equal("aitrigger.save", CapabilityOf(typeof(IAiTriggerStore).GetMethod(nameof(IAiTriggerStore.SaveAsync))!));
    }

    [Fact]
    public void No_mcp_tool_or_agent_tool_maps_to_automation_handover()
    {
        var catalog = new CapabilityCatalog();
        foreach (var id in new[] { "automation.handover", "automation.handover_acknowledge", "automation.hold" })
            Assert.Null(catalog.Find(id)!.McpTool);
        var offenders = Sources()
            .Where(s => s.Path.StartsWith("Mcp/", StringComparison.Ordinal) || s.Path.StartsWith("Services/Agent/", StringComparison.Ordinal))
            // The trigger store is one of the six stores: it declares its own handover and hold methods.
            .Where(s => s.Path != "Services/Agent/Triggers/AiTriggerStore.cs")
            .Where(s => s.Text.Contains("\"automation.handover", StringComparison.Ordinal)
                        || s.Text.Contains("\"automation.hold\"", StringComparison.Ordinal)
                        || s.Text.Contains("IAutomationHandover", StringComparison.Ordinal)
                        || s.Text.Contains("IAutomationHold", StringComparison.Ordinal))
            .Select(s => s.Path).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void HoldAsync_is_called_only_inside_account_create()
    {
        foreach (var store in Stores)
            Assert.Equal("automation.hold", CapabilityOf(store.GetMethod("HoldAsync")
                                                          ?? throw new InvalidOperationException($"{store.Name} has no HoldAsync.")));
        Assert.Equal("automation.hold", CapabilityOf(typeof(IAutomationHold).GetMethod(nameof(IAutomationHold.HoldAllAsync))!));

        // (The notification suppressions' HoldAsync is another method of the same name.)
        Assert.Equal(["Services/Mutations/AutomationHandover.cs"],
            CallersOf("HoldAsync").Where(p => p != "Services/Notifications/CompositeNotificationService.cs"));
        Assert.Equal(["Services/Auth/LocalAccountService.cs"], CallersOf("HoldAllAsync"));
        // The hold is a nested capability of account.create, and only of it.
        Assert.Contains("automation.hold", MutationCompositions.Of("account.create"));
    }

    [Fact]
    public void The_owner_resolver_implements_Describe_itself()
    {
        // The handover predicate and the account-create hold read Describe; the interface default answers Active for
        // everyone, which would hand over nothing and hold nothing.
        var map = typeof(AutomationOwnerResolver).GetInterfaceMap(typeof(IAutomationOwnerResolver));
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IAutomationOwnerResolver.Describe));
        Assert.Equal(typeof(AutomationOwnerResolver), map.TargetMethods[index].DeclaringType);
    }
}

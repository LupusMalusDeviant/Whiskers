using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whiskers.Components.Shared;
using Whiskers.Services.Approvals;
using Whiskers.Services.Mutations;
using Whiskers.Startup;

namespace Whiskers.Tests;

/// <summary>Contract 0019 (INV-65): the in-dialog approval exists for a person in a Blazor circuit only. No singleton,
/// hosted service, MCP tool or automation runner can reach the prompt, and only the dialog records a decision via
/// "dialog". <c>MutationBypassArchitectureTests</c> stays unchanged: the pages keep calling the runner as before.</summary>
public sealed class InlineApprovalArchitectureTests
{
    private static IEnumerable<Type> AppTypes()
    {
        try { return typeof(Program).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }

    private static bool Takes(Type type, Type dependency) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Any(p => dependency.IsAssignableFrom(p.ParameterType) || p.ParameterType == dependency);

    [Fact]
    public void The_prompt_is_resolvable_only_in_a_circuit()
    {
        var services = new ServiceCollection();
        services.AddGuardrailPolicy();
        services.AddMutationGateway();
        services.AddInlineApprovalPrompt();

        // Registered per circuit (scoped), together with the circuit marker it checks before it shows anything.
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(IInlineApprovalPrompt)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(InlineApprovalCircuit)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(IUiMutationRunner)).Lifetime);

        // The UI runner is the only type that takes the prompt: no hosted service, MCP tool, automation runner or
        // singleton of any kind.
        var takers = AppTypes().Where(t => !t.IsAbstract && Takes(t, typeof(IInlineApprovalPrompt))).ToList();
        Assert.Equal([typeof(UiMutationRunner)], takers);
        Assert.False(typeof(IHostedService).IsAssignableFrom(typeof(UiMutationRunner)));
        Assert.DoesNotContain(services, d => d.Lifetime == ServiceLifetime.Singleton
            && (d.ImplementationType is { } impl && (Takes(impl, typeof(IInlineApprovalPrompt)) || Takes(impl, typeof(IUiMutationRunner)))));
        Assert.DoesNotContain(AppTypes(), t => t.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolTypeAttribute>() is not null
            && (Takes(t, typeof(IInlineApprovalPrompt)) || Takes(t, typeof(IUiMutationRunner))));
        Assert.False(Takes(typeof(AutomationMutationRunner), typeof(IInlineApprovalPrompt)));
    }

    [Fact]
    public void Only_the_dialog_decides_via_Dialog()
    {
        var root = DeploymentScripts.RepoFile("src", "Whiskers");
        var decisions = new Regex(@"DecideAsync\([^;]*ApprovalChannel\.Dialog", RegexOptions.Singleline);
        var deciders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => decisions.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        Assert.Equal(["Components/Shared/InlineApprovalDialog.razor"], deciders);
    }
}

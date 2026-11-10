using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0013, D6/D7 (§ 4): no deployment path creates a container outside the compose runner — checked mechanically
/// on the IL of the app with <see cref="MutationCallScanner"/>'s reader. A new caller, a new <c>docker run</c> or
/// <c>compose … up</c> string, or a deploy tool that no longer runs <see cref="IComposeRunner.RunAsync"/> fails with
/// its name.
/// </summary>
public sealed partial class DeploymentPathArchitectureTests
{
    private static readonly Assembly App = typeof(Program).Assembly;

    /// <summary>The non-deployment creators the contract names (Boundaries): <c>container.update</c>, the C12 rollback
    /// and the R1-57 host-shell helper.</summary>
    private static readonly string[] ContainerCreators =
    [
        "Whiskers.Services.Docker.Operations.ContainerLifecycleOperations.RecreateContainerAsync",
        "Whiskers.Services.Docker.Operations.ContainerLifecycleOperations.RollbackContainerAsync",
        "Whiskers.Services.Docker.Operations.DockerClientHostShell.CreateAsync",
    ];

    /// <summary>Where a <c>docker run</c> or <c>compose … up</c> command may be written: the compose plan itself and the
    /// listed non-deployment helpers.</summary>
    private static readonly string[] RunCommandOwners =
    [
        "Whiskers.Services.Mutations.ComposePlan",
        "Whiskers.Services.Cve.TrivyScanner",
        "Whiskers.Services.Backup.VolumeBackupService",
        "Whiskers.Services.Mtls.MtlsAllowListCommands",
        "Whiskers.Services.Onboarding.OnboardingService",
        "Whiskers.Services.LogMonitor.Hygiene.LogHygieneAdvice",
    ];

    [Fact]
    public void Only_the_listed_non_deployment_code_calls_dockers_create_container()
    {
        var callers = MutationCallScanner.Callers(App, m =>
                m.DeclaringType == typeof(Docker.DotNet.IContainerOperations) && m.Name == "CreateContainerAsync")
            .Select(c => $"{c.Owner.FullName}.{c.Handler}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ContainerCreators.Order(StringComparer.Ordinal), callers);
    }

    [Fact]
    public void The_docker_service_has_no_method_that_creates_a_container()
    {
        var creating = typeof(IDockerService).GetMethods()
            .Where(m => CreatesContainer().IsMatch(m.Name)
                        || m.GetCustomAttribute<MutatingOperationAttribute>()?.CapabilityId == "container.create")
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(creating);
        Assert.Null(new CapabilityCatalog().Find("container.create"));
    }

    [Fact]
    public void Docker_run_and_compose_up_strings_occur_only_in_the_compose_plan_and_the_listed_helpers()
    {
        var owners = MutationCallScanner.Bodies(App)
            .Where(b => MutationCallScanner.StringLiterals(b.Body).Any(IsRunCommand))
            .Select(b => b.Owner.FullName!)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        var unlisted = owners.Except(RunCommandOwners, StringComparer.Ordinal).ToList();
        Assert.True(unlisted.Count == 0,
            $"A new place writes 'docker run' or 'compose … up' (contract 0013, D6): {string.Join(", ", unlisted)}. " +
            "A deployment runs through IComposeRunner; a non-deployment helper is added here on review.");
        // The scan finds real commands (a prose message such as "docker compose up failed." is none).
        Assert.Contains("Whiskers.Services.Backup.VolumeBackupService", owners);
        Assert.Contains("Whiskers.Services.Onboarding.OnboardingService", owners);
        // AutoPatch's embedded script is the one shell file that runs `compose up` (not a deployment path).
        var scripts = App.GetManifestResourceNames().Where(n => n.EndsWith(".sh", StringComparison.Ordinal)).ToList();
        Assert.Contains(scripts, n => n.EndsWith("img-update.sh", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("app.deploy")]
    [InlineData("compose.deploy")]
    [InlineData("container.env_set")]
    [InlineData("gitdeploy.deploy")]
    public void Every_mcp_tool_of_a_deployment_capability_runs_the_compose_runner(string capabilityId)
    {
        var runners = MutationCallScanner.Callers(App, m =>
                m.DeclaringType == typeof(IComposeRunner) && m.Name == nameof(IComposeRunner.RunAsync))
            .Select(c => $"{c.Owner.FullName}.{c.Handler}")
            .ToHashSet(StringComparer.Ordinal);
        var catalog = new CapabilityCatalog();

        var tools = App.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is { } tool
                        && catalog.FindByMcpTool(tool.Name ?? Whiskers.Services.Agent.AgentToolRegistry.ToSnakeCase(m.Name))?.Id == capabilityId)
            .ToList();

        // gitdeploy.deploy has no MCP tool (UI and webhooks only); the others have exactly the one tool.
        if (catalog.Find(capabilityId)!.McpTool is null)
        {
            Assert.Empty(tools);
            return;
        }
        var tool = Assert.Single(tools);
        Assert.Contains($"{tool.DeclaringType!.FullName}.{tool.Name}", runners);
    }

    /// <summary>A command literal, not a message: a sentence ("docker compose up failed.") ends with a full stop.</summary>
    private static bool IsRunCommand(string literal) => RunCommand().IsMatch(literal) && !literal.TrimEnd().EndsWith('.');

    [GeneratedRegex(@"^(Create|Run)\w*Container")]
    private static partial Regex CreatesContainer();

    // A command, not prose: the literal starts with it, or it follows a shell separator (&&, ;, ||, |).
    [GeneratedRegex(@"(^|&&|;|\|)\s*(sudo\s+)?docker\s+(run\b|compose\b[^\n;&|]*\sup\b)")]
    private static partial Regex RunCommand();
}

using System.Net;
using System.Reflection;
using System.Text.Json;
using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

// R1-36 (INV-43/44): a container call's outcome says what the container is afterwards, read back — the call
// completing is not the effect. A read-back that does not show it is a failed outcome, never a success.
public class VerificationOutcomeTests
{
    /// <summary>A docker service whose container states the test sets; every other call is unsupported.</summary>
    public class StatefulDocker : DispatchProxy
    {
        public Dictionary<string, string> States { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OomKilled { get; } = new(StringComparer.Ordinal);
        /// <summary>Exit code of a container (default: 1 when exited, else 0).</summary>
        public Dictionary<string, int> ExitCodes { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        /// <summary>Run by a call the test scripts (name → what happens); returns the call's value.</summary>
        public Dictionary<string, Func<object?[], object?>> On { get; } = new(StringComparer.Ordinal);

        public static (IDockerService Service, StatefulDocker State) Create()
        {
            var service = Create<IDockerService, StatefulDocker>();
            return (service, (StatefulDocker)(object)service);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            Calls.Add(method!.Name);
            // A scripted inspect (a read that fails, a state that changes) wins over the state table.
            if (method.Name == nameof(IDockerService.InspectContainerStateAsync) && On.TryGetValue(method.Name, out var inspect))
                return inspect(args);
            if (method.Name == nameof(IDockerService.InspectContainerStateAsync))
            {
                var id = (string)args[0]!;
                if (!States.TryGetValue(id, out var state))
                    return Task.FromException<(string, int, bool)>(new DockerContainerNotFoundException(HttpStatusCode.NotFound, ""));
                var exitCode = ExitCodes.TryGetValue(id, out var code) ? code : state == "exited" ? 1 : 0;
                return Task.FromResult((state, exitCode, OomKilled.Contains(id)));
            }
            if (On.TryGetValue(method.Name, out var handler))
            {
                var value = handler(args);
                return method.ReturnType == typeof(Task) ? Task.CompletedTask
                    : typeof(Task<string>).IsAssignableFrom(method.ReturnType) ? Task.FromResult((string)value!)
                    : value;
            }
            throw new NotSupportedException(method.Name);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> Ids = new Dictionary<string, string>
    {
        ["old"] = "aaaaaaaaaaaa", ["new"] = "bbbbbbbbbbbbbbbb",
    };

    internal static IReadOnlyList<IMutationVerifier> Verifiers(IServiceProvider sp) =>
    [
        new ContainerStartVerifier(sp), new ContainerStopVerifier(sp), new ContainerRestartVerifier(sp),
        new ContainerRemoveVerifier(sp), new ContainerUpdateVerifier(sp), new ContainerCreateVerifier(sp),
        new ContainerCreateVerifier(sp, "app.deploy"), new ContainerRollbackVerifier(sp),
    ];

    private static (MutationGateway Gateway, StatefulDocker Docker) Build()
    {
        var (service, docker) = StatefulDocker.Create();
        var sp = new ServiceCollection().AddSingleton(service).BuildServiceProvider();
        var policy = GuardrailPolicy.SafeDefault();
        policy.ProtectedResources = [];
        var gateway = new MutationGateway(new CapabilityCatalog(),
            AgentToolTestHelpers.Policy(new AgentToolTestHelpers.FixedGuardrailStore(policy)),
            new AgentToolTestHelpers.NullMutationAudit(), Verifiers(sp), NullLogger<MutationGateway>.Instance);
        return (gateway, docker);
    }

    private static MutationRequest Request(string capability, object args, string? targetId = null, string? name = null) =>
        new(capability, new MutationPrincipal(MutationPrincipalKind.Human, "admin@example.com", "admin@example.com", McpPermissionLevels.Admin),
            MutationSurface.Ui, new MutationTarget("container", targetId ?? "", name, "local"),
            JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()));

    [Theory]
    [InlineData("container.start", "running", true)]
    [InlineData("container.start", "exited", false)]
    [InlineData("container.start", "restarting", false)]
    [InlineData("container.restart", "running", true)]
    [InlineData("container.restart", "exited", false)]
    [InlineData("container.stop", "exited", true)]
    [InlineData("container.stop", "running", false)]
    [InlineData("container.stop", "paused", false)]
    public async Task Start_stop_and_restart_are_verified_by_the_state_read_back(string capability, string state, bool verified)
    {
        var (gateway, docker) = Build();
        docker.States["aaaaaaaaaaaa"] = state;

        var outcome = await gateway.ExecuteAsync(Request(capability, new { containerId = "aaaaaaaaaaaa", serverId = "local" },
            targetId: "aaaaaaaaaaaa"), (_, _) => Task.FromResult("done"));

        AssertOutcome(outcome, verified, "done");
    }

    [Theory]
    [InlineData("container.start", 0, false, true)]
    [InlineData("container.restart", 0, false, true)]
    [InlineData("container.start", 2, false, false)]
    [InlineData("container.start", 0, true, false)]
    public async Task A_one_shot_container_that_ran_to_completion_counts_as_started(string capability, int exitCode,
        bool oom, bool verified)
    {
        var (gateway, docker) = Build();
        docker.States["aaaaaaaaaaaa"] = "exited";
        docker.ExitCodes["aaaaaaaaaaaa"] = exitCode;
        if (oom) docker.OomKilled.Add("aaaaaaaaaaaa");

        var outcome = await gateway.ExecuteAsync(Request(capability, new { containerId = "aaaaaaaaaaaa" }, "aaaaaaaaaaaa"),
            (_, _) => Task.FromResult("done"));

        AssertOutcome(outcome, verified, "done");
    }

    [Fact]
    public async Task A_container_killed_for_lack_of_memory_is_not_a_verified_start()
    {
        var (gateway, docker) = Build();
        docker.States["aaaaaaaaaaaa"] = "running";
        docker.OomKilled.Add("aaaaaaaaaaaa");

        var outcome = await gateway.ExecuteAsync(Request("container.start", new { containerId = "aaaaaaaaaaaa" }, "aaaaaaaaaaaa"),
            (_, _) => Task.FromResult("done"));

        AssertOutcome(outcome, false, "done");
        Assert.Contains("memory", outcome.Reason);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Remove_is_verified_when_the_container_is_gone(bool stillThere, bool verified)
    {
        var (gateway, docker) = Build();
        if (stillThere) docker.States["aaaaaaaaaaaa"] = "exited";

        var outcome = await gateway.ExecuteAsync(Request("container.remove", new { containerId = "aaaaaaaaaaaa" }, "aaaaaaaaaaaa"),
            (_, _) => Task.FromResult("removed"));

        AssertOutcome(outcome, verified, "removed");
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("exited", false)]
    public async Task Update_reads_back_the_container_the_recreate_returned_not_the_old_one(string newState, bool verified)
    {
        var (gateway, docker) = Build();
        docker.States["aaaaaaaaaaaa"] = "running";        // the old container would pass; it must not be the one read
        docker.States[Ids["new"]] = newState;

        var outcome = await gateway.ExecuteAsync(Request("container.update", new { containerId = "aaaaaaaaaaaa", name = "web" },
            "aaaaaaaaaaaa", "web"), (_, _) => Task.FromResult(Ids["new"]));

        AssertOutcome(outcome, verified, Ids["new"]);
    }

    [Fact]
    public async Task Update_through_a_tool_reads_back_the_container_of_the_same_name()
    {
        var (gateway, docker) = Build();
        docker.States["web"] = "exited";

        var outcome = await gateway.ExecuteAsync(Request("container.update", new { containerId = "aaaaaaaaaaaa" },
            "aaaaaaaaaaaa", "web"), (_, _) => Task.FromResult("Container web updated."));

        AssertOutcome(outcome, false, "Container web updated.");
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("created", false)]
    public async Task A_deployed_container_must_run(string state, bool verified)
    {
        var (gateway, docker) = Build();
        docker.States[Ids["new"]] = state;

        var outcome = await gateway.ExecuteAsync(Request("container.create", new { serverId = "local" }),
            (_, _) => Task.FromResult(Ids["new"]));

        AssertOutcome(outcome, verified, Ids["new"]);
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("exited", false)]
    public async Task An_app_deployment_that_returned_its_container_is_read_back(string state, bool verified)
    {
        var (gateway, docker) = Build();
        docker.States[Ids["new"]] = state;

        var outcome = await gateway.ExecuteAsync(Request("app.deploy", new { serverId = "local" }),
            (_, _) => Task.FromResult(Ids["new"]));

        AssertOutcome(outcome, verified, Ids["new"]);
    }

    [Fact]
    public async Task An_app_deployment_that_names_no_container_is_not_verifiable_never_verified()
    {
        var (gateway, _) = Build();

        var outcome = await gateway.ExecuteAsync(Request("app.deploy", new { serverId = "local" }),
            (_, _) => Task.FromResult("Deployed nginx."));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(VerificationStatus.NotVerifiable, outcome.Verification);
    }

    [Fact]
    public async Task A_rollback_by_snapshot_id_alone_is_recorded_as_not_verifiable_never_as_verified()
    {
        var (gateway, _) = Build();

        var outcome = await gateway.ExecuteAsync(Request("container.rollback", new { rollbackId = 7 }),
            (_, _) => Task.FromResult("web was rolled back to the previous image."));

        Assert.Equal(MutationStatus.Executed, outcome.Status);
        Assert.Equal(VerificationStatus.NotVerifiable, outcome.Verification);
    }

    [Fact]
    public async Task A_call_that_failed_is_not_verified_at_all()
    {
        var (gateway, docker) = Build();

        var outcome = await gateway.ExecuteAsync(Request("container.start", new { containerId = "aaaaaaaaaaaa" }, "aaaaaaaaaaaa"),
            (_, _) => Task.FromException<string>(new InvalidOperationException("docker said no")));

        Assert.Equal(MutationStatus.Failed, outcome.Status);
        Assert.Equal(VerificationStatus.NotRun, outcome.Verification);   // transport failure, no read-back
        Assert.DoesNotContain(nameof(IDockerService.InspectContainerStateAsync), docker.Calls);
    }

    [Fact]
    public async Task A_read_back_that_cannot_be_made_is_a_failed_verification()
    {
        var (gateway, docker) = Build();
        docker.On[nameof(IDockerService.InspectContainerStateAsync)] = _ => throw new IOException("tunnel down");

        var outcome = await gateway.ExecuteAsync(Request("container.restart", new { containerId = "nonexistent" }, "nonexistent"),
            (_, _) => Task.FromResult("done"));

        // "nonexistent" has no state: the inspect fails, and an unproven effect is not a success.
        AssertOutcome(outcome, false, "done");
    }

    [Fact]
    public async Task A_read_the_budget_turned_away_is_tried_again()
    {
        var (gateway, docker) = Build();
        docker.States["aaaaaaaaaaaa"] = "running";
        var reads = 0;
        docker.On[nameof(IDockerService.InspectContainerStateAsync)] = _ => ++reads == 1
            ? Task.FromException<(string, int, bool)>(new Whiskers.Services.Docker.Budget.DuplicateRequestException("inspectstate:a"))
            : Task.FromResult(("running", 0, false));

        var outcome = await gateway.ExecuteAsync(Request("container.start", new { containerId = "aaaaaaaaaaaa" }, "aaaaaaaaaaaa"),
            (_, _) => Task.FromResult("done"));

        AssertOutcome(outcome, true, "done");
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task A_read_back_that_cannot_be_made_is_marked_as_unknown_not_as_a_bad_state()
    {
        var (gateway, docker) = Build();
        docker.On[nameof(IDockerService.InspectContainerStateAsync)] = _ =>
            Task.FromException<(string, int, bool)>(new IOException("tunnel down"));

        var outcome = await gateway.ExecuteAsync(Request("container.start", new { containerId = "aaaaaaaaaaaa" }, "aaaaaaaaaaaa"),
            (_, _) => Task.FromResult("done"));

        AssertOutcome(outcome, false, "done");
        Assert.True(Assert.IsType<MutationVerificationFailedException>(outcome.Error).StateUnknown);
    }

    [Fact]
    public async Task A_failed_verification_reaches_a_page_as_an_error_not_as_the_actions_answer()
    {
        var (gateway, docker) = Build();
        docker.States["aaaaaaaaaaaa"] = "exited";
        var runner = new UiMutationRunner(gateway, new CapabilityCatalog(), new AdminUser());

        var ex = await Assert.ThrowsAsync<MutationVerificationFailedException>(() =>
            runner.RunAsync("container.start", new { containerId = "aaaaaaaaaaaa", serverId = "local" }, _ => Task.FromResult("ok")));

        Assert.Contains("not running", ex.Message);
    }

    [Fact]
    public void Every_container_capability_with_an_effect_on_one_container_has_a_verifier()
    {
        var verified = Verifiers(new ServiceCollection().BuildServiceProvider()).Select(v => v.CapabilityId).ToHashSet();
        string[] lifecycle = ["container.start", "container.stop", "container.restart", "container.remove",
            "container.update", "container.create", "container.rollback"];

        Assert.All(lifecycle, c => Assert.Contains(c, verified));
        Assert.All(lifecycle, c => Assert.NotNull(new CapabilityCatalog().Find(c)));
    }

    private static void AssertOutcome<T>(MutationOutcome<T> outcome, bool verified, T value)
    {
        if (verified)
        {
            Assert.Equal(MutationStatus.Executed, outcome.Status);
            Assert.Equal(VerificationStatus.Verified, outcome.Verification);
            Assert.Equal(value, outcome.Value);
        }
        else
        {
            // The call ran (the verification ran on its result), but its effect is not there: no success, no value.
            Assert.Equal(MutationStatus.Failed, outcome.Status);
            Assert.Equal(VerificationStatus.Failed, outcome.Verification);
            Assert.False(outcome.Succeeded);
            Assert.Equal(default, outcome.Value);
            Assert.IsType<MutationVerificationFailedException>(outcome.Error);
        }
    }

    private sealed class AdminUser : Whiskers.Services.Auth.ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(AppRole.Admin);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(AppRole.Admin >= required);
    }
}

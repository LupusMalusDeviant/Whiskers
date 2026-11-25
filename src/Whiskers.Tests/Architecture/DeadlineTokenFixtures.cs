using System.Text.Json;
using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Tests;

// Fixtures of DeadlineTokenRatchetTests (contract 0023): never run, only scanned. Each method hands exactly one action
// to a gateway entry point.
#pragma warning disable CA1822, IDE0060

/// <summary>Gateway actions that discard or pass on their token, one per method.</summary>
internal class DeadlineTokenFixtures(IUiMutationRunner runner, IMutationGateway gateway)
{
    protected IUiMutationRunner Runner { get; } = runner;
    protected IMutationGateway Gateway { get; } = gateway;

    public Task Discards() => Runner.RunAsync("container.restart", new { },
        (ResolvedContainer c, CancellationToken ct) =>
        {
            _ = ct;
            return Task.CompletedTask;
        });

    public Task IgnoresUnnamed() => Runner.RunAsync("container.restart", new { },
        (ResolvedContainer c, CancellationToken _) => Task.CompletedTask);

    public Task OnlyThrows() => Runner.RunAsync("container.start", new { },
        (ResolvedContainer c, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

    public Task PassesNone() => Runner.RunAsync("container.stop", new { },
        (ResolvedContainer c, CancellationToken ct) => Task.Delay(1, CancellationToken.None));

    public Task MethodGroup() => Runner.RunAsync<ResolvedContainer>("container.remove", new { }, StaticAction);

    public Task VirtualMethodGroup() => Runner.RunAsync<ResolvedContainer>("container.update", new { }, VirtualAction);

    public Task Passes() => Runner.RunAsync("container.restart", new { },
        (ResolvedContainer c, CancellationToken ct) => Task.Delay(1, ct));

    public Task PassesThroughLocal() => Runner.RunAsync("container.restart", new { },
        async (ResolvedContainer c, CancellationToken ct) =>
        {
            var token = ct;
            await Task.Yield();
            await Task.Delay(1, token);
        });

    public Task PassesAcrossAwait() => Runner.RunAsync("container.restart", new { },
        async (ResolvedContainer c, CancellationToken ct) =>
        {
            await Task.Yield();
            await Task.Delay(1, ct);
        });

    public Task PassesToGateway() => Gateway.ExecuteAsync(
        new MutationRequest("container.restart",
            new MutationPrincipal(MutationPrincipalKind.Human, "user:x", "x", McpPermissionLevels.Admin),
            MutationSurface.Ui, new MutationTarget("container", "c1"), new Dictionary<string, JsonElement>()),
        (context, ct) => Task.Delay(1, ct).ContinueWith(_ => 1, ct));

    private static Task StaticAction(ResolvedContainer c, CancellationToken ct) => Task.CompletedTask;

    protected virtual Task VirtualAction(ResolvedContainer c, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>The same discarding action as <see cref="DeadlineTokenFixtures.Discards"/>, behind unrelated lambdas
/// that shift every generated name.</summary>
internal sealed class DeadlineTokenFixturesWithMore(IUiMutationRunner runner, IMutationGateway gateway)
    : DeadlineTokenFixtures(runner, gateway)
{
    private readonly Func<int, int> _first = x => x + 1;
    private readonly Func<int, int> _second = x => x * 2;

    public int Unrelated() => _first(_second(1)) + new[] { 1, 2 }.Select(x => x + 3).Sum();

    public new Task Discards() => Runner.RunAsync("container.restart", new { },
        (ResolvedContainer c, CancellationToken ct) =>
        {
            _ = ct;
            return Task.CompletedTask;
        });
}
#pragma warning restore CA1822, IDE0060

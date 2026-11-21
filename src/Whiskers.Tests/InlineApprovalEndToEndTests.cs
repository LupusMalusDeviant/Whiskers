using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0019 (INV-59, INV-65, INV-35/39) in the booted app: the real gateway, approval gate and store on the real
/// database, the real <c>ProtectedResourceRule</c> and the UI runner as the composition root registers it. Only the
/// onboarding work, Docker and the dialog's clicks are doubles; the scripted dialog decides through the app's own
/// <see cref="IApprovalStore"/> on the "dialog" channel, as the dialog component does.
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public sealed class InlineApprovalEndToEndTests
{
    private const string Admin = InlineApprovalRunnerTests.AdminEmail;
    private const string ProtectedId = "c0ffee0000b2";

    /// <summary>The scripted dialog, shared by every circuit scope of the test: the first ask approves as the person
    /// would and may start something meanwhile; every later ask is closed.</summary>
    private sealed class Dialog(IApprovalStore approvals) : IInlineApprovalPrompt
    {
        public int Asks;
        public Func<Task>? WhileApproving { get; set; }
        public Task? Started { get; private set; }
        public ICurrentUserService User { get; set; } = new InlineApprovalRunnerTests.Person();

        public async Task<InlineApprovalResult> AskAsync(InlineApprovalRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Asks) > 1) return new InlineApprovalResult(InlineApprovalOutcome.LeftPending);
            var resolver = await ApprovalResolver.CurrentAsync(User);
            var decided = await approvals.DecideAsync(request.ApprovalId, true, resolver!, ApprovalChannel.Dialog);
            if (WhileApproving is { } also) Started = Task.Run(also);
            return decided == ApprovalDecisionResult.Decided
                ? new InlineApprovalResult(InlineApprovalOutcome.ApprovedHere, decided)
                : new InlineApprovalResult(InlineApprovalOutcome.LeftPending, decided);
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "whiskers-inline-" + Guid.NewGuid().ToString("N"));
        private readonly string? _previous = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public InlineApprovalRunnerTests.Person User { get; } = new();
        public Dialog Prompt { get; private set; } = null!;
        public FakeDocker Docker { get; } = new(
            new ContainerInfo { Id = ProtectedId, Name = "whiskers", ServerId = "local" },
            new ContainerInfo { Id = "c0ffee0000a1", Name = "web-1", ServerId = "local" }) { AllowMutations = true };

        public static Host Start()
        {
            var host = new Host();
            Directory.CreateDirectory(host._dataDir);
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", host._dataDir);
            host.Factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.ConfigureTestServices(s =>
                    {
                        s.AddSingleton<IDockerService>(host.Docker);
                        s.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(
                            new ServerConfig { Id = "local", Name = "Local", IsDefault = true },
                            InlineApprovalRunnerTests.Edge()));
                        s.AddScoped<ICurrentUserService>(_ => host.User);
                        s.AddScoped<IInlineApprovalPrompt>(sp => host.Prompt ??= new Dialog(sp.GetRequiredService<IApprovalStore>()));
                    });
                });
            _ = host.Factory.Services;   // builds and starts the host
            host.Prompt = new Dialog(host.Factory.Services.GetRequiredService<IApprovalStore>()) { User = host.User };
            return host;
        }

        public async Task<string> OnboardAsync(List<string> runs)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            return await mutations.RunAsync("server.onboard", InlineApprovalRunnerTests.OnboardArgs,
                (ResolvedTarget _, CancellationToken _) => { lock (runs) runs.Add("onboard"); return Task.FromResult("onboarded"); });
        }

        public async Task<List<AuditLogEntity>> AuditAsync(string capability)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
                .Where(r => r.Action == capability).OrderBy(r => r.Id).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Factory is not null) await Factory.DisposeAsync();
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", _previous);
            TestSqlite.ClearPools(_dataDir);
            try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? ApprovalIdIn(string? details)
    {
        if (details is null) return null;
        using var doc = JsonDocument.Parse(details);
        return Find(doc.RootElement, "approval")?.GetProperty("id").GetString();
    }

    private static JsonElement? Find(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Object) return found.Clone();
        foreach (var property in element.EnumerateObject())
            if (Find(property.Value, name) is { } nested) return nested;
        return null;
    }

    [Fact]
    public async Task Re_onboarding_approved_in_the_dialog_runs_once_under_one_operation_id()
    {
        await using var host = Host.Start();
        var runs = new List<string>();

        Assert.Equal("onboarded", await host.OnboardAsync(runs));

        Assert.Equal(["onboard"], runs);
        Assert.Equal(1, host.Prompt.Asks);
        var rows = await host.AuditAsync("server.onboard");
        var pending = Assert.Single(rows, r => r.Outcome == nameof(MutationStatus.PendingApproval));
        var executed = Assert.Single(rows, r => r.Outcome == nameof(MutationStatus.Executed));
        Assert.Equal(2, rows.Count);
        Assert.Equal(pending.OperationId, executed.OperationId);
        var approvalId = ApprovalIdIn(pending.Details);
        Assert.NotNull(approvalId);
        Assert.Equal(approvalId, ApprovalIdIn(executed.Details));
        Assert.Contains("\"via\":\"dialog\"", executed.Details);
        Assert.Contains("\"selfApproved\":true", executed.Details);
        var approval = host.Factory.Services.GetRequiredService<IApprovalStore>().Get(approvalId!)!;
        Assert.Equal(ApprovalChannels.Dialog, approval.DecidedVia);
        Assert.Equal(executed.OperationId, approval.ConsumedBy);
    }

    [Fact]
    public async Task Restarting_the_whiskers_container_from_the_dashboard_is_approved_in_the_dialog()
    {
        await using var host = Host.Start();
        var restarted = 0;

        // As the Dashboard calls it: the resolved container, acted on by its id.
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            await mutations.RunAsync("container.restart", new { containerId = ProtectedId, serverId = "local" },
                async (ResolvedContainer c, CancellationToken ct) =>
                {
                    Interlocked.Increment(ref restarted);
                    await host.Docker.RestartContainerAsync(c.Id, "local");
                });
        }

        Assert.Equal(1, restarted);
        Assert.Equal(1, host.Prompt.Asks);
        var rows = await host.AuditAsync("container.restart");
        var pending = Assert.Single(rows, r => r.Outcome == nameof(MutationStatus.PendingApproval));
        var ran = Assert.Single(rows, r => r.Outcome != nameof(MutationStatus.PendingApproval));
        Assert.Equal(pending.OperationId, ran.OperationId);
        Assert.Contains("\"via\":\"dialog\"", ran.Details);
    }

    [Fact]
    public async Task An_operator_is_refused_and_never_sees_a_prompt()
    {
        await using var host = Host.Start();
        host.User.Role = AppRole.Operator;
        var runs = new List<string>();

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var mutations = scope.ServiceProvider.GetRequiredService<IUiMutationRunner>();
            var refused = await Assert.ThrowsAsync<MutationRefusedException>(() => mutations.RunAsync("container.restart",
                new { containerId = ProtectedId, serverId = "local" },
                (ResolvedContainer _, CancellationToken _) => { runs.Add("restart"); return Task.CompletedTask; }));
            Assert.Equal(MutationStatus.Denied, refused.Status);
            Assert.Null(refused.ApprovalId);
        }

        Assert.Empty(runs);
        Assert.Equal(0, host.Prompt.Asks);
        Assert.Empty(host.Factory.Services.GetRequiredService<IApprovalStore>().GetPending());
    }

    [Fact]
    public async Task Two_continuations_of_one_approval_execute_once()
    {
        await using var host = Host.Start();
        var runs = new List<string>();
        Exception? secondFailure = null;
        // While the dialog's approval is recorded, another tab of the same person repeats the identical click.
        host.Prompt.WhileApproving = async () =>
        {
            try { await host.OnboardAsync(runs); }
            catch (Exception ex) { secondFailure = ex; }
        };

        Exception? firstFailure = null;
        try { await host.OnboardAsync(runs); }
        catch (Exception ex) { firstFailure = ex; }
        await host.Prompt.Started!;

        // INV-35: one grant, one run — whichever continuation came first; the other ran nothing.
        Assert.Equal(["onboard"], runs);
        Assert.True(firstFailure is null ^ secondFailure is null, $"first: {firstFailure?.Message}; second: {secondFailure?.Message}");
        Assert.IsType<MutationRefusedException>(firstFailure ?? secondFailure);
        var executed = (await host.AuditAsync("server.onboard")).Where(r => r.Outcome == nameof(MutationStatus.Executed)).ToList();
        Assert.Single(executed);
        var approvals = host.Factory.Services.GetRequiredService<IApprovalStore>();
        var used = Assert.Single(approvals.GetAll(), a => a.ConsumedAt is not null);
        Assert.Equal(executed[0].OperationId, used.ConsumedBy);
    }
}

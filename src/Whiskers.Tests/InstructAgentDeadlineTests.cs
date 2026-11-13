using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Whiskers.Mcp.Tools;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

/// <summary>
/// Fable review (Medium, INV-40): instruct_agent ran without the caller's token and without a deadline, so a closed MCP
/// request or an agent that loops kept the run — and the tools it called on the hosts — going for good.
/// </summary>
[Collection(nameof(InstructAgentDeadlineTests))]   // Deadline is a static the tests change
public class InstructAgentDeadlineTests
{
    /// <summary>A session that says one thing and then runs until it is cancelled — an agent that never ends.</summary>
    private sealed class EndlessSession : IAgentSession
    {
        public CancellationToken Seen { get; private set; }
        public string SessionId => "s";
        public AgentRunState State => default;
        public IReadOnlyList<AgentMessage> History => [];

        public async IAsyncEnumerable<AgentEvent> SendAsync(string userMessage, string? imageBase64 = null,
            string? imageMediaType = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Seen = ct;
            yield return new AgentEvent.AssistantDelta("working on it");
            await Task.Delay(Timeout.Infinite, ct);
        }

        public Task ResolveConfirmationAsync(string operationId, bool approved, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Agents(IAgentSession session) : IAgentService
    {
        public Task<IAgentSession> StartSessionAsync(AgentContext context, IReadOnlyList<AgentMessage>? seedHistory = null,
            CancellationToken ct = default) => Task.FromResult(session);
        public Task<IAgentSession?> ResumeSessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IAgentSession?>(null);
    }

    private sealed class Principals : IAgentPrincipalResolver
    {
        public AgentPrincipal Resolve(HttpContext httpContext) =>
            new(AgentPrincipalKind.WebUser, "local", McpPermissionLevels.Admin, null, UserEmail: "local@test");
    }

    private static IHttpContextAccessor SignedIn() => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([], AuthConstants.AuthDisabledScheme)),
        },
    };

    private static Task<string> Instruct(IAgentSession session, CancellationToken ct) =>
        AgentTools.InstructAgent(SignedIn(), NullProxy<Whiskers.Services.Mcp.IMcpPermissionService>.Create(),
            new Agents(session), new Principals(), NullProxy<IGuardrailStore>.Create(), "restart everything", ct);

    [Fact]
    public async Task A_run_that_never_ends_stops_at_the_deadline_with_what_it_had()
    {
        var previous = AgentTools.Deadline;
        AgentTools.Deadline = TimeSpan.FromMilliseconds(200);
        try
        {
            var answer = await Instruct(new EndlessSession(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("working on it", answer);
            Assert.Contains("Frist", answer);
        }
        finally { AgentTools.Deadline = previous; }
    }

    [Fact]
    public async Task A_closed_request_stops_the_run()
    {
        var session = new EndlessSession();
        using var caller = new CancellationTokenSource();

        var run = Instruct(session, caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(session.Seen.CanBeCanceled);   // the session got a token tied to the caller, not None
    }
}

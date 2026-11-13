using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mcp;

namespace Whiskers.Mcp.Tools;

[McpServerToolType]
public class AgentTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description(
        "Instruct the Whiskers agent to carry out an operations task described in natural language. " +
        "The agent plans and executes using Whiskers's tools, but runs with YOUR permissions and the " +
        "configured guardrails — it can never exceed your rights or bypass the guardrails. Returns the " +
        "agent's final answer plus a short log of the tools it ran.")]
    public static async Task<string> InstructAgent(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IAgentService agentService,
        IAgentPrincipalResolver principalResolver,
        IGuardrailStore guardrailStore,
        [Description("The task for the agent, in natural language (e.g. 'restart the unhealthy containers on Badwolf').")] string prompt,
        CancellationToken cancellationToken = default)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "instruct_agent");
        if (denied != null) return denied;

        if (string.IsNullOrWhiteSpace(prompt))
            return "Prompt is required.";

        var http = httpContextAccessor.HttpContext;
        if (http == null)
            return "Kein HTTP-Kontext verfügbar.";

        // The agent inherits exactly the rights of its trigger (MCP key or web user).
        var principal = principalResolver.Resolve(http);
        var context = new AgentContext(
            Guid.NewGuid().ToString("N"), principal, AgentOrigin.McpTool, guardrailStore.Current);

        // INV-40 (Fable review): the call ends with its caller — a closed MCP request stops the agent and the tools it
        // runs — and never outlives its deadline, so a run that loops cannot hold the hosts for good.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        var ct = deadline.Token;

        var session = await agentService.StartSessionAsync(context, ct: ct);
        var sb = new StringBuilder();

        try
        {
            await foreach (var ev in session.SendAsync(prompt, ct: ct))
            {
                switch (ev)
                {
                    case AgentEvent.AssistantDelta d:
                        sb.Append(d.Text);
                        break;
                    case AgentEvent.ToolExecuted t:
                        sb.AppendLine();
                        sb.AppendLine($"[{(t.Result.IsError ? "Tool-Fehler" : "Tool")}] {Truncate(t.Result.Content)}");
                        break;
                    case AgentEvent.ConfirmationRequired c:
                        // A Confirm verdict marks an action above the caller's autonomous level. There is no
                        // interactive human on the MCP path, so it is denied — never auto-approved. The caller
                        // must perform the action explicitly (e.g. via the web UI) if it is intended.
                        await session.ResolveConfirmationAsync(c.Call.CorrelationId, false, ct);
                        sb.AppendLine($"[Bestätigung erforderlich, nicht autonom ausgeführt: {c.Call.Name}]");
                        break;
                    case AgentEvent.Failed f:
                        sb.AppendLine($"[Fehler] {f.Message}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            sb.AppendLine($"[Abgebrochen: Frist von {Deadline.TotalMinutes:0} Minuten überschritten]");
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? "(keine Ausgabe)" : result;
    }

    /// <summary>How long one instruct_agent call may run (tests shorten it).</summary>
    internal static TimeSpan Deadline { get; set; } = TimeSpan.FromMinutes(10);

    private static string Truncate(string s) => s.Length > 1500 ? s[..1500] + "…" : s;
}

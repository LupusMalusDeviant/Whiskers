using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using ServerWatch.Models.Agent;
using ServerWatch.Services.Agent;
using ServerWatch.Services.Agent.Guardrails;
using ServerWatch.Services.Mcp;

namespace ServerWatch.Mcp.Tools;

[McpServerToolType]
public class AgentTools
{
    [McpServerTool, Description(
        "Instruct the ServerWatch agent to carry out an operations task described in natural language. " +
        "The agent plans and executes using ServerWatch's tools, but runs with YOUR permissions and the " +
        "configured guardrails — it can never exceed your rights or bypass the guardrails. Returns the " +
        "agent's final answer plus a short log of the tools it ran.")]
    public static async Task<string> InstructAgent(
        IHttpContextAccessor httpContextAccessor,
        IMcpPermissionService permissionService,
        IAgentService agentService,
        IAgentPrincipalResolver principalResolver,
        IGuardrailStore guardrailStore,
        [Description("The task for the agent, in natural language (e.g. 'restart the unhealthy containers on Badwolf').")] string prompt)
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

        var session = await agentService.StartSessionAsync(context);
        var sb = new StringBuilder();

        await foreach (var ev in session.SendAsync(prompt))
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
                    // MCP origin: the external human is already steering via their own agent, so
                    // we auto-confirm here. Deny rules / trigger ceiling still apply hard.
                    await session.ResolveConfirmationAsync(c.Call.Id, true);
                    sb.AppendLine($"[auto-bestätigt: {c.Call.Name}]");
                    break;
                case AgentEvent.Failed f:
                    sb.AppendLine($"[Fehler] {f.Message}");
                    break;
            }
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? "(keine Ausgabe)" : result;
    }

    private static string Truncate(string s) => s.Length > 1500 ? s[..1500] + "…" : s;
}

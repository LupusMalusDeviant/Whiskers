using Microsoft.AspNetCore.Http;
using ServerWatch.Models.Agent;

namespace ServerWatch.Services.Agent;

/// <summary>Discovers the available MCP tools once and exposes them, keyed by their canonical
/// snake_case name, for the catalog/invoker to consume.</summary>
public interface IAgentToolRegistry
{
    IReadOnlyDictionary<string, AgentToolEntry> Tools { get; }
}

/// <summary>Provides the released MCP tools as LLM function definitions.
/// The source is the same registry/levels as McpPermissionLevels — no second source of truth.
/// Returns only tools that are visible to the principal at all, given role AND guardrail policy.</summary>
public interface IAgentToolCatalog
{
    IReadOnlyList<AgentToolDefinition> GetVisibleTools(AgentContext context);
}

/// <summary>Executes EXACTLY ONE tool call — only after the enforced guardrail gate. Without the gate
/// there is no path to the real MCP method. Always returns a result (Deny → IsError).</summary>
public interface IAgentToolInvoker
{
    Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default);
}

/// <summary>Derives the AgentPrincipal from the current HTTP context — web cookie user OR
/// MCP bearer key. Mirrors the resolution in McpPermissionCheck, so the agent inherits exactly the
/// rights of its trigger.</summary>
public interface IAgentPrincipalResolver
{
    AgentPrincipal Resolve(HttpContext httpContext);
}

using System.Text;
using Whiskers.Modules;

namespace Whiskers.Mcp;

/// <summary>
/// Renders the tool catalog — name, level, module, description — as Markdown (Plan-0013 WP3).
///
/// <para>The rendered file is checked in at <c>docs/guide/mcp-tool-catalog.md</c> and compared against a fresh render
/// by <c>McpToolCatalogSnapshotTests</c>. Its purpose is to make a change to the served surface a visible,
/// deliberate act: adding, removing or re-levelling a tool shows up as a diff someone has to accept. Without it,
/// the surface can shift release to release and only the users notice.</para>
///
/// <para>It is also the artefact GAP-5 publishes: Portainer documents its MCP tools, and "governed autonomy" is
/// only a checkable claim if the tools and their levels are public.</para>
/// </summary>
public static class McpToolCatalogRenderer
{
    /// <summary>Deterministic Markdown — stable ordering, no timestamps, no counts that churn on unrelated edits,
    /// so the diff only moves when the surface actually moves.</summary>
    public static string Render(IEnumerable<IWhiskersModule> modules)
    {
        var declarations = McpToolLevelCatalog.Declarations(modules);

        var sb = new StringBuilder();
        sb.AppendLine("# MCP tool catalog");
        sb.AppendLine();
        sb.AppendLine("The tools this Whiskers build serves over MCP, with the permission level each one requires.");
        sb.AppendLine("A level is declared on the tool method via `[McpToolLevel]`; the request path enforces it through");
        sb.AppendLine("`McpPermissionLevels.DefaultToolLevels`, and tests keep the two identical.");
        sb.AppendLine();
        sb.AppendLine("**This file is generated.** Do not edit it by hand — change the tools, then regenerate.");
        sb.AppendLine("`McpToolCatalogSnapshotTests` fails the build whenever it drifts from the code, so a change to the");
        sb.AppendLine("served surface is always a deliberate, reviewable diff.");
        sb.AppendLine();
        sb.AppendLine("Tools of a disabled module are not served. `read` < `write` < `admin`; a caller's key level must");
        sb.AppendLine("reach the tool's level, and the in-process agent is additionally capped by its trigger.");
        sb.AppendLine();

        foreach (var group in declarations.GroupBy(d => d.ModuleId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## Module `{group.Key}`");
            sb.AppendLine();
            sb.AppendLine("| Tool | Level | Description |");
            sb.AppendLine("|---|---|---|");
            foreach (var d in group.OrderBy(d => d.ToolName, StringComparer.Ordinal))
            {
                var description = (d.Description ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
                sb.AppendLine($"| `{d.ToolName}` | {d.Level ?? "**undeclared**"} | {description} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Totals");
        sb.AppendLine();
        sb.AppendLine("| Level | Tools |");
        sb.AppendLine("|---|---|");
        foreach (var level in new[] { "read", "write", "admin" })
            sb.AppendLine($"| {level} | {declarations.Count(d => d.Level == level)} |");
        sb.AppendLine($"| **total** | **{declarations.Count}** |");

        return sb.ToString();
    }
}

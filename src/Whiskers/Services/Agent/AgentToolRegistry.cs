using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Whiskers.Models;
using Whiskers.Models.Agent;

namespace Whiskers.Services.Agent;

/// <summary>A discovered MCP tool: canonical snake_case name, its permission level, the
/// reflected method (for the later invoker) and the LLM function schema.</summary>
public sealed record AgentToolEntry(
    string Name,
    string RequiredLevel,
    string Category,
    MethodInfo Method,
    AgentToolDefinition Definition);

/// <summary>Discovers the [McpServerTool] methods ONCE via reflection and derives the
/// LLM function definitions from them. Canonical name = snake_case of the method, cross-checked against
/// McpPermissionLevels.DefaultToolLevels (the single source of truth). DI parameters (without [Description])
/// are excluded from the schema — only real tool arguments remain.</summary>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    /// <summary>Tools the agent itself must NEVER call — otherwise recursion / permission loops.
    /// instruct_agent invokes the agent; giving it to the agent would let it call itself.</summary>
    public static readonly IReadOnlySet<string> NonAgentTools =
        new HashSet<string>(StringComparer.Ordinal) { "instruct_agent" };

    public IReadOnlyDictionary<string, AgentToolEntry> Tools { get; }

    public AgentToolRegistry() : this(typeof(McpPermissionLevels).Assembly) { }

    public AgentToolRegistry(Assembly toolAssembly)
    {
        var dict = new Dictionary<string, AgentToolEntry>(StringComparer.Ordinal);

        foreach (var type in toolAssembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() == null) continue;

                var name = ToSnakeCase(method.Name);
                // Only tools listed in the canonical permission registry are exposed.
                if (!McpPermissionLevels.DefaultToolLevels.TryGetValue(name, out var level)) continue;
                // Never give agent-disallowed tools (e.g. instruct_agent) to the agent.
                if (NonAgentTools.Contains(name)) continue;

                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? name;
                var category = McpPermissionLevels.ToolCategories.GetValueOrDefault(name, "Other");
                var schema = BuildSchema(method);

                dict[name] = new AgentToolEntry(name, level, category, method,
                    new AgentToolDefinition(name, description, schema));
            }
        }

        Tools = dict;
    }

    /// <summary>"GetContainerDetails" → "get_container_details". Inserts an underscore before every
    /// uppercase letter (except the first) and lowercases it. All tool methods are simple
    /// PascalCase, so this rule suffices and matches the DefaultToolLevels keys.</summary>
    public static string ToSnakeCase(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static JsonElement BuildSchema(MethodInfo method)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in method.GetParameters())
        {
            // Tool arguments carry [Description]; DI services (IDockerService, IHttpContextAccessor …)
            // do not and are thus cleanly filtered out.
            var desc = p.GetCustomAttribute<DescriptionAttribute>();
            if (desc == null) continue;

            properties[p.Name!] = new JsonObject
            {
                ["type"] = MapJsonType(p.ParameterType),
                ["description"] = desc.Description
            };

            if (!p.HasDefaultValue && !IsNullableReference(p))
                required.Add(p.Name!);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schema["required"] = required;

        return JsonSerializer.SerializeToElement(schema);
    }

    private static string MapJsonType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return "string";
        if (t == typeof(bool)) return "boolean";
        if (t == typeof(int) || t == typeof(long) || t == typeof(short)) return "integer";
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return "number";
        return "string";
    }

    private static bool IsNullableReference(ParameterInfo p)
    {
        if (p.ParameterType.IsValueType) return false;
        // Nullable annotation (string?) ⇒ optional. NullabilityInfoContext reads the #nullable metadata.
        var info = new NullabilityInfoContext().Create(p);
        return info.WriteState == NullabilityState.Nullable;
    }
}

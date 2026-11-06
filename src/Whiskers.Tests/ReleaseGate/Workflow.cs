using YamlDotNet.RepresentationModel;

namespace Whiskers.Tests.ReleaseGate;

/// <summary>Small YAML helpers over a GitHub workflow file, for the release gate's R10 and R16.</summary>
internal static class Workflow
{
    public static YamlMappingNode Root(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode m ? m : new YamlMappingNode();
    }

    public static Dictionary<string, YamlMappingNode> Jobs(string yaml)
        => (Child(Root(yaml), "jobs") as YamlMappingNode)?.Children
               .ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => (YamlMappingNode)kv.Value, StringComparer.Ordinal)
           ?? new Dictionary<string, YamlMappingNode>(StringComparer.Ordinal);

    public static YamlNode? Child(YamlNode? node, string key)
        => node is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v : null;

    public static string? Scalar(YamlNode? node, string key) => (Child(node, key) as YamlScalarNode)?.Value;

    public static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job)
        => (Child(job, "steps") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>() ?? [];

    public static bool Truthy(string? value) => value is not null && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    public static List<string> Needs(YamlMappingNode job) => Child(job, "needs") switch
    {
        YamlScalarNode s => [s.Value!],
        YamlSequenceNode q => q.Children.OfType<YamlScalarNode>().Select(n => n.Value!).ToList(),
        _ => [],
    };

    public static HashSet<string> Ancestors(Dictionary<string, YamlMappingNode> jobs, string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(jobs.TryGetValue(id, out var start) ? Needs(start) : []);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next) || !jobs.TryGetValue(next, out var job)) continue;
            foreach (var n in Needs(job)) stack.Push(n);
        }
        return seen;
    }
}

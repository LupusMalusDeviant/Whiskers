using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Services.Onboarding;

/// <summary>What <see cref="ScrapeConfigEditor.AddTarget"/> did with the scrape config.</summary>
public enum ScrapeConfigEditOutcome
{
    /// <summary>The target was inserted; <see cref="ScrapeConfigEdit.Yaml"/> holds the new file.</summary>
    Added,
    /// <summary>A target for the address is already there; the file stays as it is.</summary>
    AlreadyPresent,
    /// <summary>The file has no <c>static_configs:</c> line to insert under; the file stays as it is.</summary>
    NoAnchor,
}

/// <summary>The result of a scrape-config edit: the outcome and, for <see cref="ScrapeConfigEditOutcome.Added"/>, the new text.</summary>
public sealed record ScrapeConfigEdit(ScrapeConfigEditOutcome Outcome, string? Yaml);

/// <summary>Onboarding step 8 (contract 0017 part I1): adds a node_exporter target to the VictoriaMetrics scrape config.
/// The edit is done here, in C#; the host only reads and writes the file (base64 both ways). The server id and the
/// address reach the file only as double-quoted YAML scalars written by YamlDotNet's emitter, and never reach any
/// program text. The rest of the file (comments, other targets) is kept line for line.</summary>
public static class ScrapeConfigEditor
{
    public const int NodeExporterPort = 9100;

    /// <summary>Inserts <c>targets: ["ip:9100"]</c> with <c>labels: { server: "id" }</c> as the first item under the
    /// first <c>static_configs:</c>. A target already present for the address changes nothing. The result is parsed
    /// again and must name the new target; otherwise the edit throws and nothing is written.</summary>
    public static ScrapeConfigEdit AddTarget(string yaml, string serverId, string tsIp)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrEmpty(serverId);
        ArgumentException.ThrowIfNullOrEmpty(tsIp);
        if (OnboardingCommands.TailnetIPv4(tsIp) != tsIp)
            throw new ArgumentException("The scrape target takes only a normalised IPv4 literal.", nameof(tsIp));
        var target = $"{tsIp}:{NodeExporterPort}";
        var text = yaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (Targets(text).Any(t => string.Equals(t.Target, target, StringComparison.Ordinal)))
            return new ScrapeConfigEdit(ScrapeConfigEditOutcome.AlreadyPresent, null);

        var lines = text.TrimEnd().Split('\n').ToList();
        var anchor = lines.FindIndex(l => l.Trim() == "static_configs:");
        if (anchor < 0)
            return new ScrapeConfigEdit(ScrapeConfigEditOutcome.NoAnchor, null);

        var indent = ItemIndent(lines, anchor);
        var pad = new string(' ', indent);
        lines.InsertRange(anchor + 1,
        [
            $"{pad}- targets: [{Scalar(target)}]",
            $"{pad}  labels: {{ server: {Scalar(serverId)} }}",
        ]);
        var result = string.Join('\n', lines) + "\n";

        // Fail closed: the written file must parse and carry exactly the target we meant.
        if (!Targets(result).Any(t => t.Target == target && t.Server == serverId))
            throw new InvalidOperationException("The edited scrape config does not carry the new target; nothing was written.");
        return new ScrapeConfigEdit(ScrapeConfigEditOutcome.Added, result);
    }

    /// <summary>A string as one double-quoted YAML scalar, written by YamlDotNet's emitter: quotes, line breaks and
    /// every other special character are escaped, so the value cannot end the scalar.</summary>
    public static string Scalar(string value)
    {
        using var writer = new StringWriter();
        var emitter = new Emitter(writer, new EmitterSettings().WithMaxSimpleKeyLength(1024).WithBestWidth(int.MaxValue));
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, isImplicit: true));
        emitter.Emit(new Scalar(null, null, value, ScalarStyle.DoubleQuoted, isPlainImplicit: false, isQuotedImplicit: true));
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        var s = writer.ToString().Trim();
        if (!s.StartsWith('"') || !s.EndsWith('"') || s.Contains('\n'))
            throw new InvalidOperationException("The YAML emitter did not produce a single-line double-quoted scalar.");
        return s;
    }

    // Items under the anchor use the indent of the first existing item; an empty list gets the key's indent + 2.
    private static int ItemIndent(List<string> lines, int anchor)
    {
        var keyIndent = lines[anchor].Length - lines[anchor].TrimStart(' ').Length;
        for (var i = anchor + 1; i < lines.Count; i++)
        {
            var t = lines[i].TrimStart(' ');
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var ind = lines[i].Length - t.Length;
            return t.StartsWith('-') && ind >= keyIndent ? ind : keyIndent + 2;
        }
        return keyIndent + 2;
    }

    /// <summary>Every (target, server label) under <c>scrape_configs[*].static_configs[*]</c>. A file that does not parse
    /// throws: no edit of a file we cannot read.</summary>
    internal static IEnumerable<(string Target, string? Server)> Targets(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) yield break;
        if (Child(root, "scrape_configs") is not YamlSequenceNode jobs) yield break;
        foreach (var job in jobs.Children.OfType<YamlMappingNode>())
        {
            if (Child(job, "static_configs") is not YamlSequenceNode statics) continue;
            foreach (var sc in statics.Children.OfType<YamlMappingNode>())
            {
                var server = Child(sc, "labels") is YamlMappingNode labels ? (Child(labels, "server") as YamlScalarNode)?.Value : null;
                if (Child(sc, "targets") is not YamlSequenceNode targets) continue;
                foreach (var t in targets.Children.OfType<YamlScalarNode>())
                    if (t.Value is { } v) yield return (v, server);
            }
        }
    }

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;
}

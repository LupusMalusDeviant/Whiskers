using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Services.Mutations;

/// <summary>A mount as the third-party detection reads it (contract 0013, D16): its kind (<c>bind</c>, <c>volume</c>,
/// <c>tmpfs</c>, …), the host path of a bind, and the path in the container.</summary>
public sealed record ToolMount(string Type, string? Source, string Target);

/// <summary>
/// What contract 0013 § 3 reads about one service (from <c>docker compose config</c>) or one container (from
/// <c>docker inspect</c>): its image reference, <c>entrypoint</c> and <c>command</c> (<c>null</c> when not set), its
/// environment, mounts, working directory and labels. <see cref="Env"/> holds every key, but a value only for the keys
/// in <see cref="ComposeThirdParty.ReadableEnv"/> (paths and switches, never a token or a key); every other value is
/// <c>null</c>.
/// </summary>
public sealed record ToolSpec
{
    public string Image { get; init; } = "";
    public IReadOnlyList<string>? Entrypoint { get; init; }
    public IReadOnlyList<string>? Command { get; init; }
    public IReadOnlyDictionary<string, string?> Env { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);
    public IReadOnlyList<ToolMount> Mounts { get; init; } = [];
    public string? WorkingDir { get; init; }
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Contract 0013, U3 and D16–D17: proxies and tunnels that publish a service without a label on it — Traefik with
/// <c>exposedByDefault</c>, cloudflared, Tailscale <c>serve</c>/Funnel — recognised by image name, by <c>argv[0]</c>, by a
/// word of a <c>sh -c</c> string or by an environment key, and turned into <see cref="ExposureKind.ExternalRoute"/>
/// exposures (key = the signal, value <c>true</c>; the host Traefik's: its container name). Anything the detection needs
/// but cannot read throws <see cref="ComposePlanException"/> (the plan is refused); a fact that cannot be known by design
/// (<c>cloudflared.tunnel</c>, <c>tailscale.state</c>) counts as <c>public</c>. No value — token, key, URL — is ever
/// part of an exposure's detail.
/// </summary>
public static class ComposeThirdParty
{
    public const string TraefikExposedByDefault = "traefik.exposedbydefault";
    public const string TraefikHost = "traefik.exposedbydefault@host";
    public const string CloudflaredTunnel = "cloudflared.tunnel";
    public const string TailscaleFunnel = "tailscale.funnel";
    public const string TailscaleServe = "tailscale.serve";
    public const string TailscaleState = "tailscale.state";

    /// <summary>The most a configuration file may hold (D16).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>The environment keys whose values the detection reads: paths and switches only.</summary>
    public static readonly IReadOnlySet<string> ReadableEnv = new HashSet<string>(StringComparer.Ordinal)
    {
        "TS_SERVE_CONFIG", "TS_STATE_DIR", "XDG_CONFIG_HOME", "HOME", "TRAEFIK_CONFIGFILE",
        "TRAEFIK_PROVIDERS_DOCKER", "TRAEFIK_PROVIDERS_SWARM",
        "TRAEFIK_PROVIDERS_DOCKER_EXPOSEDBYDEFAULT", "TRAEFIK_PROVIDERS_SWARM_EXPOSEDBYDEFAULT",
    };

    private static readonly string[] Shells = ["sh", "bash", "ash", "dash", "zsh", "busybox"];

    /// <summary>The class of a signal: <c>tailscale.serve</c> is <c>published</c> (tailnet only), every other one —
    /// and any key this version does not know — <c>public</c>.</summary>
    public static string ClassOf(string key) =>
        key == TailscaleServe ? ExposureClasses.Published : ExposureClasses.Public;

    // ------------------------------------------------------------------------------------------- reading the inputs

    /// <summary>An environment as the detection keeps it: every key, a value only for <see cref="ReadableEnv"/>.</summary>
    public static Dictionary<string, string?> Env(IEnumerable<KeyValuePair<string, string?>> entries)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            if (key.Length > 0) env[key] = ReadableEnv.Contains(key) ? value : null;
        return env;
    }

    /// <summary><c>KEY=VALUE</c> entries (Docker's <c>Config.Env</c>).</summary>
    public static IEnumerable<KeyValuePair<string, string?>> EnvList(IEnumerable<string> entries) =>
        entries.Select(e => e.IndexOf('=') is var eq and >= 0
            ? new KeyValuePair<string, string?>(e[..eq], e[(eq + 1)..])
            : new KeyValuePair<string, string?>(e, null));

    /// <summary>A service of <c>docker compose config --format json</c>.</summary>
    public static ToolSpec FromConfig(string image, JsonElement definition)
    {
        var env = new List<KeyValuePair<string, string?>>();
        if (ComposeExposures.Property(definition, "environment") is { } environment)
        {
            if (environment.ValueKind == JsonValueKind.Object)
                foreach (var p in environment.EnumerateObject())
                    env.Add(new(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null));
            else if (environment.ValueKind == JsonValueKind.Array)
                env.AddRange(EnvList(environment.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "")));
        }
        var mounts = new List<ToolMount>();
        if (ComposeExposures.Property(definition, "volumes") is { ValueKind: JsonValueKind.Array } volumes)
            foreach (var v in volumes.EnumerateArray())
                if (ComposeExposures.Text(v, "target") is { Length: > 0 } target)
                    mounts.Add(new ToolMount(ComposeExposures.Text(v, "type") ?? "volume", ComposeExposures.Text(v, "source"), target));
        if (ComposeExposures.Property(definition, "tmpfs") is { } tmpfs)
            foreach (var t in tmpfs.ValueKind == JsonValueKind.Array ? tmpfs.EnumerateArray().Select(e => e.GetString() ?? "")
                         : tmpfs.ValueKind == JsonValueKind.String ? [tmpfs.GetString() ?? ""] : [])
                if (t.Split(':')[0] is { Length: > 0 } target) mounts.Add(new ToolMount("tmpfs", null, target));
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ComposeExposures.Property(definition, "labels") is { ValueKind: JsonValueKind.Object } l)
            foreach (var p in l.EnumerateObject())
                labels[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        return new ToolSpec
        {
            Image = image,
            Entrypoint = Argv(ComposeExposures.Property(definition, "entrypoint")),
            Command = Argv(ComposeExposures.Property(definition, "command")),
            Env = Env(env),
            Mounts = mounts,
            WorkingDir = ComposeExposures.Text(definition, "working_dir"),
            Labels = labels,
        };
    }

    /// <summary>A container of <c>docker inspect</c>: its effective argv, environment and mounts. The image is the
    /// reference the project names (<see cref="ComposePins.ReferenceLabel"/>) when it was pinned, else <c>Config.Image</c>.</summary>
    public static ToolSpec FromInspect(JsonElement container)
    {
        var config = ComposeExposures.Property(container, "Config") is { ValueKind: JsonValueKind.Object } c ? c : default;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.ValueKind == JsonValueKind.Object
            && ComposeExposures.Property(config, "Labels") is { ValueKind: JsonValueKind.Object } l)
            foreach (var p in l.EnumerateObject())
                labels[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        var mounts = new List<ToolMount>();
        if (ComposeExposures.Property(container, "Mounts") is { ValueKind: JsonValueKind.Array } list)
            foreach (var m in list.EnumerateArray())
                if (ComposeExposures.Text(m, "Destination") is { Length: > 0 } target)
                    mounts.Add(new ToolMount((ComposeExposures.Text(m, "Type") ?? "volume").ToLowerInvariant(),
                        ComposeExposures.Text(m, "Source"), target));
        return new ToolSpec
        {
            Image = labels.GetValueOrDefault(ComposePins.ReferenceLabel) is { Length: > 0 } reference ? reference
                : config.ValueKind == JsonValueKind.Object ? ComposeExposures.Text(config, "Image") ?? "" : "",
            Entrypoint = config.ValueKind == JsonValueKind.Object ? Argv(ComposeExposures.Property(config, "Entrypoint")) ?? [] : [],
            Command = config.ValueKind == JsonValueKind.Object ? Argv(ComposeExposures.Property(config, "Cmd")) ?? [] : [],
            Env = Env(EnvList(config.ValueKind == JsonValueKind.Object ? ComposeExposures.Strings(config, "Env") : [])),
            Mounts = mounts,
            WorkingDir = config.ValueKind == JsonValueKind.Object ? ComposeExposures.Text(config, "WorkingDir") : null,
            Labels = labels,
        };
    }

    /// <summary>
    /// The service as it will run: compose's <c>entrypoint</c> replaces the image's and drops its <c>Cmd</c> (as Docker
    /// does), compose's <c>command</c> replaces the <c>Cmd</c>; the image's environment and labels under compose's. Without
    /// <paramref name="imageConfig"/> (the image is not on the host yet) only what compose says is known.
    /// </summary>
    public static ToolSpec Effective(ToolSpec compose, string? imageConfig)
    {
        if (string.IsNullOrWhiteSpace(imageConfig)) return compose;
        try
        {
            using var document = JsonDocument.Parse(imageConfig);
            var config = document.RootElement;
            if (config.ValueKind != JsonValueKind.Object) return compose;
            var entrypoint = compose.Entrypoint ?? Argv(ComposeExposures.Property(config, "Entrypoint")) ?? [];
            var command = compose.Command ?? (compose.Entrypoint is not null ? [] : Argv(ComposeExposures.Property(config, "Cmd")) ?? []);
            var env = Env(EnvList(ComposeExposures.Strings(config, "Env")));
            foreach (var (k, v) in compose.Env) env[k] = v;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (ComposeExposures.Property(config, "Labels") is { ValueKind: JsonValueKind.Object } l)
                foreach (var p in l.EnumerateObject())
                    labels[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            foreach (var (k, v) in compose.Labels) labels[k] = v;
            return compose with
            {
                Entrypoint = entrypoint,
                Command = command,
                Env = env,
                WorkingDir = compose.WorkingDir ?? (ComposeExposures.Text(config, "WorkingDir") is { Length: > 0 } wd ? wd : null),
                Labels = labels,
            };
        }
        catch (JsonException)
        {
            throw new ComposePlanException("The image configuration of a proxy or tunnel cannot be read; nothing was started.");
        }
    }

    private static IReadOnlyList<string>? Argv(JsonElement? element) => element switch
    {
        { ValueKind: JsonValueKind.Array } a => a.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToList(),
        { ValueKind: JsonValueKind.String } s => (s.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
        _ => null,
    };

    // ------------------------------------------------------------------------------------------- recognising a tool

    /// <summary>The last path segment of an image reference without tag or digest (<c>tailscale/tailscale:v1</c> →
    /// <c>tailscale</c>); empty for an image id.</summary>
    public static string ImageName(string reference)
    {
        var name = reference.Trim();
        if (name.StartsWith("sha256:", StringComparison.Ordinal)) return "";
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var slash = name.LastIndexOf('/');
        var colon = name.LastIndexOf(':');
        if (colon > slash) name = name[..colon];
        return (slash >= 0 ? name[(slash + 1)..] : name).ToLowerInvariant();
    }

    /// <summary>The official Traefik image (<c>traefik</c>, <c>library/traefik</c>, <c>docker.io/library/traefik</c>),
    /// which ships no static configuration file.</summary>
    public static bool IsOfficialTraefik(string reference)
    {
        var name = reference.Trim().ToLowerInvariant();
        var at = name.IndexOf('@');
        if (at >= 0) name = name[..at];
        var slash = name.LastIndexOf('/');
        var colon = name.LastIndexOf(':');
        if (colon > slash) name = name[..colon];
        return name is "traefik" or "library/traefik" or "docker.io/library/traefik" or "index.docker.io/library/traefik";
    }

    private static string Base(string word)
    {
        var w = word.Trim();
        var slash = w.LastIndexOf('/');
        return (slash >= 0 ? w[(slash + 1)..] : w).ToLowerInvariant();
    }

    /// <summary>The effective argv: entrypoint, then command.</summary>
    public static IReadOnlyList<string> ArgvOf(ToolSpec spec) => [.. spec.Entrypoint ?? [], .. spec.Command ?? []];

    /// <summary>The script of a <c>sh -c</c> argv (a shell with an option holding <c>c</c>), or <c>null</c>.</summary>
    private static string? Script(IReadOnlyList<string> argv)
    {
        if (argv.Count < 2 || !Shells.Contains(Base(argv[0]))) return null;
        var i = Base(argv[0]) == "busybox" && argv.Count > 2 && Shells.Contains(Base(argv[1])) ? 2 : 1;
        for (; i < argv.Count; i++)
        {
            var a = argv[i];
            if (!a.StartsWith('-') || a.StartsWith("--", StringComparison.Ordinal)) return null;
            if (a.Contains('c') && i + 1 < argv.Count) return argv[i + 1];
        }
        return null;
    }

    /// <summary>
    /// The words of a shell script and which of them stand where a command name stands (start, after <c>;</c>, <c>|</c>,
    /// <c>&amp;</c>, <c>(</c>, a backtick or a newline, and after <c>exec</c> / <c>nohup</c> / <c>env</c>). A command word
    /// that holds a variable may name a tool: unreadable (D16).
    /// </summary>
    private static List<string> ScriptWords(string script)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var commandPosition = true;
        char? quote = null;
        void Flush()
        {
            if (current.Length == 0) return;
            var word = current.ToString();
            current.Clear();
            if (commandPosition && (word.Contains('$') || word.Contains('`')))
                throw new ComposePlanException(
                    "A shell command of a service runs a program named by a variable, which may be a proxy or tunnel; it cannot be read.");
            words.Add(word);
            commandPosition = word is "exec" or "nohup" or "env" or "then" or "do" or "else" or "!" or "sudo";
        }
        foreach (var ch in script)
        {
            if (quote is not null)
            {
                if (ch == quote) quote = null;
                else current.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '\'' or '"':
                    quote = ch;
                    break;
                case ' ' or '\t':
                    Flush();
                    break;
                case ';' or '|' or '&' or '(' or ')' or '\n' or '\r' or '`':
                    Flush();
                    commandPosition = ch != ')';
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }
        Flush();
        return words;
    }

    private enum Tool { Traefik, Cloudflared, Tailscale }

    private static readonly (Tool Tool, string Image, string[] Binaries, string[] EnvKeys)[] Tools =
    [
        (Tool.Traefik, "traefik", ["traefik"], []),
        (Tool.Cloudflared, "cloudflared", ["cloudflared"], ["TUNNEL_TOKEN", "TUNNEL_TOKEN_FILE"]),
        (Tool.Tailscale, "tailscale", ["containerboot", "tailscaled", "tailscale"], ["TS_AUTHKEY", "TS_AUTH_KEY", "TS_SERVE_CONFIG"]),
    ];

    /// <summary>Whether <paramref name="spec"/> runs a tool of contract 0013's table (by image, argv, sh -c or env).</summary>
    public static bool IsTool(ToolSpec spec, string tool) =>
        Recognise(spec, ArgvOf(spec), WordsOf(ArgvOf(spec))).Any(t => t.ToString().Equals(tool, StringComparison.OrdinalIgnoreCase));

    private static List<string> WordsOf(IReadOnlyList<string> argv) => Script(argv) is { } script ? ScriptWords(script) : [];

    private static HashSet<Tool> Recognise(ToolSpec spec, IReadOnlyList<string> argv, IReadOnlyList<string> words)
    {
        var found = new HashSet<Tool>();
        var image = ImageName(spec.Image);
        foreach (var (tool, name, binaries, keys) in Tools)
            if (image == name
                || (argv.Count > 0 && binaries.Contains(Base(argv[0])))
                || words.Any(w => binaries.Contains(Base(w)))
                || keys.Any(spec.Env.ContainsKey))
                found.Add(tool);
        return found;
    }

    // ------------------------------------------------------------------------------------------- host files

    private const string AbsentMarker = "__WHISKERS_ABSENT__";

    /// <summary>The longest output a file read may give: <see cref="MaxFileBytes"/> in base64, and a little more.</summary>
    public const int MaxReadChars = MaxFileBytes / 3 * 4 + 64;

    /// <summary>D16: reads a host file as base64, or prints the absent marker. Read only.</summary>
    public static string ReadCommand(string hostPath)
    {
        var quoted = Whiskers.Utils.ShellUtils.Quote(hostPath);
        return $"if [ -e {quoted} ]; then base64 -w0 < {quoted}; else echo {AbsentMarker}; fi";
    }

    /// <summary>What <see cref="ReadCommand"/> printed: the content, or <c>null</c> when the file does not exist. A failed
    /// or truncated read, a file larger than <see cref="MaxFileBytes"/> or output that is not base64 throws.</summary>
    public static string? ParseRead(Whiskers.Services.Server.CommandResult result)
    {
        const string unreadable = "A configuration file of a proxy or tunnel cannot be read on the server.";
        if (!result.Success || result.OutputTruncated) throw new ComposePlanException(unreadable);
        var output = result.Output.Trim();
        if (output == AbsentMarker) return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(output);
        }
        catch (FormatException)
        {
            throw new ComposePlanException(unreadable);
        }
        if (bytes.Length > MaxFileBytes)
            throw new ComposePlanException("A configuration file of a proxy or tunnel is larger than 1 MiB.");
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>A <see cref="HostFileReader"/> over a read-only executor of one server.</summary>
    public static HostFileReader Reader(Whiskers.Services.Server.IHostCommandExecutor executor, string serverId) =>
        async (path, ct) => ParseRead(await executor.ExecuteAsync(serverId, ReadCommand(path), TimeSpan.FromSeconds(15), ct,
            MaxReadChars));

    // ------------------------------------------------------------------------------------------- detection

    /// <summary>
    /// Reads a host file for the detection: its content, <c>null</c> when it does not exist; throws
    /// <see cref="ComposePlanException"/> when it cannot be read or is larger than <see cref="MaxFileBytes"/>.
    /// </summary>
    public delegate Task<string?> HostFileReader(string hostPath, CancellationToken ct);

    /// <summary>
    /// The third-party exposures of one service (or container) <paramref name="service"/>. Every host file read is recorded
    /// in <paramref name="files"/> (path → SHA-256 of its content, or <c>absent</c>), so step 4 can compare.
    /// </summary>
    public static async Task<IReadOnlyList<ComposeExposure>> DetectAsync(string service, ToolSpec spec, HostFileReader read,
        IDictionary<string, string> files, CancellationToken ct)
    {
        var argv = ArgvOf(spec);
        var words = WordsOf(argv);
        var tools = Recognise(spec, argv, words);
        var found = new List<ComposeExposure>();
        void Add(string key) => found.Add(Exposure(service, key, "true"));

        if (tools.Contains(Tool.Traefik) && await TraefikExposesAsync(spec, argv, words, read, files, ct))
            Add(TraefikExposedByDefault);
        if (tools.Contains(Tool.Cloudflared))
            Add(CloudflaredTunnel);
        if (tools.Contains(Tool.Tailscale))
        {
            var tokens = argv.Concat(words).ToList();
            var (funnel, serve) = (Follows(tokens, "funnel"), Follows(tokens, "serve"));
            if (spec.Env.TryGetValue("TS_SERVE_CONFIG", out var serveConfig))
            {
                var (configFunnel, configServe) = await ServeConfigAsync(spec, serveConfig, read, files, ct);
                funnel |= configFunnel;
                serve |= configServe && !configFunnel;
            }
            else if (spec.Env.ContainsKey("TS_STATE_DIR") || spec.Mounts.Any(m => Under("/var/lib/tailscale", m.Target)))
                Add(TailscaleState);
            if (funnel) Add(TailscaleFunnel);
            if (serve) Add(TailscaleServe);
        }
        return found;
    }

    /// <summary>An <see cref="ExposureKind.ExternalRoute"/> exposure: key and value are shown (the value is <c>true</c> or
    /// a container name), never anything of the configuration.</summary>
    public static ComposeExposure Exposure(string service, string key, string value) =>
        new(service, ExposureKind.ExternalRoute, $"external route {key}") { Key = key, Value = value };

    /// <summary>A <c>tailscale</c> word followed (after its options) by <paramref name="verb"/>.</summary>
    private static bool Follows(IReadOnlyList<string> tokens, string verb)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (Base(tokens[i]) != "tailscale") continue;
            for (var j = i + 1; j < tokens.Count; j++)
            {
                if (tokens[j].StartsWith('-')) continue;
                if (tokens[j] == verb) return true;
                break;
            }
        }
        return false;
    }

    private static bool Under(string directory, string path)
    {
        var p = path.TrimEnd('/');
        return p == directory || p.StartsWith(directory + "/", StringComparison.Ordinal);
    }

    /// <summary>A container path through the mounts: the host path of a bind, <c>("unreadable", null)</c> on a volume,
    /// tmpfs or other mount, <c>("image", null)</c> when no mount covers it (inside the image).</summary>
    private static (string Kind, string? HostPath) Locate(ToolSpec spec, string path)
    {
        var mount = spec.Mounts.Where(m => Under(m.Target.TrimEnd('/'), path) || m.Target.TrimEnd('/').Length == 0)
            .OrderByDescending(m => m.Target.TrimEnd('/').Length).FirstOrDefault();
        if (mount is null) return ("image", null);
        if (!string.Equals(mount.Type, "bind", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(mount.Source)
            || !mount.Source.StartsWith('/'))
            return ("unreadable", null);
        var rest = path.TrimEnd('/')[mount.Target.TrimEnd('/').Length..];
        return ("bind", mount.Source.TrimEnd('/') + rest);
    }

    private static async Task<string?> ReadAsync(string hostPath, HostFileReader read, IDictionary<string, string> files,
        CancellationToken ct)
    {
        var content = await read(hostPath, ct);
        files[hostPath] = content is null ? "absent" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return content;
    }

    private static string Absolute(ToolSpec spec, string path) =>
        path.StartsWith('/') ? path : $"{(string.IsNullOrEmpty(spec.WorkingDir) ? "/" : spec.WorkingDir.TrimEnd('/'))}/{path.TrimStart('.', '/')}";

    // ------------------------------------------------------------------------------------------- Tailscale

    private static async Task<(bool Funnel, bool Serve)> ServeConfigAsync(ToolSpec spec, string? configured,
        HostFileReader read, IDictionary<string, string> files, CancellationToken ct)
    {
        const string unreadable = "The Tailscale serve configuration (TS_SERVE_CONFIG) of a service cannot be read.";
        if (string.IsNullOrWhiteSpace(configured)) throw new ComposePlanException(unreadable);
        var (kind, host) = Locate(spec, Absolute(spec, configured.Trim()));
        if (kind != "bind" || host is null) throw new ComposePlanException(unreadable);
        var content = await ReadAsync(host, read, files, ct) ?? throw new ComposePlanException(unreadable);
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ComposePlanException(unreadable);
            var funnel = AnyFunnel(root);
            var serve = root.EnumerateObject().Any(p => p.Name is "TCP" or "Web" or "Services"
                && p.Value.ValueKind == JsonValueKind.Object && p.Value.EnumerateObject().Any());
            return (funnel, serve);
        }
        catch (JsonException)
        {
            throw new ComposePlanException(unreadable);
        }

        static bool AnyFunnel(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var p in element.EnumerateObject())
                {
                    if (p.Name == "AllowFunnel" && p.Value.ValueKind == JsonValueKind.Object
                        && p.Value.EnumerateObject().Any(f => f.Value.ValueKind == JsonValueKind.True))
                        return true;
                    if (AnyFunnel(p.Value)) return true;
                }
            else if (element.ValueKind == JsonValueKind.Array)
                return element.EnumerateArray().Any(AnyFunnel);
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------- Traefik

    /// <summary>The Docker or Swarm provider's state in one source: enabled, and <c>exposedByDefault</c> (null = not set,
    /// Traefik's default <c>true</c>).</summary>
    private sealed class Providers
    {
        public readonly Dictionary<string, (bool Enabled, bool? Exposed)> State = new(StringComparer.Ordinal);

        public void Enable(string provider, bool enabled) =>
            State[provider] = (enabled, State.GetValueOrDefault(provider).Exposed);

        /// <summary>Setting an option of a provider enables it (fail closed: also after an explicit <c>false</c>).</summary>
        public void Expose(string provider, bool exposed) => State[provider] = (true, exposed);

        public bool Exposes => State.Values.Any(s => s.Enabled && s.Exposed != false);
    }

    private static bool Bool(string? value, string what) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "true" or "" or null => true,
            "false" => false,
            _ => throw new ComposePlanException($"The Traefik setting {what} of a service cannot be read."),
        };

    /// <summary>
    /// D16: whether a Traefik service enables the <c>docker</c> or <c>swarm</c> provider with <c>exposedByDefault</c> not
    /// <c>false</c> — in its CLI arguments, its environment or its static file; any source that does counts.
    /// </summary>
    private static async Task<bool> TraefikExposesAsync(ToolSpec spec, IReadOnlyList<string> argv, IReadOnlyList<string> words,
        HostFileReader read, IDictionary<string, string> files, CancellationToken ct)
    {
        // CLI (also inside a sh -c string).
        var cli = new Providers();
        string? configFile = null;
        var tokens = argv.Concat(words).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith('-')) continue;
            var flag = token.TrimStart('-');
            var eq = flag.IndexOf('=');
            var name = (eq >= 0 ? flag[..eq] : flag).ToLowerInvariant();
            string? value = eq >= 0 ? flag[(eq + 1)..] : null;
            if (value is null && name is "configfile" or "providers.docker.exposedbydefault" or "providers.swarm.exposedbydefault"
                && i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
                value = tokens[++i];
            if (name == "configfile") { configFile = value; continue; }
            foreach (var provider in (string[])["docker", "swarm"])
            {
                var key = "providers." + provider;
                if (name == key) cli.Enable(provider, Bool(value, "--" + key));
                else if (name == key + ".exposedbydefault") cli.Expose(provider, Bool(value, "--" + name));
                else if (name.StartsWith(key + ".", StringComparison.Ordinal)) cli.Enable(provider, true);
            }
        }
        if (cli.Exposes) return true;

        // Environment.
        var env = new Providers();
        foreach (var (key, value) in spec.Env)
            foreach (var provider in (string[])["DOCKER", "SWARM"])
            {
                var p = provider.ToLowerInvariant();
                var prefix = "TRAEFIK_PROVIDERS_" + provider;
                if (key == prefix) env.Enable(p, Bool(value, key));
                else if (key == prefix + "_EXPOSEDBYDEFAULT") env.Expose(p, Bool(value, key));
                else if (key.StartsWith(prefix + "_", StringComparison.Ordinal)) env.Enable(p, true);
            }
        if (env.Exposes) return true;
        configFile ??= spec.Env.GetValueOrDefault("TRAEFIK_CONFIGFILE");

        // The static file: the configured one, else Traefik's default places.
        var official = IsOfficialTraefik(spec.Image);
        if (!string.IsNullOrWhiteSpace(configFile))
        {
            var content = await TraefikFileAsync(spec, Absolute(spec, configFile.Trim()), official, required: true, read, files, ct);
            return content is not null && FileExposes(configFile, content);
        }
        var directories = new List<string> { "/etc/traefik" };
        if (spec.Env.GetValueOrDefault("XDG_CONFIG_HOME") is { Length: > 0 } xdg) directories.Add(xdg.TrimEnd('/'));
        directories.Add((spec.Env.GetValueOrDefault("HOME") is { Length: > 0 } home ? home.TrimEnd('/') : "/root") + "/.config");
        directories.Add(string.IsNullOrEmpty(spec.WorkingDir) ? "" : spec.WorkingDir.TrimEnd('/'));
        foreach (var directory in directories.Distinct(StringComparer.Ordinal))
            foreach (var extension in (string[])["yml", "yaml", "toml"])
            {
                var path = $"{directory}/traefik.{extension}";
                if (await TraefikFileAsync(spec, path, official, required: false, read, files, ct) is { } content
                    && FileExposes(path, content))
                    return true;
            }
        return false;
    }

    private static async Task<string?> TraefikFileAsync(ToolSpec spec, string path, bool official, bool required,
        HostFileReader read, IDictionary<string, string> files, CancellationToken ct)
    {
        var unreadable = $"The Traefik static configuration of a service ({System.IO.Path.GetFileName(path)}) cannot be read.";
        var (kind, host) = Locate(spec, path);
        switch (kind)
        {
            case "image":
                // The official image ships no static file; any other image might.
                if (official && !required) return null;
                throw new ComposePlanException(unreadable);
            case "bind":
                var content = await ReadAsync(host!, read, files, ct);
                if (content is null && required) throw new ComposePlanException(unreadable);
                return content;
            default:
                throw new ComposePlanException(unreadable);
        }
    }

    /// <summary>A static file (YAML or the TOML subset of D16) enables the docker/swarm provider without
    /// <c>exposedByDefault: false</c>.</summary>
    internal static bool FileExposes(string path, string content) =>
        path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ? TomlExposes(content) : YamlExposes(content);

    private static bool YamlExposes(string content)
    {
        const string unreadable = "The Traefik static configuration of a service does not parse.";
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(content));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            throw new ComposePlanException(unreadable);
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return false;
        var providers = Child(root, "providers");
        if (providers is null) return false;
        if (providers is not YamlMappingNode providerMap) return providers is YamlScalarNode { Value: null or "" } ? false
            : throw new ComposePlanException(unreadable);
        var state = new Providers();
        foreach (var provider in (string[])["docker", "swarm"])
        {
            if (Child(providerMap, provider) is not { } node) continue;
            state.Enable(provider, true);
            if (node is YamlMappingNode options && Child(options, "exposedbydefault") is { } exposed)
                state.Expose(provider, exposed is YamlScalarNode s ? Bool(s.Value, "exposedByDefault") : throw new ComposePlanException(unreadable));
        }
        return state.Exposes;

        static YamlNode? Child(YamlMappingNode map, string key) =>
            map.Children.FirstOrDefault(c => c.Key is YamlScalarNode s && string.Equals(s.Value, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>The TOML subset of D16: <c>[providers.docker]</c> / <c>[providers.swarm]</c> tables (and their
    /// sub-tables) with an <c>exposedByDefault</c> key, and dotted <c>providers.docker.…</c> keys at the top level. Any
    /// other construct that names a provider — an inline table, a <c>[providers]</c> table, an array of tables, a
    /// multi-line string — is outside the subset: unreadable.</summary>
    private static bool TomlExposes(string content)
    {
        const string unreadable = "The Traefik static configuration of a service uses TOML outside the supported subset.";
        if (content.Contains("\"\"\"", StringComparison.Ordinal) || content.Contains("'''", StringComparison.Ordinal))
            throw new ComposePlanException(unreadable);
        var state = new Providers();
        string? table = null;
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;
            var lower = line.ToLowerInvariant().Replace(" ", "").Replace("\"", "").Replace("'", "");
            if (lower.StartsWith("[[", StringComparison.Ordinal))
            {
                if (lower.Contains("providers", StringComparison.Ordinal)) throw new ComposePlanException(unreadable);
                table = "";
                continue;
            }
            if (lower.StartsWith('['))
            {
                table = lower.Trim('[', ']');
                if (table == "providers") throw new ComposePlanException(unreadable);
                foreach (var provider in (string[])["docker", "swarm"])
                    if (table == "providers." + provider || table.StartsWith("providers." + provider + ".", StringComparison.Ordinal))
                        state.Enable(provider, true);
                continue;
            }
            var eq = lower.IndexOf('=');
            if (eq <= 0) throw new ComposePlanException(unreadable);
            var key = lower[..eq];
            var value = lower[(eq + 1)..];
            var full = string.IsNullOrEmpty(table) ? key : table + "." + key;
            if (full.Contains("providers", StringComparison.Ordinal) && value.StartsWith('{'))
                throw new ComposePlanException(unreadable);
            foreach (var provider in (string[])["docker", "swarm"])
            {
                var prefix = "providers." + provider;
                if (full == prefix) throw new ComposePlanException(unreadable);
                if (full == prefix + ".exposedbydefault") state.Expose(provider, Bool(value, "exposedByDefault"));
                else if (full.StartsWith(prefix + ".", StringComparison.Ordinal)) state.Enable(provider, true);
            }
        }
        return state.Exposes;

        static string StripComment(string line)
        {
            char? quote = null;
            for (var i = 0; i < line.Length; i++)
            {
                if (quote is not null) { if (line[i] == quote) quote = null; continue; }
                if (line[i] is '"' or '\'') quote = line[i];
                else if (line[i] == '#') return line[..i];
            }
            return line;
        }
    }

    // ------------------------------------------------------------------------------------------- host Traefik (Q1)

    /// <summary>
    /// Q1: a running Traefik container on the server outside the plan's project, from its <c>docker inspect</c>, routes
    /// every container that does not say <c>traefik.enable=false</c>. An unreadable configuration of it counts as
    /// <c>exposedByDefault</c> (confirmed, never a refusal: it is not part of the deployment).
    /// </summary>
    public static async Task<bool> HostTraefikExposesAsync(JsonElement? container, string listedImage, HostFileReader read,
        CancellationToken ct)
    {
        if (container is not { } doc) return true;
        try
        {
            var spec = FromInspect(doc);
            if (spec.Image.Length == 0) spec = spec with { Image = listedImage };
            var argv = ArgvOf(spec);
            return await TraefikExposesAsync(spec, argv, WordsOf(argv), read, new Dictionary<string, string>(StringComparer.Ordinal), ct);
        }
        catch (ComposePlanException)
        {
            return true;
        }
    }

    /// <summary>A service a Traefik on the host leaves alone: a literal <c>traefik.enable=false</c> label.</summary>
    public static bool OptsOutOfTraefik(IReadOnlyDictionary<string, string> labels) =>
        labels.Any(l => string.Equals(l.Key, "traefik.enable", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(l.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
}

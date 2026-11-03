using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whiskers.Services.Vpn;
using Whiskers.Services.Vpn.Providers;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>
/// R1-43 (F-21, INV-41): a VPN enrollment key never appears in a command line, a log line or a result message, not even
/// as a fragment. The in-app providers hand it to the CLI as a file (<c>tailscale up --auth-key=file:…</c>,
/// <c>netbird up --setup-key-file=…</c>) of mode 0600 in a directory of mode 0700, deleted when the call returns; the
/// legacy entrypoint writes it with the shell's own <c>printf</c> into such a file the same way.
/// </summary>
public class VpnSecretHandlingTests : IDisposable
{
    internal const string TailscaleKey = "tskey-auth-kQ7xW2pL9mZ4vB8nR3tY6uJ1hG5fD0sA";
    internal const string NetbirdKey = "A1B2C3D4-E5F6-4789-9ABC-DEF012345678";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpn-secret-" + Guid.NewGuid().ToString("N"));

    public VpnSecretHandlingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- Test doubles ----------------------------------------------------------------------------------------------

    /// <summary>One recorded CLI call, with the key file's content and modes as they were while the CLI ran.</summary>
    internal sealed record Call(string File, IReadOnlyList<string> Args, string? KeyPath, string? KeyContent,
        UnixFileMode? KeyMode, UnixFileMode? KeyDirMode);

    /// <summary>Records every call; answers <c>version</c> and <c>status</c>, and <c>up</c> with <see cref="UpResult"/>.</summary>
    internal sealed class RecordingVpnRunner : IVpnProcessRunner
    {
        public List<Call> Calls { get; } = [];
        public List<(string File, IReadOnlyList<string> Args)> Detached { get; } = [];
        public VpnProcessResult UpResult { get; set; } = new(0, "", "");

        /// <summary>The daemon answers <c>status --json</c> (no tailscaled is started).</summary>
        public bool DaemonRunning { get; set; } = true;

        public Task<VpnProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken ct,
            int timeoutMs = 30000)
        {
            var args = arguments.ToList();
            var keyArg = args.FirstOrDefault(a => a.StartsWith("--auth-key=file:", StringComparison.Ordinal)
                                                  || a.StartsWith("--setup-key-file=", StringComparison.Ordinal));
            string? path = keyArg is null ? null
                : keyArg.StartsWith("--auth-key=file:", StringComparison.Ordinal) ? keyArg["--auth-key=file:".Length..]
                : keyArg["--setup-key-file=".Length..];
            string? content = null;
            UnixFileMode? mode = null, dirMode = null;
            if (path is not null && File.Exists(path))
            {
                content = File.ReadAllText(path);
                if (!OperatingSystem.IsWindows())
                {
                    mode = File.GetUnixFileMode(path);
                    dirMode = File.GetUnixFileMode(Path.GetDirectoryName(path)!);
                }
            }
            lock (Calls) Calls.Add(new Call(file, args, path, content, mode, dirMode));

            if (args.Contains("version")) return Task.FromResult(new VpnProcessResult(0, "1.0", ""));
            if (args.Contains("status"))
                return Task.FromResult(DaemonRunning
                    ? new VpnProcessResult(0, "{\"BackendState\":\"NeedsLogin\"}", "")
                    : new VpnProcessResult(1, "", "failed to connect to local tailscaled"));
            if (args.Contains("up")) return Task.FromResult(UpResult);
            return Task.FromResult(new VpnProcessResult(0, "", ""));
        }

        public bool StartDetached(string file, IReadOnlyList<string> arguments, ILogger logger, string? logName = null)
        {
            lock (Detached) Detached.Add((file, arguments.ToList()));
            return true;
        }

        public Call Up => Assert.Single(Calls, c => c.Args.Contains("up"));
    }

    internal sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
        }
    }

    internal static IOptions<VpnSettings> Settings(Action<VpnSettings>? configure = null)
    {
        var s = new VpnSettings
        {
            Provider = "tailscale",
            Hostname = "whiskers-test",
            Tailscale = { StateDir = "/var/lib/ts-test", Socket = "/var/run/tailscale/tailscaled.sock" },
        };
        configure?.Invoke(s);
        return Options.Create(s);
    }

    /// <summary>Asserts that <paramref name="text"/> holds neither <paramref name="key"/> nor any 8-character piece of it.</summary>
    internal static void AssertNoKey(string? text, string key)
    {
        if (string.IsNullOrEmpty(text)) return;
        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        for (var i = 0; i + 8 <= key.Length; i++)
            Assert.False(text.Contains(key.Substring(i, 8), StringComparison.Ordinal),
                $"A piece of the key ('{key.Substring(i, 8)}') leaked into: {text}");
    }

    // ---- In-app providers ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tailscale_passes_the_auth_key_as_a_file_and_never_in_argv()
    {
        var runner = new RecordingVpnRunner();
        var provider = new TailscaleVpnProvider(Settings(s => s.Tailscale.AuthKey = TailscaleKey),
            new ListLogger<TailscaleVpnProvider>(), runner, () => false, _dir);

        var result = await provider.EnsureUpAsync();

        Assert.True(result.Success);
        foreach (var call in runner.Calls)
            foreach (var arg in call.Args)
                AssertNoKey(arg, TailscaleKey);
        var up = runner.Up;
        Assert.Contains("--auth-key=file:" + up.KeyPath, up.Args);
        Assert.Equal(TailscaleKey, up.KeyContent);   // the CLI read the key from the file
        Assert.False(File.Exists(up.KeyPath));       // … which is gone once the call returned
        Assert.False(Directory.Exists(Path.GetDirectoryName(up.KeyPath)));
    }

    [Fact]
    public async Task Netbird_passes_the_setup_key_as_a_file_and_never_in_argv_or_the_environment()
    {
        var runner = new RecordingVpnRunner();
        var provider = new NetbirdVpnProvider(Settings(s =>
            {
                s.Provider = "netbird";
                s.Netbird.SetupKey = NetbirdKey;
            }),
            new ListLogger<NetbirdVpnProvider>(), runner, _dir);

        var result = await provider.EnsureUpAsync();

        Assert.True(result.Success);
        foreach (var arg in runner.Calls.SelectMany(c => c.Args)) AssertNoKey(arg, NetbirdKey);
        var up = runner.Up;
        Assert.Contains("--setup-key-file=" + up.KeyPath, up.Args);
        Assert.Equal(NetbirdKey, up.KeyContent);
        Assert.False(File.Exists(up.KeyPath));
    }

    [Fact]
    public void The_process_runner_takes_no_environment_so_no_key_can_ride_along_there()
    {
        // NB_SETUP_KEY / TS_AUTHKEY used to be set on the CLI's environment; the runner has no way to do that now.
        var parameters = typeof(IVpnProcessRunner).GetMethods().SelectMany(m => m.GetParameters()).ToList();
        Assert.DoesNotContain(parameters, p => p.ParameterType.IsAssignableTo(typeof(IReadOnlyDictionary<string, string>)));
        var psi = VpnProcessRunner.StartInfo("netbird", ["up"]);
        Assert.DoesNotContain("NB_SETUP_KEY", psi.Environment.Keys);
        Assert.DoesNotContain("TS_AUTHKEY", psi.Environment.Keys);
    }

    [SkippableFact]
    public async Task The_key_file_is_0600_in_a_0700_directory_while_the_cli_reads_it()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix file modes");
        var runner = new RecordingVpnRunner();
        var provider = new TailscaleVpnProvider(Settings(s => s.Tailscale.AuthKey = TailscaleKey),
            new ListLogger<TailscaleVpnProvider>(), runner, () => false, _dir);

        await provider.EnsureUpAsync();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, runner.Up.KeyMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, runner.Up.KeyDirMode);
    }

    [SkippableFact]
    public void A_secret_file_is_created_0600_and_removed_on_dispose()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix file modes");
        string path;
        using (var file = VpnSecretFile.Create(TailscaleKey, _dir))
        {
            path = file.Path;
            Assert.Equal(VpnSecretFile.FileMode, File.GetUnixFileMode(path));
            Assert.Equal(VpnSecretFile.DirectoryMode, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
            Assert.Equal(TailscaleKey, File.ReadAllText(path));
        }
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    /// <summary>Round 2 NIT: a setgid parent (the directory then inherits the bit) is no reason to refuse — only the
    /// permission bits say who can read the key.</summary>
    [SkippableFact]
    public void A_secret_file_can_be_created_under_a_setgid_parent()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix file modes");
        var parent = Path.Combine(_dir, "setgid");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
        Skip.IfNot(File.GetUnixFileMode(parent).HasFlag(UnixFileMode.SetGroup), "this file system does not keep setgid");

        using var file = VpnSecretFile.Create(TailscaleKey, parent);
        Assert.Equal(TailscaleKey, File.ReadAllText(file.Path));
        Assert.Equal(VpnSecretFile.FileMode, File.GetUnixFileMode(file.Path) & ~UnixFileMode.SetGroup);
    }

    [Fact]
    public void A_secret_file_holds_the_key_without_a_newline_and_is_removed_on_dispose()
    {
        var file = VpnSecretFile.Create(" " + NetbirdKey + "\n", _dir);
        Assert.Equal(NetbirdKey, File.ReadAllText(file.Path));
        file.Dispose();
        Assert.False(File.Exists(file.Path));
    }

    [Fact]
    public async Task A_failing_cli_that_echoes_the_key_leaks_it_into_neither_result_nor_log()
    {
        var runner = new RecordingVpnRunner
        {
            UpResult = new VpnProcessResult(1, "",
                $"backend error: invalid key \"{TailscaleKey}\" (prefix {TailscaleKey[..14]}, tail {TailscaleKey[^10..]})"),
        };
        var logger = new ListLogger<TailscaleVpnProvider>();
        var provider = new TailscaleVpnProvider(Settings(s => s.Tailscale.AuthKey = TailscaleKey), logger, runner,
            () => false, _dir);

        var result = await provider.EnsureUpAsync();

        Assert.False(result.Success);
        Assert.Equal(VpnBringUpState.Failed, result.State);
        Assert.Contains("exit code 1", result.Message);
        AssertNoKey(result.Message, TailscaleKey);
        Assert.NotEmpty(logger.Lines);
        foreach (var line in logger.Lines) AssertNoKey(line, TailscaleKey);
    }

    [Fact]
    public async Task A_failing_netbird_that_echoes_the_key_leaks_it_into_neither_result_nor_log()
    {
        var runner = new RecordingVpnRunner { UpResult = new VpnProcessResult(1, "", $"setup key {NetbirdKey} rejected") };
        var logger = new ListLogger<NetbirdVpnProvider>();
        var provider = new NetbirdVpnProvider(Settings(s => { s.Provider = "netbird"; s.Netbird.SetupKey = NetbirdKey; }),
            logger, runner, _dir);

        var result = await provider.EnsureUpAsync();

        Assert.False(result.Success);
        AssertNoKey(result.Message, NetbirdKey);
        foreach (var line in logger.Lines) AssertNoKey(line, NetbirdKey);
    }

    [Fact]
    public void Scrub_removes_the_key_and_every_piece_of_it()
    {
        var text = $"a {TailscaleKey} b {TailscaleKey[5..20]} c {TailscaleKey[^9..]}";
        var scrubbed = VpnArguments.Scrub(text, TailscaleKey);
        AssertNoKey(scrubbed, TailscaleKey);
        Assert.StartsWith("a ", scrubbed);
    }

    // ---- Legacy entrypoint -----------------------------------------------------------------------------------------

    private static string EntrypointPath => Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), "entrypoint.sh");

    [Fact]
    public void The_entrypoint_never_puts_the_auth_key_on_a_command_line()
    {
        var lines = File.ReadAllLines(EntrypointPath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        // Round 2 (M1): every key variable is copied into a shell variable and unset — under every prefix the
        // configuration reads — before the script starts its first process, so not even tailscaled inherits one.
        string[] names = ["TAILSCALE_AUTHKEY", "Vpn__Tailscale__AuthKey", "Vpn__Netbird__SetupKey", "NETBIRD_SETUP_KEY", "TS_AUTHKEY", "NB_SETUP_KEY"];
        string[] prefixes = ["", .. SecretEnvironment.ConfigurationPrefixes];
        var unsetLoop = lines.FindIndex(l => l == "for ws_prefix in \"\" " + string.Join(' ', SecretEnvironment.ConfigurationPrefixes) + "; do");
        Assert.True(unsetLoop >= 0, "the unset loop covers the configuration prefixes");
        var unsetLine = lines[unsetLoop + 1] + " " + lines[unsetLoop + 2];
        Assert.StartsWith("unset ", unsetLine);
        foreach (var name in names) Assert.Contains("\"${ws_prefix}" + name + "\"", unsetLine);
        int First(Predicate<string> p) { var i = lines.FindIndex(p); Assert.True(i >= 0); return i; }
        var firstProcess = new[]
        {
            First(l => l == "remove_stale_keys \"$WHISKERS_SECRET_DIR\""),
            First(l => l.StartsWith("ws_provider=$(", StringComparison.Ordinal)),
            First(l => l.StartsWith("tailscaled --state", StringComparison.Ordinal)),
        }.Min();
        Assert.True(unsetLoop < firstProcess, "the keys are unset before the first process is started");

        // Every line that reads a key variable ("$NAME" / "${NAME", under any prefix) is a copy into a shell variable
        // above the unset; the copies are read only by a test, the builtin printf into a private file or key_to_file.
        var reads = lines.Select((l, i) => (l, i)).Where(x => names.Any(n => prefixes.Any(p =>
            System.Text.RegularExpressions.Regex.IsMatch(x.l, @"\$\{?" + p + n + "(?![A-Za-z0-9_])")))).ToList();
        Assert.NotEmpty(reads);
        foreach (var (line, index) in reads)
        {
            Assert.True(index < unsetLoop, $"A key variable is read after it was unset: {line}");
            Assert.Matches(@"^ws_\w+=\$\{", line);
        }
        string[] copies = ["ws_ts_legacy_key", "ws_ts_key", "ws_nb_key"];
        foreach (var line in lines.Where(l => copies.Any(c =>
                     System.Text.RegularExpressions.Regex.IsMatch(l, @"\$\{?" + c + "(?![A-Za-z0-9_])"))))
            Assert.True(line.StartsWith("if [ -n \"$ws_", StringComparison.Ordinal)
                        || line.StartsWith("printf '%s' \"$ws_ts_legacy_key\" > ", StringComparison.Ordinal)
                        || System.Text.RegularExpressions.Regex.IsMatch(line,
                            @"^Vpn__\w+File=\$\(key_to_file ""\$ws_\w+_key""\)$"),
                $"A key's value is used outside a test, the printf builtin or key_to_file: {line}");
        Assert.DoesNotContain(lines, l => l.StartsWith("export ws_", StringComparison.Ordinal));
        // Round 3 (m1): keys, key files and the provider are resolved in the order the configuration lets the sources
        // win — bare, then SecretEnvironment.ConfigurationPrefixes in order (verified against the real builder in
        // SecretEnvironmentTests).
        static string Chain(string name) =>
            "${" + name + ":-" + string.Concat(SecretEnvironment.ConfigurationPrefixes.Select(p => "${" + p + name + ":-"))
            + new string('}', SecretEnvironment.ConfigurationPrefixes.Count + 1);
        Assert.Contains("ws_ts_key=" + Chain("Vpn__Tailscale__AuthKey"), lines);
        Assert.Contains("ws_nb_key=" + Chain("Vpn__Netbird__SetupKey"), lines);
        Assert.Contains("ws_ts_keyfile=" + Chain("Vpn__Tailscale__AuthKeyFile"), lines);
        Assert.Contains("ws_nb_keyfile=" + Chain("Vpn__Netbird__SetupKeyFile"), lines);
        Assert.Contains("ws_cfg_provider=" + Chain("Vpn__Provider"), lines);
        Assert.Contains("ws_provider=$(printf '%s' \"$ws_cfg_provider\" | tr '[:upper:]' '[:lower:]')", lines);
        // Round 3 NIT n1: an inherited ws_* variable keeps its export flag on assignment — they are unset first.
        var unsetWs = lines.FindIndex(l => l.StartsWith("unset ws_ts_legacy_key ws_ts_key ws_nb_key ", StringComparison.Ordinal));
        Assert.True(unsetWs >= 0 && unsetWs < lines.FindIndex(l => l.StartsWith("ws_ts_legacy_key=", StringComparison.Ordinal)),
            "the shell copies are unset before they are assigned");
        // Round 3 (m2): VPN_PROVIDER alone becomes Whiskers' provider; the legacy bring-up is skipped when VPN_PROVIDER
        // is set or Whiskers' provider is tailscale/netbird — before tailscaled could start.
        Assert.Contains("if [ -z \"$ws_cfg_provider\" ] && [ -n \"${VPN_PROVIDER:-}\" ]; then", lines);
        Assert.Contains("Vpn__Provider=$VPN_PROVIDER", lines);
        Assert.Contains("tailscale|netbird) ws_in_app=1 ;;", lines);
        var skip = lines.FindIndex(l => l == "if [ -n \"${VPN_PROVIDER:-}\" ] || [ -n \"$ws_in_app\" ]; then");
        Assert.True(skip >= 0 && lines[skip + 2] == "start_app \"$@\"", "the legacy skip starts the app");
        Assert.True(skip < lines.FindIndex(l => l.StartsWith("tailscaled --state", StringComparison.Ordinal)));
        Assert.Contains("printf '%s' \"$1\" > \"$kf_dir/key\"", lines);   // key_to_file itself
        // The CLI gets a file, and nothing else hands a key over.
        Assert.Contains(lines, l => l.StartsWith("tailscale up --auth-key=\"file:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("--authkey", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("--setup-key=", StringComparison.Ordinal));
        // Every way into the application drops the traps (B1).
        Assert.Equal(1, lines.Count(l => l.StartsWith("exec ", StringComparison.Ordinal)));
        Assert.Contains("exec dotnet Whiskers.dll \"$@\"", lines);
        Assert.Equal(3, lines.Count(l => l == "start_app \"$@\""));
        // The legacy file is private: umask 077 before it is written, removed after the call, also on an early exit and
        // on a stop signal, which is passed on (m4).
        var umask = lines.FindIndex(l => l == "umask 077");
        var write = lines.FindIndex(l => l.StartsWith("printf '%s' \"$ws_ts_legacy_key\"", StringComparison.Ordinal));
        var remove = lines.FindIndex(Math.Max(write, 0), l => l == "rm -rf \"$ts_key_dir\"");
        Assert.True(umask >= 0 && umask < write && write < remove, "umask 077 → printf → rm -rf in this order");
        Assert.Contains("trap 'rm -rf \"$ts_key_dir\"' EXIT", lines);
        Assert.Contains("trap 'rm -rf \"$ts_key_dir\"; exit 143' TERM", lines);
        Assert.Contains("trap 'rm -rf \"$ts_key_dir\"; exit 130' INT", lines);
        Assert.Contains("trap 'rm -rf \"$ts_key_dir\"; exit 129' HUP", lines);
        var traps = lines.FindIndex(l => l.StartsWith("trap 'rm -rf", StringComparison.Ordinal));
        Assert.True(traps < write, "the cleanup traps are set before the key is written");
    }

    [Fact]
    public void The_entrypoint_is_posix_sh_with_lf_line_endings()
    {
        var text = File.ReadAllText(EntrypointPath);
        Assert.StartsWith("#!/bin/sh\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.DoesNotContain("[[", text);      // bash-only test
        Assert.DoesNotContain("function ", text);
    }

    /// <summary>The real entrypoint under <c>sh</c>, with <paramref name="bin"/> first on the PATH, its key files in
    /// <paramref name="secrets"/> and none of the test process's own key variables inherited.</summary>
    private ProcessStartInfo Entrypoint(string bin, string secrets, IReadOnlyDictionary<string, string> env)
    {
        var psi = SecretEnvironment.Scrub(new ProcessStartInfo("sh")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        });
        psi.ArgumentList.Add(EntrypointPath);
        psi.Environment.Remove("VPN_PROVIDER");
        psi.Environment["PATH"] = bin + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin");
        psi.Environment["WHISKERS_DATA_DIR"] = Path.Combine(_dir, "data-" + Path.GetFileName(bin));
        psi.Environment["WHISKERS_SECRET_DIR"] = secrets;
        psi.Environment["TMPDIR"] = secrets;
        psi.Environment["VPN_FORCE_LEGACY_TAILSCALE"] = "1";      // also on a runner that has tailscale0
        foreach (var (key, value) in env) psi.Environment[key] = value;
        return psi;
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(ProcessStartInfo psi)
    {
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return (proc.ExitCode, await stdout, await stderr);
    }

    private (string Bin, string Secrets, string Record) EntrypointDirs(string name)
    {
        var bin = Path.Combine(_dir, "bin-" + name);
        var secrets = Path.Combine(_dir, "secrets-" + name);
        var record = Path.Combine(_dir, "record-" + name);
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(secrets);
        Directory.CreateDirectory(record);
        // The fake daemon records the environment the real one would keep for the container's lifetime (M1).
        WriteScript(Path.Combine(bin, "tailscaled"), $"env > '{record}/tailscaled-env'\nexit 0");
        // The fake application records its environment and, when it got a key file, that file's mode and content.
        WriteScript(Path.Combine(bin, "dotnet"), $$"""
            env > '{{record}}/env'
            for f in "${Vpn__Tailscale__AuthKeyFile:-}" "${Vpn__Netbird__SetupKeyFile:-}"; do
                if [ -n "$f" ]; then
                    stat -c '%a' "$f" >> '{{record}}/appmode'
                    stat -c '%a' "$(dirname "$f")" >> '{{record}}/appdirmode'
                    cat "$f" >> '{{record}}/appcontent'
                fi
            done
            exit 0
            """);
        return (bin, secrets, record);
    }

    /// <summary>Runs the real entrypoint with fake <c>tailscaled</c>, <c>tailscale</c> and <c>dotnet</c> on the PATH: the
    /// fake <c>tailscale</c> records its argv and the key file's mode and content while it runs; the fake <c>dotnet</c>
    /// records the environment the application would get.</summary>
    [SkippableFact]
    public async Task The_entrypoint_hands_the_key_over_in_a_private_file_and_drops_it_before_the_app_starts()
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh; Windows has no Unix file modes");
        var (bin, secrets, record) = EntrypointDirs("legacy");
        WriteScript(Path.Combine(bin, "tailscale"), $$"""
            if [ "$1" = "up" ]; then
                env > '{{record}}/tailscale-env'
                for a in "$@"; do printf '%s\n' "$a" >> '{{record}}/args'; done
                for a in "$@"; do
                    case "$a" in
                        --auth-key=file:*)
                            f="${a#--auth-key=file:}"
                            stat -c '%a' "$f" > '{{record}}/mode'
                            stat -c '%a' "$(dirname "$f")" > '{{record}}/dirmode'
                            cat "$f" > '{{record}}/content'
                            printf '%s' "$f" > '{{record}}/path'
                            ;;
                    esac
                done
            fi
            exit 0
            """);

        var (code, stdout, stderr) = await RunAsync(Entrypoint(bin, secrets, new Dictionary<string, string>
        {
            ["TAILSCALE_AUTHKEY"] = TailscaleKey,
            ["TAILSCALE_HOSTNAME"] = "edge node",                 // a space stays inside one argv element
            ["Vpn__Tailscale__AuthKey"] = TailscaleKey,           // what compose sets in every mode
            ["TS_AUTHKEY"] = TailscaleKey,
            ["ASPNETCORE_Vpn__Tailscale__AuthKey"] = TailscaleKey,
            ["DOTNET_TAILSCALE_AUTHKEY"] = TailscaleKey,
        }));
        Assert.True(code == 0, $"entrypoint failed: {stderr}");

        // M1: neither the long-lived daemon nor the CLI inherits a key under any name.
        AssertNoKey(File.ReadAllText(Path.Combine(record, "tailscaled-env")), TailscaleKey);
        AssertNoKey(File.ReadAllText(Path.Combine(record, "tailscale-env")), TailscaleKey);
        var args = File.ReadAllLines(Path.Combine(record, "args"));
        var keyPath = File.ReadAllText(Path.Combine(record, "path"));
        Assert.Equal(["up", "--auth-key=file:" + keyPath, "--accept-routes", "--hostname=edge node"], args);
        foreach (var arg in args) AssertNoKey(arg, TailscaleKey);
        Assert.Equal("600", File.ReadAllText(Path.Combine(record, "mode")).Trim());
        Assert.Equal("700", File.ReadAllText(Path.Combine(record, "dirmode")).Trim());
        Assert.Equal(TailscaleKey, File.ReadAllText(Path.Combine(record, "content")));
        Assert.StartsWith(secrets, keyPath);
        Assert.False(File.Exists(keyPath));
        Assert.Empty(Directory.GetFileSystemEntries(secrets));                   // the key directory is gone
        // The application's environment holds no key under any name — and no key file either: it is not the in-app provider.
        var appEnv = File.ReadAllText(Path.Combine(record, "env"));
        AssertNoKey(appEnv, TailscaleKey);
        Assert.DoesNotContain("AuthKeyFile=", appEnv);
        AssertNoKey(stdout, TailscaleKey);
        AssertNoKey(stderr, TailscaleKey);
    }

    [SkippableFact]
    public async Task The_entrypoint_moves_the_compose_key_into_a_private_file_for_the_in_app_provider()
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh; Windows has no Unix file modes");
        var (bin, secrets, record) = EntrypointDirs("inapp");
        WriteScript(Path.Combine(bin, "tailscale"), "exit 0");

        var (code, stdout, stderr) = await RunAsync(Entrypoint(bin, secrets, new Dictionary<string, string>
        {
            ["VPN_PROVIDER"] = "Tailscale",
            ["Vpn__Tailscale__AuthKey"] = TailscaleKey,
            ["Vpn__Netbird__SetupKey"] = NetbirdKey,              // not the active provider: dropped, no file
            ["TAILSCALE_AUTHKEY"] = TailscaleKey,
        }));
        Assert.True(code == 0, $"entrypoint failed: {stderr}");

        var appEnv = File.ReadAllText(Path.Combine(record, "env"));
        AssertNoKey(appEnv, TailscaleKey);
        AssertNoKey(appEnv, NetbirdKey);
        Assert.Contains("Vpn__Tailscale__AuthKeyFile=" + secrets, appEnv);
        Assert.DoesNotContain("Vpn__Netbird__SetupKeyFile=", appEnv);
        Assert.Equal("600", File.ReadAllText(Path.Combine(record, "appmode")).Trim());
        Assert.Equal("700", File.ReadAllText(Path.Combine(record, "appdirmode")).Trim());
        Assert.Equal(TailscaleKey, File.ReadAllText(Path.Combine(record, "appcontent")));
        AssertNoKey(stdout + stderr, TailscaleKey);
    }

    /// <summary>Rounds 2 and 3 (m1): the provider and the keys are resolved as Whiskers resolves them — the bare name,
    /// then <c>DOTNET_</c>, then <c>ASPNETCORE_</c> — and a provider set only in the configuration (no VPN_PROVIDER, a
    /// k8s / docker run shape) keeps the legacy bring-up from running as well (m2).</summary>
    [SkippableFact]
    public async Task The_entrypoint_follows_the_configured_provider_when_VPN_PROVIDER_is_not_set()
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh; Windows has no Unix file modes");
        var (bin, secrets, record) = EntrypointDirs("config-provider");
        WriteScript(Path.Combine(bin, "tailscale"), "exit 0");

        var (code, stdout, stderr) = await RunAsync(Entrypoint(bin, secrets, new Dictionary<string, string>
        {
            ["DOTNET_Vpn__Provider"] = "netbird",                    // wins over ASPNETCORE_, as in Whiskers
            ["ASPNETCORE_Vpn__Provider"] = "tailscale",
            ["ASPNETCORE_Vpn__Netbird__SetupKey"] = NetbirdKey,
            ["Vpn__Tailscale__AuthKey"] = TailscaleKey,              // no provider reads it: dropped, and said so
            ["TAILSCALE_AUTHKEY"] = TailscaleKey,                    // would drive the legacy tailscale up — must not run
        }));
        Assert.True(code == 0, $"entrypoint failed: {stderr}");

        var appEnv = File.ReadAllText(Path.Combine(record, "env"));
        AssertNoKey(appEnv, TailscaleKey);
        AssertNoKey(appEnv, NetbirdKey);
        Assert.Contains("Vpn__Netbird__SetupKeyFile=" + secrets, appEnv);
        Assert.DoesNotContain("Vpn__Tailscale__AuthKeyFile=", appEnv);
        Assert.Equal("600", File.ReadAllText(Path.Combine(record, "appmode")).Trim());
        Assert.Equal(NetbirdKey, File.ReadAllText(Path.Combine(record, "appcontent")));
        Assert.Contains("a Tailscale auth key (Vpn__Tailscale__AuthKey, possibly prefixed) is set but the VPN provider is 'netbird'", stderr);
        Assert.False(File.Exists(Path.Combine(record, "tailscaled-env")), "the legacy bring-up must not run next to the in-app provider");
        Assert.False(File.Exists(Path.Combine(record, "tailscale-env")), "no ungoverned tailscale up");
        AssertNoKey(stdout + stderr, TailscaleKey);
        AssertNoKey(stdout + stderr, NetbirdKey);
    }

    /// <summary>Round 3 (m2): who brings the VPN up, for every combination of <c>VPN_PROVIDER</c> and
    /// <c>Vpn__Provider</c> ("" = unset). Never both; nobody only where a provider explicitly says <c>none</c> while
    /// VPN_PROVIDER is set (the VPN runs on the host). The compose shape (Vpn__Provider=${VPN_PROVIDER:-none}) is the
    /// diagonal plus ("", "none"), which stays on the legacy path.</summary>
    [SkippableTheory]
    [InlineData("", "", true, false)]
    [InlineData("", "none", true, false)]                // compose with VPN_PROVIDER unset
    [InlineData("", "tailscale", false, true)]
    [InlineData("none", "", false, false)]
    [InlineData("none", "none", false, false)]
    [InlineData("none", "tailscale", false, true)]
    [InlineData("tailscale", "", false, true)]           // VPN_PROVIDER alone is handed to Whiskers as Vpn__Provider
    [InlineData("tailscale", "none", false, false)]      // conflict: Whiskers says none, warned on the console
    [InlineData("tailscale", "tailscale", false, true)]
    [InlineData("", "Netbird", false, true)]
    public async Task Exactly_one_side_brings_the_vpn_up(string vpnProvider, string configProvider, bool legacy, bool inApp)
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh");
        var (bin, secrets, record) = EntrypointDirs($"matrix-{vpnProvider}-{configProvider}");
        WriteScript(Path.Combine(bin, "tailscale"), "exit 0");
        var env = new Dictionary<string, string>();
        if (vpnProvider.Length > 0) env["VPN_PROVIDER"] = vpnProvider;
        if (configProvider.Length > 0) env["Vpn__Provider"] = configProvider;

        var psi = Entrypoint(bin, secrets, env);
        psi.Environment.Remove("Vpn__Provider");                  // only what the row sets
        if (configProvider.Length > 0) psi.Environment["Vpn__Provider"] = configProvider;
        var (code, _, stderr) = await RunAsync(psi);
        Assert.True(code == 0, $"entrypoint failed: {stderr}");

        var legacyRan = File.Exists(Path.Combine(record, "tailscaled-env"));
        var appProvider = File.ReadAllLines(Path.Combine(record, "env"))
            .FirstOrDefault(l => l.StartsWith("Vpn__Provider=", StringComparison.Ordinal))?["Vpn__Provider=".Length..] ?? "";
        var inAppRuns = appProvider.ToLowerInvariant() is "tailscale" or "netbird";
        Assert.Equal(legacy, legacyRan);
        Assert.Equal(inApp, inAppRuns);
        Assert.False(legacyRan && inAppRuns, "both sides brought the VPN up");
        if (vpnProvider.Length > 0 && configProvider.Length > 0 && !vpnProvider.Equals(configProvider, StringComparison.OrdinalIgnoreCase))
            Assert.Contains("differs from Vpn__Provider", stderr);
    }

    [SkippableFact]
    public async Task The_entrypoint_drops_every_key_variable_on_the_provider_none_path_and_removes_stale_key_dirs()
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh");
        var (bin, secrets, record) = EntrypointDirs("none");
        WriteScript(Path.Combine(bin, "tailscale"), "exit 0");
        // What a crash or a kill left behind: the in-app CLI file, the legacy file, an earlier run's app key file.
        foreach (var stale in new[] { "whiskers-vpn-0123", "whiskers-ts.abcdef", "whiskers-vpnkey.abcdef" })
        {
            Directory.CreateDirectory(Path.Combine(secrets, stale));
            File.WriteAllText(Path.Combine(secrets, stale, "key"), TailscaleKey);
        }
        Directory.CreateDirectory(Path.Combine(secrets, "unrelated"));

        var (code, _, stderr) = await RunAsync(Entrypoint(bin, secrets, new Dictionary<string, string>
        {
            ["VPN_PROVIDER"] = "none",
            ["Vpn__Tailscale__AuthKey"] = TailscaleKey,
            ["Vpn__Netbird__SetupKey"] = NetbirdKey,
            ["TAILSCALE_AUTHKEY"] = TailscaleKey,
            ["NETBIRD_SETUP_KEY"] = NetbirdKey,
            ["TS_AUTHKEY"] = TailscaleKey,
            ["NB_SETUP_KEY"] = NetbirdKey,
        }));
        Assert.True(code == 0, $"entrypoint failed: {stderr}");

        var appEnv = File.ReadAllText(Path.Combine(record, "env"));
        AssertNoKey(appEnv, TailscaleKey);
        AssertNoKey(appEnv, NetbirdKey);
        Assert.Equal(["unrelated"], Directory.GetDirectories(secrets).Select(Path.GetFileName));
        Assert.Contains("(Vpn__Tailscale__AuthKey, possibly prefixed) is set but the VPN provider is 'none'", stderr);
    }

    [SkippableFact]
    public async Task The_entrypoint_reports_a_failed_tailscale_up_instead_of_swallowing_it()
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh");
        var (bin, secrets, _) = EntrypointDirs("fail");
        WriteScript(Path.Combine(bin, "tailscale"), "[ \"$1\" = up ] && exit 3\nexit 0");

        var (_, _, stderr) = await RunAsync(Entrypoint(bin, secrets,
            new Dictionary<string, string> { ["TAILSCALE_AUTHKEY"] = TailscaleKey }));

        Assert.Contains("tailscale up failed with exit code 3", stderr);
        Assert.Empty(Directory.GetFileSystemEntries(secrets));   // the key is removed on failure too
        AssertNoKey(stderr, TailscaleKey);
    }

    [SkippableFact]
    public async Task A_stop_during_tailscale_up_removes_the_key_and_is_passed_on_not_swallowed()
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh; POSIX signals");
        var (bin, secrets, record) = EntrypointDirs("stop");
        WriteScript(Path.Combine(bin, "tailscale"), $$"""
            if [ "$1" = "up" ]; then touch '{{record}}/up-started'; sleep 3; fi
            exit 0
            """);

        using var proc = Process.Start(Entrypoint(bin, secrets,
            new Dictionary<string, string> { ["TAILSCALE_AUTHKEY"] = TailscaleKey }))!;
        var started = Path.Combine(record, "up-started");
        for (var i = 0; i < 200 && !File.Exists(started); i++) await Task.Delay(50);
        Assert.True(File.Exists(started), "tailscale up never started");
        using (var kill = Process.Start(new ProcessStartInfo("kill", ["-TERM", proc.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]))!)
            await kill.WaitForExitAsync();
        await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        Assert.Equal(143, proc.ExitCode);                                   // the stop is passed on
        Assert.False(File.Exists(Path.Combine(record, "env")));             // Whiskers was not started after it
        Assert.Empty(Directory.GetFileSystemEntries(secrets));              // and the key is gone
    }
    private static void WriteScript(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

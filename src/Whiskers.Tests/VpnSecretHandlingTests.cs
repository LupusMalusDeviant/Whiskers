using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whiskers.Services.Vpn;
using Whiskers.Services.Vpn.Providers;

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

        // Every line that names the key: the test, the builtin printf into the private file, and the unset.
        var withKey = lines.Where(l => l.Contains("TAILSCALE_AUTHKEY", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, withKey.Count);
        Assert.Contains(withKey, l => l.StartsWith("if [ -n \"${TAILSCALE_AUTHKEY:-}\" ]", StringComparison.Ordinal));
        Assert.Contains(withKey, l => l.StartsWith("printf '%s' \"$TAILSCALE_AUTHKEY\" > ", StringComparison.Ordinal));
        Assert.Contains("unset TAILSCALE_AUTHKEY", withKey);
        // The CLI gets the file, and nothing else hands a key over.
        Assert.Contains(lines, l => l.StartsWith("tailscale up --auth-key=\"file:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("--authkey", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("TS_AUTHKEY", StringComparison.Ordinal));
        // The file is private: umask 077 before it is written, its directory removed after the call.
        var umask = lines.FindIndex(l => l == "umask 077");
        var write = lines.FindIndex(l => l.StartsWith("printf '%s' \"$TAILSCALE_AUTHKEY\"", StringComparison.Ordinal));
        var remove = lines.FindIndex(write, l => l == "rm -rf \"$ts_key_dir\"");
        Assert.True(umask >= 0 && umask < write && write < remove, "umask 077 → printf → rm -rf in this order");
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

    /// <summary>Runs the real entrypoint with fake <c>tailscaled</c>, <c>tailscale</c> and <c>dotnet</c> on the PATH: the
    /// fake <c>tailscale</c> records its argv and the key file's mode and content while it runs; the fake <c>dotnet</c>
    /// records the environment the application would get.</summary>
    [SkippableFact]
    public async Task The_entrypoint_hands_the_key_over_in_a_private_file_and_drops_it_before_the_app_starts()
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh; Windows has no Unix file modes");
        var bin = Path.Combine(_dir, "bin");
        var data = Path.Combine(_dir, "data");
        var tmp = Path.Combine(_dir, "tmp");
        var record = Path.Combine(_dir, "record");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(record);

        WriteScript(Path.Combine(bin, "tailscaled"), "exit 0");
        WriteScript(Path.Combine(bin, "tailscale"), $$"""
            if [ "$1" = "up" ]; then
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
        WriteScript(Path.Combine(bin, "dotnet"), $"env > '{record}/env'\nexit 0");

        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(EntrypointPath);
        psi.Environment.Remove("VPN_PROVIDER");
        psi.Environment["PATH"] = bin + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin");
        psi.Environment["WHISKERS_DATA_DIR"] = data;
        psi.Environment["TMPDIR"] = tmp;
        psi.Environment["TAILSCALE_AUTHKEY"] = TailscaleKey;
        psi.Environment["TAILSCALE_HOSTNAME"] = "edge node";      // a space stays inside one argv element
        psi.Environment["VPN_FORCE_LEGACY_TAILSCALE"] = "1";      // also on a runner that has tailscale0

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        Assert.True(proc.ExitCode == 0, $"entrypoint failed: {await stderr}");

        var args = File.ReadAllLines(Path.Combine(record, "args"));
        var keyPath = File.ReadAllText(Path.Combine(record, "path"));
        Assert.Equal(["up", "--auth-key=file:" + keyPath, "--accept-routes", "--hostname=edge node"], args);
        foreach (var arg in args) AssertNoKey(arg, TailscaleKey);
        Assert.Equal("600", File.ReadAllText(Path.Combine(record, "mode")).Trim());
        Assert.Equal("700", File.ReadAllText(Path.Combine(record, "dirmode")).Trim());
        Assert.Equal(TailscaleKey, File.ReadAllText(Path.Combine(record, "content")));
        Assert.StartsWith(tmp, keyPath);
        Assert.False(File.Exists(keyPath));
        Assert.Empty(Directory.GetFileSystemEntries(tmp));                       // the key directory is gone
        Assert.DoesNotContain("TAILSCALE_AUTHKEY=", File.ReadAllText(Path.Combine(record, "env")));   // app env
        AssertNoKey(await stdout, TailscaleKey);
        AssertNoKey(await stderr, TailscaleKey);
    }

    [SkippableFact]
    public async Task The_entrypoint_reports_a_failed_tailscale_up_instead_of_swallowing_it()
    {
        Skip.If(OperatingSystem.IsWindows(), "the entrypoint is POSIX sh");
        var bin = Path.Combine(_dir, "bin2");
        var tmp = Path.Combine(_dir, "tmp2");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(tmp);
        WriteScript(Path.Combine(bin, "tailscaled"), "exit 0");
        WriteScript(Path.Combine(bin, "tailscale"), "[ \"$1\" = up ] && exit 3\nexit 0");
        WriteScript(Path.Combine(bin, "dotnet"), "exit 0");

        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(EntrypointPath);
        psi.Environment.Remove("VPN_PROVIDER");
        psi.Environment["PATH"] = bin + ":" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin");
        psi.Environment["WHISKERS_DATA_DIR"] = Path.Combine(_dir, "data2");
        psi.Environment["TMPDIR"] = tmp;
        psi.Environment["TAILSCALE_AUTHKEY"] = TailscaleKey;
        psi.Environment["VPN_FORCE_LEGACY_TAILSCALE"] = "1";

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        await stdout;

        Assert.Contains("tailscale up failed with exit code 3", stderr);
        Assert.Empty(Directory.GetFileSystemEntries(tmp));   // the key is removed on failure too
        AssertNoKey(stderr, TailscaleKey);
    }

    private static void WriteScript(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Options;
using Whiskers.Services.Vpn;
using Whiskers.Services.Vpn.Providers;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>
/// R1-43 review B1: docker-compose hands the VPN keys to Whiskers as <c>Vpn__Tailscale__AuthKey</c> /
/// <c>Vpn__Netbird__SetupKey</c>, in every mode. No child process may inherit them — not tailscaled (which runs as long as
/// Whiskers), not ssh, docker, nsenter or a shell: under <c>pid: host</c> its <c>/proc/&lt;pid&gt;/environ</c> is readable
/// on the host. Whiskers drops them from its own environment once the configuration has read them, and every process
/// start removes them from the child's environment. Keys can come from files instead (<c>…AuthKeyFile</c>).
/// </summary>
[Collection(nameof(SecretEnvironmentTests))]   // sets process-wide environment variables
public class SecretEnvironmentTests : IDisposable
{
    private const string Key = VpnSecretHandlingTests.TailscaleKey;
    private static readonly string[] Names =
        ["Vpn__Tailscale__AuthKey", "VPN__NETBIRD__SETUPKEY", "TAILSCALE_AUTHKEY", "NETBIRD_SETUP_KEY", "TS_AUTHKEY", "NB_SETUP_KEY",
         "ASPNETCORE_Vpn__Tailscale__AuthKey", "dotnet_NB_SETUP_KEY"];   // round 2 (m2): behind a configuration prefix

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "secret-env-" + Guid.NewGuid().ToString("N"));

    public SecretEnvironmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var name in Names) Environment.SetEnvironmentVariable(name, null);
        Environment.SetEnvironmentVariable("WHISKERS_TEST_HARMLESS", null);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void SetAll()
    {
        foreach (var name in Names) Environment.SetEnvironmentVariable(name, Key);
        Environment.SetEnvironmentVariable("WHISKERS_TEST_HARMLESS", "stays");
    }

    [Theory]
    [InlineData("Vpn__Tailscale__AuthKey", true)]
    [InlineData("vpn__tailscale__authkey", true)]
    [InlineData("Vpn:Netbird:SetupKey", true)]
    [InlineData("TS_AUTHKEY", true)]
    [InlineData("nb_setup_key", true)]
    [InlineData("ASPNETCORE_Vpn__Tailscale__AuthKey", true)]   // round 2 (m2): the configuration reads these too
    [InlineData("aspnetcore_vpn:netbird:setupkey", true)]
    [InlineData("DOTNET_Vpn__Netbird__SetupKey", true)]
    [InlineData("DOTNET_TS_AUTHKEY", true)]
    [InlineData("ASPNETCORE_", false)]
    [InlineData("ASPNETCORE_Vpn__Tailscale__AuthKeyFile", false)]
    [InlineData("OTHER_Vpn__Tailscale__AuthKey", false)]       // not a prefix the configuration reads
    [InlineData("Vpn__Tailscale__AuthKeyFile", false)]   // a path, not the key
    [InlineData("Vpn__Tailscale__LoginServer", false)]
    [InlineData("PATH", false)]
    public void The_secret_names_are_recognised_without_case_and_separator(string name, bool secret) =>
        Assert.Equal(secret, SecretEnvironment.IsSecret(name));

    [Fact]
    public void The_vpn_runner_hands_no_key_variable_to_its_child()
    {
        SetAll();

        var psi = VpnProcessRunner.StartInfo("tailscaled", ["--state=/x"]);

        Assert.DoesNotContain(psi.Environment.Keys, SecretEnvironment.IsSecret);
        Assert.DoesNotContain(psi.Environment.Values, v => v?.Contains(Key, StringComparison.Ordinal) == true);
        Assert.Equal("stays", psi.Environment["WHISKERS_TEST_HARMLESS"]);
    }

    [Fact]
    public async Task A_real_child_of_the_vpn_runner_sees_no_key_in_its_environment()
    {
        SetAll();

        var result = OperatingSystem.IsWindows()
            ? await VpnProcessRunner.Instance.RunAsync("cmd", ["/c", "set"], CancellationToken.None)
            : await VpnProcessRunner.Instance.RunAsync("env", [], CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("WHISKERS_TEST_HARMLESS=stays", result.StdOut, StringComparison.OrdinalIgnoreCase);
        VpnSecretHandlingTests.AssertNoKey(result.StdOut, Key);
    }

    [Fact]
    public void Whiskers_drops_the_key_variables_from_its_own_environment()
    {
        SetAll();

        SecretEnvironment.RemoveFromProcess();

        foreach (var name in Names) Assert.Null(Environment.GetEnvironmentVariable(name));
        Assert.Equal("stays", Environment.GetEnvironmentVariable("WHISKERS_TEST_HARMLESS"));
    }

    [Fact]
    public void Program_drops_them_right_after_the_configuration_has_read_the_environment()
    {
        var program = File.ReadAllLines(Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), "src", "Whiskers", "Program.cs"))
            .Select(l => l.Trim()).ToList();
        var configuration = program.FindIndex(l => l.StartsWith("builder.AddWhiskersConfiguration(", StringComparison.Ordinal));
        var removal = program.FindIndex(l => l.StartsWith("Whiskers.Utils.SecretEnvironment.RemoveFromProcess();", StringComparison.Ordinal));
        var build = program.FindIndex(l => l.StartsWith("var app = builder.Build();", StringComparison.Ordinal));
        Assert.True(configuration >= 0 && configuration < removal && removal < build,
            "RemoveFromProcess runs after the configuration is loaded and before the host is built");
    }

    /// <summary>Every place in Whiskers that builds a <see cref="ProcessStartInfo"/> scrubs it: a new process start
    /// that forgets it fails here.</summary>
    [Fact]
    public void Every_process_start_in_whiskers_scrubs_the_child_environment()
    {
        var root = Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), "src", "Whiskers");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .Where(f => f.Text.Contains("new ProcessStartInfo", StringComparison.Ordinal))
            .ToList();

        Assert.True(files.Count >= 6, "the scan found the known process starts");
        foreach (var (file, text) in files)
        {
            var starts = CountOf(text, "new ProcessStartInfo");
            var scrubs = CountOf(text, "SecretEnvironment.Scrub(");
            Assert.True(scrubs >= starts,
                $"{Path.GetRelativePath(root, file)} builds {starts} ProcessStartInfo but scrubs only {scrubs}.");
        }

        // Round 2 NIT: a Process.Start(string, …) overload builds its start info where nothing can scrub it — every
        // call hands over a (scrubbed) ProcessStartInfo variable instead.
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var calls = CountOf(text, "Process.Start(");
            var withStartInfo = System.Text.RegularExpressions.Regex.Matches(text, @"Process\.Start\(\s*[A-Za-z_]\w*\s*\)").Count;
            Assert.True(calls == withStartInfo,
                $"{Path.GetRelativePath(root, file)} calls Process.Start with something other than a ProcessStartInfo variable.");
        }
    }

    /// <summary>Round 2 (m2): the configuration reads the environment unprefixed and behind prefixes (the prefix is
    /// dropped: <c>ASPNETCORE_Vpn__Tailscale__AuthKey</c> is <c>Vpn:Tailscale:AuthKey</c>). The prefixes are taken from
    /// the builder Program.cs uses, so a prefix added there that <see cref="SecretEnvironment"/> misses fails here.</summary>
    [Fact]
    public void Every_environment_prefix_the_configuration_reads_is_a_secret_prefix_too()
    {
        SetAll();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        var prefixes = ((Microsoft.Extensions.Configuration.IConfigurationBuilder)builder.Configuration).Sources
            .OfType<Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource>()
            .Select(s => s.Prefix ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Contains("", prefixes);
        Assert.Contains(prefixes, p => p.Length > 0);
        foreach (var prefix in prefixes.Where(p => p.Length > 0))
        {
            Assert.Contains(prefix, SecretEnvironment.ConfigurationPrefixes, StringComparer.OrdinalIgnoreCase);
            Assert.True(SecretEnvironment.IsSecret(prefix + "Vpn__Tailscale__AuthKey"), prefix);
            Assert.True(SecretEnvironment.IsSecret(prefix.ToLowerInvariant() + "TS_AUTHKEY"), prefix);
        }
        // The prefixed name really is the configuration key (what makes it a secret).
        Assert.Equal(Key, builder.Configuration["Vpn:Tailscale:AuthKey"]);
        // And Whiskers adds no environment source of its own beyond the builder's.
        var root = Path.Combine(McpToolCatalogSnapshotTests.RepositoryRoot(), "src", "Whiskers");
        Assert.DoesNotContain(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories),
            f => File.ReadAllText(f).Contains("AddEnvironmentVariables(", StringComparison.Ordinal));
    }

    private static int CountOf(string text, string what)
    {
        var count = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ---- Key files (review B1.5) -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_key_file_wins_over_the_value_and_is_what_the_cli_reads()
    {
        var file = Path.Combine(_dir, "ts-key");
        File.WriteAllText(file, Key + "\n");
        var settings = VpnSecretHandlingTests.Settings(s =>
        {
            s.Tailscale.AuthKey = "tskey-auth-the-old-inline-value";
            s.Tailscale.AuthKeyFile = file;
        }).Value;
        VpnKeyFiles.Apply(settings);
        var runner = new VpnSecretHandlingTests.RecordingVpnRunner();
        var provider = new TailscaleVpnProvider(Options.Create(settings),
            new VpnSecretHandlingTests.ListLogger<TailscaleVpnProvider>(), runner, () => false, _dir);

        Assert.True((await provider.EnsureUpAsync()).Success);
        Assert.Equal(Key, runner.Up.KeyContent);
        Assert.Null(settings.KeyFileProblem);
    }

    [Fact]
    public async Task A_key_file_that_cannot_be_read_fails_the_bring_up_before_any_cli_runs()
    {
        var settings = VpnSecretHandlingTests.Settings(s =>
        {
            s.Provider = "netbird";
            s.Netbird.SetupKeyFile = Path.Combine(_dir, "missing");
        }).Value;
        VpnKeyFiles.Apply(settings);
        var runner = new VpnSecretHandlingTests.RecordingVpnRunner();
        var provider = new NetbirdVpnProvider(Options.Create(settings),
            new VpnSecretHandlingTests.ListLogger<NetbirdVpnProvider>(), runner, _dir);

        var result = await provider.EnsureUpAsync();

        Assert.Equal(VpnBringUpState.InvalidSetting, result.State);
        Assert.Contains("Vpn:Netbird:SetupKeyFile", result.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void An_empty_key_file_is_a_problem_not_an_empty_key()
    {
        var file = Path.Combine(_dir, "empty");
        File.WriteAllText(file, "  \n");
        var settings = new VpnSettings { Tailscale = { AuthKey = null, AuthKeyFile = file } };
        VpnKeyFiles.Apply(settings);
        Assert.Contains("is empty", settings.KeyFileProblem);
        Assert.Null(settings.Tailscale.AuthKey);
    }

    [SkippableFact]
    public void A_key_file_readable_by_others_is_warned_about()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no Unix file modes");
        var file = Path.Combine(_dir, "wide");
        File.WriteAllText(file, Key);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var settings = new VpnSettings { Tailscale = { AuthKeyFile = file } };
        VpnKeyFiles.Apply(settings);
        Assert.Equal(Key, settings.Tailscale.AuthKey);
        Assert.Contains("readable by group or others", settings.KeyFileWarning);
        VpnSecretHandlingTests.AssertNoKey(settings.KeyFileWarning, Key);
    }

    // ---- Leftovers of a crash (review m3) --------------------------------------------------------------------------

    [Fact]
    public void Key_directories_left_behind_are_removed_and_nothing_else()
    {
        var stale = Path.Combine(_dir, VpnSecretFile.DirectoryPrefix + "0123abcd");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "key"), Key);
        var keep = Path.Combine(_dir, "whiskers-vpnkey.abcdef");   // the entrypoint's file for this run
        Directory.CreateDirectory(keep);

        Assert.Equal(1, VpnSecretFile.RemoveStale([_dir]));

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(keep));
    }

    [Fact]
    public void A_configured_secret_directory_is_the_only_place_key_files_go()
    {
        Environment.SetEnvironmentVariable(VpnSecretFile.ParentVariable, _dir);
        try
        {
            Assert.Equal([_dir], VpnSecretFile.DefaultParents());
            using var file = VpnSecretFile.Create(Key);
            Assert.StartsWith(_dir, file.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VpnSecretFile.ParentVariable, null);
        }
    }
}

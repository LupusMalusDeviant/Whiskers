using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Modules;

namespace Whiskers.Tests;

/// <summary>RoadToSAP §6 DoD — the full-app boot matrix. Boots the real application in-process via
/// <see cref="WebApplicationFactory{T}"/> under Environment=Development (so the DI container runs
/// ValidateOnBuild + ValidateScopes over the WHOLE graph — Core plus every enabled module) and pings the
/// dependency-free <c>/healthz</c> liveness endpoint. This is the automated counterpart to the per-PR manual
/// boot-gate: it proves the app comes up in the extreme module configurations (all modules on, all off = only
/// Core, and the opt-in example module on) without a DI regression. Docker/DB unavailability is irrelevant —
/// <c>/healthz</c> carries no dependency checks; a real boot/DI break instead surfaces as an exception when the
/// host starts.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var below
public class BootMatrixTests
{
    /// <summary>Boots the app with <paramref name="featureSettings"/> and asserts it came up with exactly the modules in
    /// <paramref name="expectedModules"/> — written out per case, not read through the app's flag reader — and the
    /// auth-disabled setup.</summary>
    private static async Task AssertBootsAsync(IReadOnlyCollection<string> expectedModules,
        IEnumerable<KeyValuePair<string, string?>> featureSettings)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        // WHISKERS_DATA_DIR is read EAGERLY in Program.cs (DataPathOptions.FromConfiguration), before the
        // factory's in-memory config is layered in — it must be a real env var (see SetupWizardBootTests).
        // An in-memory-only value made the boot silently use the /app/data default, which CI runners can't write.
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        var config = new List<KeyValuePair<string, string?>>(BootSettings.AuthDisabled);
        config.AddRange(featureSettings);

        try
        {
            using var bootSettings = BootSettings.Apply(config);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development"); // enables ValidateOnBuild + ValidateScopes
                    b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
                });

            // Building + starting the host validates the full DI graph; a broken matrix throws here.
            using var client = factory.CreateClient();
            var resp = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // The boot must really be the configuration it claims — the complete module set: a flag Program never
            // saw, or one inherited from the environment, would leave a "lean" boot full (or a full one lean) and
            // pass anyway.
            var registry = factory.Services.GetRequiredService<IModuleRegistry>();
            var actual = ModuleCatalog.All().Select(m => m.Id).Where(registry.IsEnabled).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(expectedModules.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
            BootSettings.AssertBoot(factory.Services, config);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>The modules a default install runs: every one except the opt-in example.</summary>
    private static readonly string[] DefaultModules =
    [
        "all-in-one", "terminal", "notifications", "scheduler", "logmonitor", "volumebackups", "webhooks",
        "host-management", "deployment", "cve", "cloud-control", "image-updates", "agent", "gitdeploy", "dns",
    ];

    private static KeyValuePair<string, string?> Flag(string module, bool on) =>
        new($"Features:{module}:Enabled", on ? "true" : "false");

    [Fact]
    public Task All_modules_on_the_default_boots() => AssertBootsAsync(DefaultModules, []);

    [Fact]
    public Task Only_core_every_module_off_boots() => AssertBootsAsync([BootSettings.CoreModuleId], BootSettings.OnlyCore());

    [Fact]
    public Task Opt_in_example_module_on_boots() =>
        AssertBootsAsync([.. DefaultModules, "hello-world"], [Flag("hello-world", true)]);

    [Fact]
    public async Task A_lean_boot_stays_lean_with_an_opt_in_flag_inherited_from_the_environment()
    {
        // As if the shell running the tests had Features__hello-world__Enabled=true set.
        using var inherited = BootSettings.Apply([Flag("hello-world", true)]);

        await AssertBootsAsync([BootSettings.CoreModuleId], BootSettings.OnlyCore());
    }

    [Fact]
    public async Task An_auth_setting_given_only_in_memory_is_caught()
    {
        // The original mistake: Auth:Disabled only in the factory's in-memory configuration. The builder never sees
        // it and registers the real authentication; the check must say so.
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        using var data = BootSettings.Apply([new("WHISKERS_DATA_DIR", dataDir)]);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(BootSettings.AuthDisabled));
                });
            using var client = factory.CreateClient();

            var caught = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => BootSettings.AssertAuthenticationSetup(factory.Services, disabled: true));
            Assert.Contains("real authentication setup", caught.Message);
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}

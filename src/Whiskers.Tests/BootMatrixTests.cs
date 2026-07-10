using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
public class BootMatrixTests
{
    private static async Task AssertBootsAsync(params (string Key, string Value)[] featureSettings)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        var config = new List<KeyValuePair<string, string?>>
        {
            new("WHISKERS_DATA_DIR", dataDir), // config-resolved (DataPathOptions.FromConfiguration) — no process env
            new("Auth:Disabled", "true"),
        };
        foreach (var (key, value) in featureSettings)
            config.Add(new(key, value));

        try
        {
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
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    // Every default-on module turned off (all-in-one has no flag → stays, carrying the Core surface) = "only Core".
    private static (string, string)[] AllModulesOff() =>
        ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build())
            .Where(m => m.Id != "all-in-one")
            .Select(m => ($"Features:{m.Id}:Enabled", "false"))
            .ToArray();

    [Fact]
    public Task All_modules_on_the_default_boots() => AssertBootsAsync();

    [Fact]
    public Task Only_core_every_module_off_boots() => AssertBootsAsync(AllModulesOff());

    [Fact]
    public Task Opt_in_example_module_on_boots() => AssertBootsAsync(("Features:hello-world:Enabled", "true"));
}

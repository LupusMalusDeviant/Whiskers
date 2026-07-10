using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Modules;
using Whiskers.Services.Maintenance;

namespace Whiskers.Tests;

/// <summary>The F3 maintenance middleware, end-to-end via the real app: once maintenance is entered, a top-level
/// HTML navigation gets 503 while the liveness probe stays 200 (exempt). Shares the WebAppBoot collection so the
/// process-wide WHISKERS_DATA_DIR env var can't leak into a parallel factory boot.</summary>
[Collection("WebAppBoot")]
public sealed class MaintenanceBootTests
{
    [Fact]
    public async Task Maintenance_returns_503_for_html_navigation_but_healthz_stays_up()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-maint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        var config = new List<KeyValuePair<string, string?>>();
        foreach (var m in ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build()).Where(m => m.Id != "all-in-one"))
            config.Add(new($"Features:{m.Id}:Enabled", "false"));   // lean boot
        config.Add(new("Auth:Disabled", "true"));                   // focus on maintenance, not login

        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var beforeHealth = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, beforeHealth.StatusCode);

            // Flip the shared singleton the middleware reads.
            factory.Services.GetRequiredService<IMaintenanceStateService>().EnterMaintenance("test restore");

            var nav = new HttpRequestMessage(HttpMethod.Get, "/dashboard");
            nav.Headers.Accept.ParseAdd("text/html");
            var navResp = await client.SendAsync(nav);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, navResp.StatusCode);

            var health = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);   // liveness stays up (exempt from the gate)
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

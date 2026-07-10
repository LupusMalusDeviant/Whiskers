using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Whiskers.Modules;

namespace Whiskers.Tests;

/// <summary>Serializes the WebApplicationFactory boot tests that set the WHISKERS_DATA_DIR env var, so it can
/// never leak into a parallel factory boot.</summary>
[CollectionDefinition("WebAppBoot", DisableParallelization = true)]
public class WebAppBootCollection { }

/// <summary>W1 setup-redirect, end-to-end via the real app: a fresh instance funnels HTML navigation to /setup,
/// a configured admin skips it, and Auth:Disabled bypasses it entirely.</summary>
[Collection("WebAppBoot")]
public sealed class SetupWizardBootTests
{
    // WHISKERS_DATA_DIR is read EAGERLY in Program.cs (DataPathOptions.FromConfiguration), before the factory's
    // in-memory config is layered in — so it must be a real ENV var for the data dir to be isolated per test.
    // The [Collection] above serializes these boots so this process-wide var can't leak into a parallel factory.
    private static async Task WithClientAsync(IEnumerable<KeyValuePair<string, string?>> extra, Func<HttpClient, Task> body)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-setupboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        var config = new List<KeyValuePair<string, string?>>();
        foreach (var m in ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build()).Where(m => m.Id != "all-in-one"))
            config.Add(new($"Features:{m.Id}:Enabled", "false"));   // lean boot
        config.AddRange(extra);
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await body(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static Task<HttpResponseMessage> HtmlGet(HttpClient client, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.ParseAdd("text/html");   // simulate a browser navigation
        return client.SendAsync(req);
    }

    [Fact]
    public Task Fresh_instance_funnels_html_navigation_to_setup() =>
        WithClientAsync(new[] { new KeyValuePair<string, string?>("Auth:Disabled", "false") }, async client =>
        {
            var dash = await HtmlGet(client, "/dashboard");
            Assert.Equal(HttpStatusCode.Redirect, dash.StatusCode);
            Assert.Equal("/setup", dash.Headers.Location!.OriginalString);

            var health = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);       // infra not trapped

            var setup = await HtmlGet(client, "/setup");
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);        // wizard renders
        });

    [Fact]
    public Task Configured_admin_skips_the_wizard() =>
        WithClientAsync(new[]
        {
            new KeyValuePair<string, string?>("Auth:Disabled", "false"),
            new KeyValuePair<string, string?>("WHISKERS_ADMIN_EMAIL", "admin@x"),   // C5 seeds an Admin role at boot
        }, async client =>
        {
            var setup = await HtmlGet(client, "/setup");
            Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);
            Assert.Equal("/", setup.Headers.Location!.OriginalString);              // wizard is a dead route

            var dash = await HtmlGet(client, "/dashboard");
            Assert.NotEqual("/setup", dash.Headers.Location?.OriginalString);        // never funneled to /setup
        });

    [Fact]
    public Task Auth_disabled_has_no_setup_redirect() =>
        WithClientAsync(new[] { new KeyValuePair<string, string?>("Auth:Disabled", "true") }, async client =>
        {
            var dash = await HtmlGet(client, "/dashboard");
            Assert.NotEqual("/setup", dash.Headers.Location?.OriginalString);        // bypass mode = no wizard
        });
}

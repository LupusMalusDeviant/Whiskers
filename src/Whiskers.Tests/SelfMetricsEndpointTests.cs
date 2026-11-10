using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Services.Observability.SelfMetrics;

namespace Whiskers.Tests;

/// <summary>
/// The <c>whiskers_self_*</c> series on the real endpoint (Plan-0003 WP3).
///
/// <para>Whiskers exported the container inventory of the whole fleet and not one number about itself. These
/// tests boot the real application and read <c>/metrics</c>, because "the exporter compiles" and "the series
/// are served" are different claims — and it was the second one that failed unnoticed for the MCP surface
/// from 0.12.0 to 0.13.0.</para>
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class SelfMetricsEndpointTests
{
    private const string ScrapeToken = "test-scrape-token";

    private static async Task WithAppAsync(Func<HttpClient, IServiceProvider, Task> body)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-selfmetrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var previous = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        try
        {
            var settings = new List<KeyValuePair<string, string?>>
            {
                new("Auth:Disabled", "true"),
                new("Metrics:ScrapeToken", ScrapeToken),
            };
            using var bootSettings = BootSettings.Apply(settings);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
                });

            using var client = factory.CreateClient();
            BootSettings.AssertBoot(factory.Services, settings);
            await body(client, factory.Services);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previous);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static async Task<string> ScrapeAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);

        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"/metrics answered {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task The_endpoint_carries_the_loop_health_series() => await WithAppAsync(async (client, services) =>
    {
        var metrics = services.GetRequiredService<ISelfMetrics>();
        metrics.RecordCycle("logmonitor", "badwolf", TimeSpan.FromSeconds(2), success: true);
        metrics.RecordSkip("cve", "k3s-cluster", "Kubernetes server, Docker-only loop");
        metrics.Count("log_fetch_timeouts", "badwolf");

        var body = await ScrapeAsync(client);

        Assert.Contains("whiskers_self_loop_last_success_age_seconds", body);
        Assert.Contains("loop=\"logmonitor\",server=\"badwolf\"", body);
        Assert.Contains("whiskers_self_log_fetch_timeouts_total", body);

        // A server a loop deliberately skips must still be visible. Absent, "not monitored here" reads
        // exactly like "nothing to report" — the confusion this whole package exists to remove.
        Assert.Contains("server=\"k3s-cluster\"", body);
        Assert.Contains("result=\"skipped\"", body);
    });

    [Fact]
    public async Task The_self_series_survive_a_fleet_that_answers_nothing() => await WithAppAsync(async (client, _) =>
    {
        // No Docker host is reachable in the test environment, so the inventory section fails. The self
        // metrics must still be there — they are exactly what you need when the fleet has gone silent, and
        // putting them behind the inventory would have made them disappear at the worst moment.
        var body = await ScrapeAsync(client);

        Assert.Contains("whiskers_self_", body);
    });

    [Fact]
    public async Task A_paused_server_shows_up_as_a_number() => await WithAppAsync(async (client, services) =>
    {
        // Plan-0005: a paused server reports nothing at all, which on a dashboard reads exactly like a quiet
        // one. The pause has to be a series somebody can alert on, or the emergency stop becomes a way to hide
        // a server rather than protect it.
        var suspension = services.GetRequiredService<Whiskers.Services.Observability.ILoopSuspensionService>();
        GatewayArrange.Run("checks.pause", new Whiskers.Services.Mutations.MutationTarget("server", "badwolf", ServerId: "badwolf"), () => suspension.Suspend("badwolf", DateTime.UtcNow.AddMinutes(30), "test", automatic: true));

        var body = await ScrapeAsync(client);

        Assert.Contains("whiskers_loops_paused{server=\"badwolf\",automatic=\"true\"} 1", body);

        GatewayArrange.Run("checks.resume", new Whiskers.Services.Mutations.MutationTarget("server", "badwolf", ServerId: "badwolf"), () => suspension.Resume("badwolf"));
        Assert.DoesNotContain("whiskers_loops_paused{server=\"badwolf\"", await ScrapeAsync(client));
    });

    [Fact]
    public async Task The_log_scan_exclusion_count_is_exported() => await WithAppAsync(async (client, _) =>
    {
        // Plan-0007 WP2.2. What matters is not the value but its movement: exclusions appearing without a
        // configuration change mean the access-path detection has grown too greedy, and a container excluded
        // by mistake looks precisely like one with nothing to report.
        var body = await ScrapeAsync(client);

        Assert.Contains("whiskers_log_scan_exclusions", body);
    });

    [Fact]
    public async Task A_german_scraper_still_gets_dot_decimals() => await WithAppAsync(async (client, services) =>
    {
        // /metrics sits behind UseRequestLocalization, which supports "de". Without an explicit culture pin a
        // scraper sending Accept-Language: de gets "0,120" — and Prometheus rejects the ENTIRE scrape on the
        // first unparsable line. The monitoring would go dark because of a request header, which is exactly
        // the class of failure this whole work package exists to catch.
        var metrics = services.GetRequiredService<ISelfMetrics>();
        metrics.RecordCycle("logmonitor", "badwolf", TimeSpan.FromMilliseconds(1234.5), success: true, TimeSpan.FromMinutes(1));

        var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        request.Headers.AcceptLanguage.ParseAdd("de-DE");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Every sample line is "name{labels} value" — the value must parse under the invariant culture.
        var values = body.Split('\n')
            .Where(l => l.StartsWith("whiskers_self_", StringComparison.Ordinal))
            .Select(l => l[(l.LastIndexOf(' ') + 1)..].Trim())
            .Where(v => v.Length > 0)
            .ToList();

        Assert.NotEmpty(values);
        foreach (var value in values)
        {
            Assert.DoesNotContain(",", value);
            Assert.True(
                double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
                $"Prometheus could not parse '{value}' — the whole scrape would be rejected.");
        }
    });

    [Fact]
    public async Task The_open_host_findings_are_exported() => await WithAppAsync(async (client, _) =>
    {
        // Plan-0004 WP5.4. A monitor whose alerts never close stops being read long before anyone works out
        // why, so the count of unclosed findings and the age of the oldest are numbers you can alert on.
        var body = await ScrapeAsync(client);

        Assert.Contains("whiskers_host_findings_open", body);
        Assert.Contains("whiskers_host_finding_oldest_age_seconds", body);
    });

    [Fact]
    public async Task An_incomplete_audit_trail_shows_up_as_a_number() => await WithAppAsync(async (client, services) =>
    {
        // R1-53: the audit trail's completeness was a readiness check only. An outcome that could not be written is now
        // a series an alert can watch. (A failed write of the older audit holds for an hour, so the value is stable
        // while the recovery loop runs beside the test.)
        var recovery = services.GetRequiredService<Whiskers.Services.Mutations.IAuditRecovery>();
        Assert.Contains("whiskers_self_audit_legacy_write_failed 0", await ScrapeAsync(client));

        recovery.LegacyWriteFailed();
        var body = await ScrapeAsync(client);

        Assert.Contains("whiskers_self_audit_legacy_write_failed 1", body);
        Assert.Contains("# TYPE whiskers_self_audit_events_pending gauge", body);
        Assert.Contains("# TYPE whiskers_self_audit_intents_unsettled gauge", body);
    });

    [Fact]
    public async Task Every_answer_carries_its_request_id() => await WithAppAsync(async (client, _) =>
    {
        // R1-53: the correlation middleware runs in the real pipeline — a safe client id comes back, an unsafe one is
        // replaced, and a refused answer carries one too (here the 401 of the /metrics scrape-token check; the boot runs
        // with the LAN bypass, so no auth-chain refusal is exercised).
        var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        request.Headers.TryAddWithoutValidation("X-Request-Id", "scrape-42");
        var response = await client.SendAsync(request);
        Assert.Equal("scrape-42", Assert.Single(response.Headers.GetValues("X-Request-Id")));

        var unsafeRequest = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        unsafeRequest.Headers.TryAddWithoutValidation("X-Request-Id", "<b>not an id</b>");
        var refused = await client.SendAsync(unsafeRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(refused.Headers.GetValues("X-Request-Id")));
    });

    [Fact]
    public async Task The_endpoint_stays_shut_without_the_token() => await WithAppAsync(async (client, _) =>
    {
        var response = await client.GetAsync("/metrics");

        // The payload now says how Whiskers behaves and which servers exist. It must not become readable
        // just because it grew more interesting.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    });

    [Fact]
    public async Task Stale_CVE_data_shows_up_as_a_number_on_the_real_endpoint() =>
        await WithAppAsync(async (client, services) =>
    {
        // The claim worth testing is not that CveMetrics computes a number — the unit tests cover that — but
        // that the number reaches the served output. That is precisely the step that failed unnoticed for the
        // MCP surface from 0.12.0 to 0.13.0, and it is the step a mocked test cannot see.
        var store = services.GetRequiredService<Whiskers.Services.Cve.ICveFindingsStore>();
        store.Set(new Whiskers.Models.Cve.CveScanResult
        {
            ServerId = "badwolf",
            Source = Whiskers.Models.Cve.CveSource.Container,
            ContainerId = "c-authentik",
            ContainerName = "authentik-worker-1",
            ScannedAt = DateTime.UtcNow.AddDays(-50),   // the scanner broke seven weeks ago
            Findings =
            [
                new Whiskers.Models.Cve.CveFinding
                {
                    ServerId = "badwolf", Source = Whiskers.Models.Cve.CveSource.Container,
                    ContainerId = "c-authentik", ContainerName = "authentik-worker-1",
                    CveId = "CVE-2026-9999", Package = "openssl",
                    Severity = Whiskers.Models.Cve.CveSeverity.Critical
                }
            ]
        });

        var body = await ScrapeAsync(client);

        Assert.Contains("whiskers_cve_stale_targets{server=\"badwolf\"} 1", body);
        Assert.Contains("whiskers_cve_findings{server=\"badwolf\",severity=\"Critical\"} 1", body);
        Assert.Contains("whiskers_cve_distinct_ids 1", body);
        Assert.Contains("whiskers_cve_data_age_seconds{server=\"badwolf\"}", body);
    });

    [Fact]
    public async Task The_CVE_series_carry_no_container_label() =>
        await WithAppAsync(async (client, services) =>
    {
        // The endpoint bounds cardinality by server, never by container: a container label across a large
        // fleet turns a few dozen series into thousands and fills the time-series database — a monitoring
        // outage caused by monitoring. Naming the guilty target is the UI's job, not this endpoint's.
        var store = services.GetRequiredService<Whiskers.Services.Cve.ICveFindingsStore>();
        store.Set(new Whiskers.Models.Cve.CveScanResult
        {
            ServerId = "badwolf", Source = Whiskers.Models.Cve.CveSource.Container,
            ContainerId = "c-authentik", ContainerName = "authentik-worker-1",
            ScannedAt = DateTime.UtcNow.AddDays(-50)
        });

        var body = await ScrapeAsync(client);

        foreach (var line in body.Split('\n').Where(l => l.StartsWith("whiskers_cve_")))
        {
            Assert.DoesNotContain("container=", line);
            Assert.DoesNotContain("authentik-worker-1", line);
        }
    });

    [Fact]
    public async Task An_invalid_reloaded_CVE_interval_neither_breaks_the_scrape_nor_moves_the_staleness() =>
        await WithAppAsync(async (client, services) =>
    {
        // R1-41, review round 2: the reload is allowed (the loop keeps its last valid interval), but /metrics converted the
        // raw value — 2147483647 hours overflowed TimeSpan and the whole scrape answered 500 — and a smaller bad value made
        // the staleness threshold disagree with the cadence the loop keeps. Driven through a real reload of the real app.
        var config = (Microsoft.Extensions.Configuration.IConfigurationRoot)services
            .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Whiskers.Configuration.CveMonitorSettings>>();
        void Reload(string hours)
        {
            config["CveMonitor:CheckIntervalHours"] = hours;
            config.Reload();
            Assert.Equal(int.Parse(hours), options.CurrentValue.CheckIntervalHours);   // the reload really happened
        }

        var store = services.GetRequiredService<Whiskers.Services.Cve.ICveFindingsStore>();
        store.Set(new Whiskers.Models.Cve.CveScanResult
        {
            ServerId = "badwolf", Source = Whiskers.Models.Cve.CveSource.Os, ScannedAt = DateTime.UtcNow.AddHours(-30),
        });
        const string stale = "whiskers_cve_stale_targets{server=\"badwolf\"} 1";   // 30 h old, past two 12-hour intervals

        Reload("12");
        Assert.Contains(stale, await ScrapeAsync(client));

        Reload("2147483647");
        var body = await ScrapeAsync(client);                    // answered, not an overflow
        Assert.Contains(stale, body);                            // judged by the interval the loop keeps
        Assert.Contains("whiskers_self_series_total", body);
        Assert.Contains("whiskers_cve_distinct_ids", body);

        Reload("721");                                           // just out of range: 2 × 721 h would have hidden it
        Assert.Contains(stale, await ScrapeAsync(client));
        Assert.Equal(TimeSpan.FromHours(12),
            services.GetRequiredService<Whiskers.Configuration.EffectiveInterval<Whiskers.Configuration.CveMonitorSettings>>().Interval);
    });
}

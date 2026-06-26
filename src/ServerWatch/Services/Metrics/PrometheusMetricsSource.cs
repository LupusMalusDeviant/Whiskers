using System.Globalization;
using System.Text.Json;
using ServerWatch.Models;
using ServerWatch.Services.ServerConfig;

namespace ServerWatch.Services.Metrics;

/// <summary>
/// Reads metrics from a Prometheus-compatible TSDB (VictoriaMetrics) fed by node_exporter and
/// cAdvisor over the mesh. No SSH key involved. Servers are matched by the <c>server</c> label,
/// which carries the server's <see cref="ServerConfig.Name"/> (see deploy/telemetry/scrape.yml).
/// </summary>
public class PrometheusMetricsSource
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ServerConfigService _serverConfig;
    private readonly ILogger<PrometheusMetricsSource> _logger;

    public PrometheusMetricsSource(
        IHttpClientFactory httpFactory,
        ServerConfigService serverConfig,
        ILogger<PrometheusMetricsSource> logger)
    {
        _httpFactory = httpFactory;
        _serverConfig = serverConfig;
        _logger = logger;
    }

    // NOTE: no container-stats method here on purpose. Container stats come from the Docker API
    // (see MetricsSourceDispatcher). This source only serves HOST metrics from the TSDB.

    public async Task<ServerSystemInfo?> GetServerSystemInfoAsync(string serverId)
    {
        var server = _serverConfig.GetServer(serverId);
        if (server?.MetricsEndpoint is not { Length: > 0 } endpoint)
            return null;

        var s = Escape(server.Name);
        var info = new ServerSystemInfo { ServerId = server.Id, ServerName = server.Name };

        var memTotal = await QueryScalarAsync(endpoint, $"node_memory_MemTotal_bytes{{server=\"{s}\"}}");
        var memAvail = await QueryScalarAsync(endpoint, $"node_memory_MemAvailable_bytes{{server=\"{s}\"}}");
        var cpu = await QueryScalarAsync(endpoint,
            $"100 - (avg(rate(node_cpu_seconds_total{{mode=\"idle\",server=\"{s}\"}}[2m]))*100)");
        var cpuCount = await QueryScalarAsync(endpoint,
            $"count(count by(cpu)(node_cpu_seconds_total{{mode=\"idle\",server=\"{s}\"}}))");

        if (memTotal == null && cpu == null)
        {
            // Nothing scraped for this server — report unreachable so the collector skips it
            // rather than persisting a row of zeros.
            info.IsReachable = false;
            info.Error = "No telemetry in TSDB for this server (scrape target down or label mismatch).";
            return info;
        }

        info.IsReachable = true;
        info.MemoryTotalBytes = (long)(memTotal ?? 0);
        info.MemoryUsedBytes = (long)Math.Max(0, (memTotal ?? 0) - (memAvail ?? 0));
        info.CpuUsagePercent = cpu ?? 0;
        info.CpuCount = (int)(cpuCount ?? 0);
        return info;
    }

    /// <summary>
    /// Runs an instant query against the TSDB and returns the first result's scalar value,
    /// or null if the query failed or returned no series.
    /// </summary>
    private async Task<double?> QueryScalarAsync(string endpoint, string promql)
    {
        try
        {
            var url = $"{endpoint.TrimEnd('/')}/api/v1/query?query={Uri.EscapeDataString(promql)}";
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);

            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("TSDB query failed ({Status}) for {Query}", resp.StatusCode, promql);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var result = doc.RootElement.GetProperty("data").GetProperty("result");
            if (result.GetArrayLength() == 0)
                return null;

            var value = result[0].GetProperty("value")[1].GetString();
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TSDB query errored for {Query}", promql);
            return null;
        }
    }

    // Escape a label value for use inside a PromQL selector (backslash and double-quote).
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

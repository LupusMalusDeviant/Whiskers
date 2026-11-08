using System.Net;
using System.Text;
using Whiskers.Services.Metrics;
using Whiskers.Services.Metrics.DiskHealth;

namespace Whiskers.Tests;

/// <summary>
/// R1-58: recorded Prometheus/VictoriaMetrics <c>/api/v1/query</c> answers for the six disk-health queries of host
/// <c>badwolf</c>, as node_exporter v1.8.2 with smartmon.sh's textfile delivers them, evaluated at
/// <see cref="Now"/> (2026-11-05 12:00:00 UTC). Every sample carries that fresh query time — only the collection-age
/// series (<c>smartmon_smartctl_run</c>, <c>node_textfile_mtime_seconds</c>) say how old the SMART data really is.
/// </summary>
internal static class DiskHealthFixtures
{
    public static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);   // unix 1793880000

    // ---- SMART (textfile collector, smartmon.sh) --------------------------------------------------------------

    public const string SmartHealthy = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"smartmon_device_smart_available","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"sat"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_enabled","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"sat"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_healthy","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"sat"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_available","disk":"/dev/nvme0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"nvme"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_enabled","disk":"/dev/nvme0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"nvme"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_healthy","disk":"/dev/nvme0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"nvme"},"value":[1793880000.123,"1"]}
        ]}}
        """;

    /// <summary>/dev/sda: SMART overall-health self-assessment failed.</summary>
    public const string SmartFailed = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"smartmon_device_smart_available","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"sat"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_enabled","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"sat"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_healthy","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"sat"},"value":[1793880000.123,"0"]},
          {"metric":{"__name__":"smartmon_device_smart_available","disk":"/dev/nvme0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"nvme"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_enabled","disk":"/dev/nvme0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"nvme"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"smartmon_device_smart_healthy","disk":"/dev/nvme0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"nvme"},"value":[1793880000.123,"1"]}
        ]}}
        """;

    /// <summary>The script ran at 11:45 (15 minutes ago).</summary>
    public const string SmartRunFresh = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"smartmon_smartctl_run","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"sat"},"value":[1793880000.123,"1793879100"]},
          {"metric":{"__name__":"smartmon_smartctl_run","disk":"/dev/nvme0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"nvme"},"value":[1793880000.123,"1793879101"]}
        ]}}
        """;

    /// <summary>The script last ran at 09:00 — three hours ago, past the 2-hour limit. The sample time is fresh.</summary>
    public const string SmartRunExpired = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"smartmon_smartctl_run","disk":"/dev/sda","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"sat"},"value":[1793880000.123,"1793869200"]},
          {"metric":{"__name__":"smartmon_smartctl_run","disk":"/dev/nvme0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","type":"nvme"},"value":[1793880000.123,"1793869201"]}
        ]}}
        """;

    /// <summary>smartmon.prom written at 11:45:05 (node_exporter ≥ 1.7 labels the file by its path).</summary>
    public const string MtimeFresh = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"node_textfile_mtime_seconds","file":"/host/var/lib/node_exporter/textfile_collector/smartmon.prom","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"1793879105.25"]}
        ]}}
        """;

    /// <summary>smartmon.prom last written at 09:00:05 — the timer stopped; node_exporter keeps serving the old file.</summary>
    public const string MtimeExpired = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"node_textfile_mtime_seconds","file":"/host/var/lib/node_exporter/textfile_collector/smartmon.prom","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"1793869205"]}
        ]}}
        """;

    // ---- md (mdadm collector) ---------------------------------------------------------------------------------

    public const string MdHealthy = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"active"},"value":[1793880000.123,"2"]},
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"failed"},"value":[1793880000.123,"0"]},
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"spare"},"value":[1793880000.123,"0"]},
          {"metric":{"__name__":"node_md_disks_required","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"2"]},
          {"metric":{"__name__":"node_md_state","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"active"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"node_md_state","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"inactive"},"value":[1793880000.123,"0"]},
          {"metric":{"__name__":"node_md_state","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"recovering"},"value":[1793880000.123,"0"]}
        ]}}
        """;

    /// <summary>md0: one of its two disks marked failed (still in the array, [U_]).</summary>
    public const string MdFailedDisk = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"active"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"failed"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"spare"},"value":[1793880000.123,"0"]},
          {"metric":{"__name__":"node_md_disks_required","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"2"]},
          {"metric":{"__name__":"node_md_state","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"active"},"value":[1793880000.123,"1"]}
        ]}}
        """;

    /// <summary>md0: the failed disk was removed — one active disk of two required, none marked failed.</summary>
    public const string MdFewerActive = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"active"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"failed"},"value":[1793880000.123,"0"]},
          {"metric":{"__name__":"node_md_disks","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"spare"},"value":[1793880000.123,"0"]},
          {"metric":{"__name__":"node_md_disks_required","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"2"]},
          {"metric":{"__name__":"node_md_state","device":"md0","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"active"},"value":[1793880000.123,"1"]}
        ]}}
        """;

    // ---- ZFS (zfs collector) ----------------------------------------------------------------------------------

    /// <summary>Pool <c>tank</c> in <paramref name="state"/>: 1 for that state, 0 for the others, as the collector emits it.</summary>
    public static string Zpool(string state)
    {
        var states = new[] { "online", "degraded", "faulted", "offline", "removed", "unavail", "suspended" };
        var rows = states.Select(s =>
            $$"""{"metric":{"__name__":"node_zfs_zpool_state","instance":"100.64.0.7:9100","job":"node","server":"badwolf","state":"{{s}}","zpool":"tank"},"value":[1793880000.123,"{{(s == state ? 1 : 0)}}"]}""");
        return """{"status":"success","data":{"resultType":"vector","result":[""" + string.Join(",", rows) + "]}}";
    }

    public static readonly string ZfsOnline = Zpool("online");

    // ---- collector success ------------------------------------------------------------------------------------

    public const string CollectorsOk = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"node_scrape_collector_success","collector":"mdadm","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"1"]},
          {"metric":{"__name__":"node_scrape_collector_success","collector":"zfs","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"1"]}
        ]}}
        """;

    public const string CollectorsMdadmFailed = """
        {"status":"success","data":{"resultType":"vector","result":[
          {"metric":{"__name__":"node_scrape_collector_success","collector":"mdadm","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"0"]},
          {"metric":{"__name__":"node_scrape_collector_success","collector":"zfs","instance":"100.64.0.7:9100","job":"node","server":"badwolf"},"value":[1793880000.123,"1"]}
        ]}}
        """;

    // ---- empty, refused, malformed ----------------------------------------------------------------------------

    public const string Empty = """{"status":"success","data":{"resultType":"vector","result":[]}}""";

    public const string StatusError = """{"status":"error","errorType":"bad_data","error":"1:1: parse error: unexpected character inside braces: '!'"}""";

    public const string Truncated = """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"__name__":"smartmon_device_smart_healthy","disk":"/dev/sda"},"value":[1793880000.123,""";

    public const string ValueNotANumberPair = """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"__name__":"smartmon_device_smart_healthy","disk":"/dev/sda"},"value":[1793880000.123]}]}}""";

    public const string MatrixInsteadOfVector = """{"status":"success","data":{"resultType":"matrix","result":[]}}""";

    // ---- assembled readings -----------------------------------------------------------------------------------

    public static PromQueryResult P(string json) => PromQueryResult.Parse(json);

    public static DiskHealthQueries Queries(string smart = SmartHealthy, string run = SmartRunFresh, string mtime = MtimeFresh,
        string md = MdHealthy, string? zfs = null, string collectors = CollectorsOk) =>
        new(P(smart), P(run), P(mtime), P(md), P(zfs ?? ZfsOnline), P(collectors));

    public static DiskHealthReading Read(DiskHealthQueries queries, DiskHealthReading? previous = null, DateTime? at = null) =>
        DiskHealthParser.Parse("badwolf", queries, at ?? Now, previous, DiskHealthParser.DefaultFreshness);

    public static DiskHealthReading HealthyReading(DateTime? at = null) => Read(Queries(), at: at);
}

/// <summary>A message handler that answers from a function.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request.RequestUri!.AbsoluteUri);
        return answer(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>An <see cref="IHttpClientFactory"/> over one handler.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>A query client answering from a function of (endpoint, PromQL).</summary>
internal sealed class StubPromClient(Func<string, string, CancellationToken, Task<PromQueryResult>> answer) : IPrometheusQueryClient
{
    public List<(string Endpoint, string PromQl)> Calls { get; } = [];

    public Task<PromQueryResult> QueryAsync(string endpoint, string promql, CancellationToken ct)
    {
        lock (Calls) Calls.Add((endpoint, promql));
        return answer(endpoint, promql, ct);
    }

    /// <summary>Answers the six queries from <paramref name="queries"/> by their position in <see cref="DiskHealthQueries.For"/>.</summary>
    public static StubPromClient From(Func<string, DiskHealthQueries> perEndpoint) => new((endpoint, promql, _) =>
    {
        var q = perEndpoint(endpoint);
        var all = new[] { q.Smart, q.SmartRun, q.TextfileMtime, q.Md, q.Zfs, q.Collectors };
        var index = DiskHealthQueries.For("badwolf").Select(Shape).ToList().IndexOf(Shape(promql));
        return Task.FromResult(index < 0 ? PromQueryResult.Failed("unexpected query") : all[index]);
    });

    // The query without its server selector, so the position does not depend on the server id.
    private static string Shape(string promql) => System.Text.RegularExpressions.Regex.Replace(promql, "server=\"(\\\\.|[^\"\\\\])*\"", "server");
}

/// <summary>A clock the test moves.</summary>
internal sealed class DiskHealthClock(DateTime start) : TimeProvider
{
    public DateTime Utc { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => new(Utc, TimeSpan.Zero);
}

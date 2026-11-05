using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Services.Metrics;
using Whiskers.Services.Metrics.DiskHealth;
using static Whiskers.Tests.DiskHealthFixtures;

namespace Whiskers.Tests;

/// <summary>
/// R1-58 (contract 0005): SMART, md and ZFS states parsed from recorded <c>/api/v1/query</c> answers into Healthy,
/// DegradedOrFailing, Unknown and Unsupported. Freshness comes only from the collection-age evidence, never from the
/// sample time; Unknown and Unsupported are never Healthy and never clear a DegradedOrFailing reading.
/// </summary>
public sealed class DiskHealthReadingTests
{
    private static DiskHealthItem Item(DiskHealthReading r, string kind, string name) =>
        Assert.Single(r.Items, i => i.Kind == kind && i.Name == name);

    // ---------------------------------------------------------------------------------------------- the good case

    [Fact]
    public void Fresh_good_SMART_md_and_ZFS_read_Healthy_with_one_item_each()
    {
        var r = HealthyReading();

        Assert.Equal(DiskHealthState.Healthy, r.Overall);
        Assert.Equal(4, r.Items.Count);
        Assert.All(r.Items, i => Assert.Equal(DiskHealthState.Healthy, i.State));
        var sda = Item(r, DiskHealthKinds.Smart, "/dev/sda");
        Assert.Equal("SMART overall-health passed", sda.Reason);
        // The older of the two pieces of evidence: the run at 11:45:00, not the file at 11:45:05 or the sample at 12:00.
        Assert.Equal(new DateTime(2026, 11, 5, 11, 45, 0, DateTimeKind.Utc), sda.CollectedUtc);
        Assert.Equal("md0: 2 of 2 disks active", Item(r, DiskHealthKinds.Md, "md0").Reason);
        Assert.Equal(DiskHealthState.Healthy, Item(r, DiskHealthKinds.Zfs, "tank").State);
        Assert.Equal(Now, r.ReadUtc);
    }

    [Fact]
    public void A_textfile_labelled_by_name_as_older_node_exporters_do_is_read_too()
    {
        var byName = MtimeFresh.Replace("/host/var/lib/node_exporter/textfile_collector/smartmon.prom", "smartmon.prom");
        Assert.Equal(DiskHealthState.Healthy, Read(Queries(mtime: byName)).Overall);
    }

    // ---------------------------------------------------------------------------------------------- bad readings

    [Fact]
    public void SMART_failed_is_DegradedOrFailing()
    {
        var r = Read(Queries(smart: SmartFailed));

        Assert.Equal(DiskHealthState.DegradedOrFailing, r.Overall);
        var sda = Item(r, DiskHealthKinds.Smart, "/dev/sda");
        Assert.Equal(DiskHealthState.DegradedOrFailing, sda.State);
        Assert.Equal("SMART overall-health failed", sda.Reason);
        Assert.Equal(DiskHealthState.Healthy, Item(r, DiskHealthKinds.Smart, "/dev/nvme0").State);
    }

    [Fact]
    public void An_md_array_with_a_failed_disk_is_DegradedOrFailing()
    {
        var r = Read(Queries(md: MdFailedDisk));

        Assert.Equal(DiskHealthState.DegradedOrFailing, r.Overall);
        var md0 = Item(r, DiskHealthKinds.Md, "md0");
        Assert.Equal(DiskHealthState.DegradedOrFailing, md0.State);
        Assert.Equal("md0: 1 of 2 disks failed", md0.Reason);
    }

    [Fact]
    public void An_md_array_with_fewer_active_disks_than_required_is_DegradedOrFailing()
    {
        var r = Read(Queries(md: MdFewerActive));

        Assert.Equal(DiskHealthState.DegradedOrFailing, r.Overall);
        Assert.Equal("md0: 1 of 2 disks active", Item(r, DiskHealthKinds.Md, "md0").Reason);
    }

    [Theory]
    [InlineData("degraded")]
    [InlineData("faulted")]
    [InlineData("offline")]
    [InlineData("removed")]
    [InlineData("unavail")]
    [InlineData("suspended")]
    public void A_ZFS_pool_that_is_not_online_is_DegradedOrFailing(string state)
    {
        var r = Read(Queries(zfs: Zpool(state)));

        Assert.Equal(DiskHealthState.DegradedOrFailing, r.Overall);
        var tank = Item(r, DiskHealthKinds.Zfs, "tank");
        Assert.Equal(DiskHealthState.DegradedOrFailing, tank.State);
        Assert.Equal($"pool state {state}", tank.Reason);
    }

    // ---------------------------------------------------------------------------------------------- freshness

    [Fact]
    public void Fresh_sample_times_but_an_expired_smartctl_run_read_Unknown()
    {
        var r = Read(Queries(run: SmartRunExpired));

        Assert.Equal(DiskHealthState.Unknown, r.Overall);
        var sda = Item(r, DiskHealthKinds.Smart, "/dev/sda");
        Assert.Equal(DiskHealthState.Unknown, sda.State);
        Assert.Equal("smartctl last ran 3 h ago", sda.Reason);
        // md and ZFS stay as they are: freshness is per source.
        Assert.Equal(DiskHealthState.Healthy, Item(r, DiskHealthKinds.Md, "md0").State);
    }

    [Fact]
    public void Fresh_sample_times_but_an_expired_textfile_mtime_read_Unknown()
    {
        var r = Read(Queries(mtime: MtimeExpired));

        Assert.Equal(DiskHealthState.Unknown, r.Overall);
        Assert.All(r.Items.Where(i => i.Kind == DiskHealthKinds.Smart), i =>
        {
            Assert.Equal(DiskHealthState.Unknown, i.State);
            Assert.Equal("smartmon.prom 3 h old", i.Reason);    // written 09:00:05, read 12:00
        });
    }

    [Fact]
    public void Missing_collection_age_evidence_reads_Unknown_even_with_a_good_value()
    {
        Assert.Equal("no smartctl run time", Item(Read(Queries(run: Empty)), DiskHealthKinds.Smart, "/dev/sda").Reason);
        Assert.Equal("no modification time for smartmon.prom", Item(Read(Queries(mtime: Empty)), DiskHealthKinds.Smart, "/dev/sda").Reason);
        Assert.Equal(DiskHealthState.Unknown, Read(Queries(run: Empty)).Overall);
        Assert.Equal(DiskHealthState.Unknown, Read(Queries(mtime: Empty)).Overall);
    }

    [Fact]
    public void A_collection_time_far_in_the_future_is_not_trusted()
    {
        var r = Read(Queries(), at: Now.AddHours(-1));   // Whiskers' clock an hour behind the host's

        Assert.Equal(DiskHealthState.Unknown, r.Overall);
        Assert.Equal("collection time lies in the future", Item(r, DiskHealthKinds.Smart, "/dev/sda").Reason);
    }

    [Fact]
    public void A_failed_disk_with_old_evidence_still_reads_failing_never_less()
    {
        var r = Read(Queries(smart: SmartFailed, run: SmartRunExpired));

        Assert.Equal(DiskHealthState.DegradedOrFailing, r.Overall);
        Assert.StartsWith("SMART overall-health failed (", Item(r, DiskHealthKinds.Smart, "/dev/sda").Reason);
    }

    [Fact]
    public void The_freshness_limit_is_two_hours()
    {
        Assert.Equal(TimeSpan.FromHours(2), DiskHealthParser.DefaultFreshness);
        // 11:45 is fresh at 13:44 and expired at 13:46.
        Assert.Equal(DiskHealthState.Healthy, Read(Queries(), at: Now.AddMinutes(104)).Overall);
        Assert.Equal(DiskHealthState.Unknown, Read(Queries(), at: Now.AddMinutes(106)).Overall);
    }

    [Fact]
    public void md_and_ZFS_without_their_collectors_success_read_Unknown()
    {
        var mdFailed = Read(Queries(collectors: CollectorsMdadmFailed));
        Assert.Equal(DiskHealthState.Unknown, Item(mdFailed, DiskHealthKinds.Md, "md0").State);
        Assert.Equal("mdadm collector did not succeed", Item(mdFailed, DiskHealthKinds.Md, "md0").Reason);
        Assert.Equal(DiskHealthState.Healthy, Item(mdFailed, DiskHealthKinds.Zfs, "tank").State);

        var none = Read(Queries(collectors: Empty));
        Assert.Equal(DiskHealthState.Unknown, none.Overall);
        Assert.Equal("no zfs collector status", Item(none, DiskHealthKinds.Zfs, "tank").Reason);

        var refused = Read(Queries(collectors: StatusError));
        Assert.StartsWith("mdadm collector status could not be read", Item(refused, DiskHealthKinds.Md, "md0").Reason);
    }

    // ---------------------------------------------------------------------------------------------- missing, partial, malformed

    [Fact]
    public void Missing_series_read_Unknown_after_a_reading_and_Unsupported_before_any()
    {
        var nothing = Queries(smart: Empty, run: Empty, mtime: Empty, md: Empty, zfs: Empty, collectors: Empty);

        var before = Read(nothing);
        Assert.Equal(DiskHealthState.Unsupported, before.Overall);
        Assert.Empty(before.Items);

        var after = Read(nothing, previous: HealthyReading(Now.AddMinutes(-5)));
        Assert.Equal(DiskHealthState.Unknown, after.Overall);
        Assert.Equal(4, after.Items.Count);
        Assert.All(after.Items, i =>
        {
            Assert.Equal(DiskHealthState.Unknown, i.State);
            Assert.StartsWith(DiskHealthParser.NoLongerReported, i.Reason);
        });
    }

    [Fact]
    public void A_partial_result_reads_Unknown_for_what_went_missing()
    {
        // The ZFS pool was there five minutes ago; now the zfs query answers an empty vector.
        var r = Read(Queries(zfs: Empty), previous: HealthyReading(Now.AddMinutes(-5)));

        Assert.Equal(DiskHealthState.Unknown, r.Overall);
        var tank = Item(r, DiskHealthKinds.Zfs, "tank");
        Assert.Equal(DiskHealthState.Unknown, tank.State);
        Assert.Equal("no longer reported — last: pool state online", tank.Reason);
        Assert.Equal(DiskHealthState.Healthy, Item(r, DiskHealthKinds.Smart, "/dev/sda").State);

        // One disk of two gone from smartmon.prom.
        var oneDisk = FixTrailingComma(string.Join("\n", SmartHealthy.Split('\n').Where(l => !l.Contains("/dev/nvme0"))));
        var partial = Read(Queries(smart: oneDisk), previous: HealthyReading(Now.AddMinutes(-5)));
        Assert.Equal(DiskHealthState.Unknown, Item(partial, DiskHealthKinds.Smart, "/dev/nvme0").State);
        Assert.Equal(DiskHealthState.Unknown, partial.Overall);
    }

    [Fact]
    public void A_carried_item_keeps_its_first_reason_and_does_not_grow()
    {
        var gone = Queries(zfs: Empty);
        var first = Read(gone, previous: HealthyReading(Now.AddMinutes(-10)), at: Now.AddMinutes(-5));
        var second = Read(gone, previous: first);

        Assert.Equal("no longer reported — last: pool state online", Item(second, DiskHealthKinds.Zfs, "tank").Reason);
    }

    [Theory]
    [InlineData(StatusError)]
    [InlineData(Truncated)]
    [InlineData(ValueNotANumberPair)]
    [InlineData(MatrixInsteadOfVector)]
    [InlineData("<html>502 Bad Gateway</html>")]
    public void A_refused_or_malformed_answer_is_a_failed_query_never_an_empty_one(string body)
    {
        var parsed = PromQueryResult.Parse(body);

        Assert.False(parsed.Success);
        Assert.NotNull(parsed.Error);
        Assert.Empty(parsed.Samples);
    }

    [Theory]
    [InlineData(StatusError)]
    [InlineData(Truncated)]
    public void A_refused_or_malformed_answer_reads_Unknown_after_a_reading_and_Unsupported_before_any(string body)
    {
        var all = Queries(smart: body, run: body, mtime: body, md: body, zfs: body, collectors: body);

        Assert.Equal(DiskHealthState.Unsupported, Read(all).Overall);
        var after = Read(all, previous: HealthyReading(Now.AddMinutes(-5)));
        Assert.Equal(DiskHealthState.Unknown, after.Overall);
        Assert.All(after.Items, i =>
        {
            Assert.Equal(DiskHealthState.Unknown, i.State);
            Assert.StartsWith("query failed: ", i.Reason);
        });

        // One malformed source among good ones: only its items are Unknown.
        var oneSource = Read(Queries(md: body), previous: HealthyReading(Now.AddMinutes(-5)));
        Assert.Equal(DiskHealthState.Unknown, oneSource.Overall);
        Assert.Equal(DiskHealthState.Unknown, Item(oneSource, DiskHealthKinds.Md, "md0").State);
        Assert.Equal(DiskHealthState.Healthy, Item(oneSource, DiskHealthKinds.Smart, "/dev/sda").State);
    }

    [Fact]
    public void A_Docker_source_server_without_a_metrics_endpoint_is_Unsupported()
    {
        var r = Read(DiskHealthQueries.AllFailed("no metrics endpoint"));

        Assert.Equal(DiskHealthState.Unsupported, r.Overall);
        Assert.Empty(r.Items);
        Assert.False(r.HadReading);
    }

    [Fact]
    public void A_host_with_none_of_the_three_sources_is_Unsupported()
    {
        // node_exporter answers — no textfile, no md arrays, no pools.
        var r = Read(Queries(smart: Empty, run: Empty, mtime: Empty, md: Empty, zfs: Empty));

        Assert.Equal(DiskHealthState.Unsupported, r.Overall);
    }

    // ---------------------------------------------------------------------------------------------- never healthy, never clears

    public static TheoryData<string> NotVouchedFor() => new()
    {
        "expired-run", "expired-mtime", "no-run", "malformed", "status-error", "missing", "collector-failed", "smart-unavailable",
    };

    private static DiskHealthQueries Case(string name) => name switch
    {
        "expired-run" => Queries(run: SmartRunExpired),
        "expired-mtime" => Queries(mtime: MtimeExpired),
        "no-run" => Queries(run: Empty),
        "malformed" => Queries(smart: Truncated, run: Truncated, mtime: Truncated, md: Truncated, zfs: Truncated, collectors: Truncated),
        "status-error" => Queries(md: StatusError),
        "missing" => Queries(smart: Empty, run: Empty, mtime: Empty, md: Empty, zfs: Empty, collectors: Empty),
        "collector-failed" => Queries(collectors: CollectorsMdadmFailed),
        "smart-unavailable" => Queries(smart: SmartHealthy.Replace(
            "\"__name__\":\"smartmon_device_smart_available\",\"disk\":\"/dev/sda\",\"instance\":\"100.64.0.7:9100\",\"job\":\"node\",\"server\":\"badwolf\",\"type\":\"sat\"},\"value\":[1793880000.123,\"1\"]",
            "\"__name__\":\"smartmon_device_smart_available\",\"disk\":\"/dev/sda\",\"instance\":\"100.64.0.7:9100\",\"job\":\"node\",\"server\":\"badwolf\",\"type\":\"sat\"},\"value\":[1793880000.123,\"0\"]")),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(NotVouchedFor))]
    public void Unknown_and_Unsupported_are_never_Healthy(string name)
    {
        var first = Read(Case(name));
        var afterGood = Read(Case(name), previous: HealthyReading(Now.AddMinutes(-5)));

        Assert.NotEqual(DiskHealthState.Healthy, first.Overall);
        Assert.Equal(DiskHealthState.Unknown, afterGood.Overall);
    }

    [Theory]
    [InlineData("expired-run")]
    [InlineData("expired-mtime")]
    [InlineData("no-run")]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("smart-unavailable")]
    public void Unknown_never_clears_a_failing_disk(string name)
    {
        var failing = Read(Queries(smart: SmartFailed), at: Now.AddMinutes(-5));
        Assert.Equal(DiskHealthState.DegradedOrFailing, failing.Overall);

        var next = Read(Case(name), previous: failing);

        Assert.NotEqual(DiskHealthState.Healthy, next.Overall);
        Assert.Equal(DiskHealthState.Unknown, Item(next, DiskHealthKinds.Smart, "/dev/sda").State);
    }

    [Theory]
    [InlineData("status-error")]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("collector-failed")]
    public void Unknown_never_clears_a_degraded_array(string name)
    {
        var degraded = Read(Queries(md: MdFailedDisk), at: Now.AddMinutes(-5));
        Assert.Equal(DiskHealthState.DegradedOrFailing, degraded.Overall);

        var next = Read(Case(name), previous: degraded);

        Assert.NotEqual(DiskHealthState.Healthy, next.Overall);
        Assert.Equal(DiskHealthState.Unknown, Item(next, DiskHealthKinds.Md, "md0").State);
    }

    [Fact]
    public void SMART_not_available_or_not_enabled_on_a_disk_is_Unknown_not_Healthy()
    {
        var r = Read(Case("smart-unavailable"));
        Assert.Equal("SMART is not available on this disk", Item(r, DiskHealthKinds.Smart, "/dev/sda").Reason);
        Assert.Equal(DiskHealthState.Unknown, r.Overall);
    }

    // ---------------------------------------------------------------------------------------------- names and queries

    [Fact]
    public void A_label_value_from_the_host_is_display_escaped()
    {
        Assert.Equal("sda", DiskHealthParser.DisplayName("sd\u0007a\r\n"));
        Assert.Equal("evil", DiskHealthParser.DisplayName("‮evil"));
        Assert.Equal("?", DiskHealthParser.DisplayName("\u0000"));
        var longName = DiskHealthParser.DisplayName(new string('x', 500));
        Assert.Equal(64, longName.Length);
        Assert.EndsWith("…", longName);

        var hostile = MdHealthy.Replace("\"device\":\"md0\"", "\"device\":\"md0\\u001b[31m<script>\"");
        var r = Read(Queries(md: hostile));
        var md = Assert.Single(r.Items, i => i.Kind == DiskHealthKinds.Md);
        Assert.DoesNotContain('\u001b', md.Name);
    }

    [Fact]
    public void The_six_queries_select_on_the_server_id_and_escape_it()
    {
        var queries = DiskHealthQueries.For("bad\"wolf");

        Assert.Equal(6, queries.Count);
        Assert.All(queries, q => Assert.Contains("server=\"bad\\\"wolf\"", q));
        Assert.Contains(queries, q => q.StartsWith("smartmon_smartctl_run{", StringComparison.Ordinal));
        Assert.Contains(queries, q => q.StartsWith("node_textfile_mtime_seconds{", StringComparison.Ordinal) && q.Contains("smartmon\\\\.prom"));
        Assert.Contains(queries, q => q.Contains("node_scrape_collector_success") && q.Contains("mdadm|zfs"));
    }

    // ---------------------------------------------------------------------------------------------- the query client

    private static PrometheusQueryClient Client(StubHttpHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<PrometheusQueryClient>.Instance);

    [Fact]
    public async Task The_client_asks_the_query_endpoint_and_returns_labelled_samples()
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(StubHttpHandler.Json(SmartHealthy)));

        var result = await Client(handler).QueryAsync("http://100.64.0.1:8428/", "smartmon_device_smart_healthy{server=\"badwolf\"}", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(6, result.Samples.Count);
        Assert.Equal("/dev/sda", result.Samples[0].Label("disk"));
        Assert.InRange((result.Samples[0].SampleUtc - new DateTime(2026, 11, 5, 12, 0, 0, 123, DateTimeKind.Utc)).Duration(),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        var url = Assert.Single(handler.Requests);
        Assert.StartsWith("http://100.64.0.1:8428/api/v1/query?query=", url);
        Assert.Contains(Uri.EscapeDataString("server=\"badwolf\""), url);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "<html>oops</html>", "HTTP 500")]
    [InlineData(HttpStatusCode.BadRequest, StatusError, "query refused (bad_data)")]
    [InlineData(HttpStatusCode.OK, Truncated, "malformed response (not JSON)")]
    public async Task A_failed_answer_is_a_failed_result_not_an_exception(HttpStatusCode status, string body, string error)
    {
        var handler = new StubHttpHandler((_, _) => Task.FromResult(StubHttpHandler.Json(body, status)));

        var result = await Client(handler).QueryAsync("http://tsdb:8428", "up", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(error, result.Error);
    }

    [Fact]
    public async Task An_unreachable_endpoint_or_a_bad_URL_is_a_failed_result_without_the_endpoint_in_it()
    {
        var handler = new StubHttpHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var result = await Client(handler).QueryAsync("http://user:secret@tsdb:8428", "up", CancellationToken.None);
        Assert.False(result.Success);
        Assert.DoesNotContain("secret", result.Error);

        Assert.False((await Client(handler).QueryAsync("file:///etc/passwd", "up", CancellationToken.None)).Success);
        Assert.False((await Client(handler).QueryAsync("", "up", CancellationToken.None)).Success);
    }

    [Fact]
    public async Task The_callers_cancellation_reaches_the_request_and_is_thrown_not_turned_into_a_result()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpHandler.Json(Empty);
        });
        using var cts = new CancellationTokenSource();

        var query = Client(handler).QueryAsync("http://tsdb:8428", "up", cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void The_client_bounds_each_query_to_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), PrometheusQueryClient.Timeout);
    }

    private static string FixTrailingComma(string json) => System.Text.RegularExpressions.Regex.Replace(json, @",\s*\]", "]");
}

using Whiskers.Models;
using Whiskers.Services.Observability;

namespace Whiskers.Tests;

/// <summary>
/// The last known reading per server (2026-08-28).
///
/// <para>The background loops ask every server every 30 seconds. The dashboard then asked again from
/// scratch and rendered two seconds of "nicht erreichbar" while it waited — for an answer the building
/// already had. This cache keeps it.</para>
///
/// <para>Two properties decide whether that is an improvement or a new way to lie. It has to stay
/// <b>bounded</b>: one entry per server, expired entries dropped, servers that no longer exist swept out.
/// And the age has to travel with the value — a reading from thirty seconds ago and one from now look
/// identical on screen, and only one of them is evidence about the server right now.</para>
/// </summary>
public class FleetSnapshotCacheTests
{
    private static readonly DateTime Start = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A hand-wound clock. Small enough not to be worth a NuGet dependency, and it makes the
    /// expiry tests instant instead of real-time.</summary>
    private sealed class TestTime(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static (FleetSnapshotCache Cache, TestTime Time) New()
    {
        var time = new TestTime(Start);
        return (new FleetSnapshotCache(time), time);
    }

    private static ServerSystemInfo Info(string id, bool reachable = true) => new()
    {
        ServerId = id, ServerName = id, IsReachable = reachable,
        OperatingSystem = "Ubuntu 24.04", CpuCount = 4, MemoryTotalBytes = 16_000_000_000
    };

    [Fact]
    public void A_stored_reading_comes_back_with_its_age()
    {
        // The age is the whole difference between a useful cache and a quiet lie.
        var (cache, time) = New();
        cache.Put("host-a", Info("host-a"));

        time.Advance(TimeSpan.FromSeconds(42));
        var cached = cache.Get("host-a");

        Assert.NotNull(cached);
        Assert.Equal(42, cached!.Age.TotalSeconds, precision: 0);
        Assert.Equal("Ubuntu 24.04", cached.Info.OperatingSystem);
    }

    [Fact]
    public void A_reading_that_has_aged_out_is_not_served_at_all()
    {
        // Past ten minutes — twenty missed metrics cycles — the number says more about the outage than about
        // the server. Handing it over would dress an outage as a healthy fleet.
        var (cache, time) = New();
        cache.Put("host-a", Info("host-a"));

        time.Advance(IFleetSnapshotCache.MaxAge + TimeSpan.FromSeconds(1));

        Assert.Null(cache.Get("host-a"));
        Assert.Empty(cache.GetAll());
    }

    [Fact]
    public void Expired_entries_are_actually_dropped_and_not_just_hidden()
    {
        // "Regelmäßiges Löschen älterer Werte" — a cache that only hides what it will not serve grows for as
        // long as the process lives.
        var (cache, time) = New();
        cache.Put("gone", Info("gone"));

        time.Advance(IFleetSnapshotCache.MaxAge + TimeSpan.FromMinutes(1));
        cache.Put("fresh", Info("fresh"));      // a write sweeps

        Assert.Single(cache.GetAll());
        Assert.True(cache.GetAll().ContainsKey("fresh"));
    }

    [Fact]
    public void One_entry_per_server_no_matter_how_often_it_is_written()
    {
        // The size is the fleet, not the uptime.
        var (cache, time) = New();
        for (var i = 0; i < 200; i++)
        {
            cache.Put("host-a", Info("host-a"));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        cache.Put("host-a", Info("host-a"));   // and the newest write wins

        Assert.Single(cache.GetAll());
        Assert.Equal(0, cache.Get("host-a")!.Age.TotalSeconds, precision: 0);
    }

    [Fact]
    public void A_server_that_no_longer_exists_is_swept_out()
    {
        var (cache, _) = New();
        cache.Put("host-a", Info("host-a"));
        cache.Put("deleted-in-july", Info("deleted-in-july"));

        var removed = cache.PruneRemovedServers(new HashSet<string> { "host-a" });

        Assert.Equal(1, removed);
        Assert.Null(cache.Get("deleted-in-july"));
        Assert.NotNull(cache.Get("host-a"));
    }

    [Fact]
    public void An_empty_server_list_clears_nothing()
    {
        // Same rule as the CVE store learned on 2026-08-27: an empty list is far more likely to mean "could
        // not be read" than "there are no servers", and acting on it blanks the dashboard over a config hiccup.
        var (cache, _) = New();
        cache.Put("host-a", Info("host-a"));

        Assert.Equal(0, cache.PruneRemovedServers(new HashSet<string>()));
        Assert.NotNull(cache.Get("host-a"));
    }

    [Fact]
    public void Server_ids_are_matched_without_regard_to_case()
    {
        // servers.json is hand-edited; "Host-B" and "host-b" are one machine.
        var (cache, _) = New();
        cache.Put("Host-B", Info("Host-B"));

        Assert.NotNull(cache.Get("host-b"));
        Assert.Equal(0, cache.PruneRemovedServers(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "host-b" }));
    }

    [Fact]
    public void A_clock_that_jumped_backwards_never_yields_a_negative_age()
    {
        // Would render as "from the future" and sort wrong everywhere it is shown.
        var (cache, time) = New();
        cache.Put("host-a", Info("host-a"));

        time.Advance(TimeSpan.FromSeconds(-30));

        Assert.True(cache.Get("host-a")!.Age >= TimeSpan.Zero);
    }
}

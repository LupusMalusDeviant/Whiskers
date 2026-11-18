using Whiskers.Models.Cve;
using Whiskers.Services.Cve;

namespace Whiskers.Tests;

public class CveFindingsStorePruneTests
{
    // A non-existent temp persist path → the store starts empty (no /app/data pollution).
    private static CveFindingsStore NewStore()
        => new(persistPath: Path.Combine(Path.GetTempPath(), $"cve-prune-{Guid.NewGuid():N}.json"));

    private static CveScanResult Result(string serverId, string? containerId, CveSource source)
        => new() { ServerId = serverId, ContainerId = containerId, Source = source };

    [Fact]
    public void PrunesPhantomContainers_KeepsOsAndLive()
    {
        var store = NewStore();
        store.Set(Result("s", null, CveSource.Os));            // s:os
        store.Set(Result("s", "c1", CveSource.Container));     // s:c1  (still present)
        store.Set(Result("s", "c2", CveSource.Container));     // s:c2  (phantom — recreated away)
        store.Set(Result("other", "c9", CveSource.Container)); // different server, must be untouched

        var removed = store.PruneServer("s", new HashSet<string> { CveFindingsStore.Key("s", "c1") });

        Assert.Equal(1, removed);
        Assert.NotNull(store.Get("s", null));      // OS target kept
        Assert.NotNull(store.Get("s", "c1"));      // live container kept
        Assert.Null(store.Get("s", "c2"));         // phantom removed
        Assert.NotNull(store.Get("other", "c9"));  // other server untouched
    }

    [Fact]
    public void EmptyLiveSet_RemovesContainersButKeepsOs()
    {
        var store = NewStore();
        store.Set(Result("s", null, CveSource.Os));
        store.Set(Result("s", "c1", CveSource.Container));

        var removed = store.PruneServer("s", new HashSet<string>());

        Assert.Equal(1, removed);
        Assert.NotNull(store.Get("s", null)); // OS target never pruned
        Assert.Null(store.Get("s", "c1"));
    }

    // ---- results left behind by a server that was removed from the fleet (2026-08-27) -------------------

    [Fact]
    public void RemovedServer_LosesEverythingIncludingItsOsEntry()
    {
        // The case found in the field: a server deleted in July was still reporting 419 vulnerabilities six
        // weeks later. PruneServer only ever runs for servers that still exist, so nothing had ever looked at
        // these. The OS entry goes too — it is protected in PruneServer because a container listing says
        // nothing about the host, but a server that is gone has no host left to protect.
        var store = NewStore();
        store.Set(Result("gone", null, CveSource.Os));
        store.Set(Result("gone", "c1", CveSource.Container));
        store.Set(Result("gone", "c2", CveSource.Container));
        store.Set(Result("live", null, CveSource.Os));
        store.Set(Result("live", "c1", CveSource.Container));

        var removed = store.PruneRemovedServers(new HashSet<string> { "live" });

        Assert.Equal(3, removed);
        Assert.Null(store.Get("gone", null));
        Assert.Null(store.Get("gone", "c1"));
        Assert.Null(store.Get("gone", "c2"));
        Assert.NotNull(store.Get("live", null));
        Assert.NotNull(store.Get("live", "c1"));
    }

    [Fact]
    public void DisabledIsNotRemoved()
    {
        // Switching a server off is not deleting it. A fortnight of maintenance must not cost its findings —
        // and, through them, the first-seen ages that say how long each one has been open. The caller passes
        // every CONFIGURED server, enabled or not, and this test is what holds it to that.
        var store = NewStore();
        store.Set(Result("paused-for-maintenance", "c1", CveSource.Container));

        var removed = store.PruneRemovedServers(
            new HashSet<string> { "paused-for-maintenance", "other" });

        Assert.Equal(0, removed);
        Assert.NotNull(store.Get("paused-for-maintenance", "c1"));
    }

    [Fact]
    public void AnEmptyServerListDeletesNothingAtAll()
    {
        // The dangerous direction. "No servers configured" and "the server list could not be read" arrive as
        // the same empty set, and acting on it would wipe every finding for every server — after which the
        // next scan reports the entire fleet as newly vulnerable and notifies about all of it. Doing nothing
        // is recoverable; this is not.
        var store = NewStore();
        store.Set(Result("s1", null, CveSource.Os));
        store.Set(Result("s1", "c1", CveSource.Container));
        store.Set(Result("s2", "c1", CveSource.Container));

        var removed = store.PruneRemovedServers(new HashSet<string>());

        Assert.Equal(0, removed);
        Assert.Equal(3, store.GetAll().Count);
    }

    [Fact]
    public void ServerIdsAreMatchedWithoutRegardToCase()
    {
        // servers.json is hand-edited. A server surviving or vanishing must not turn on the capitalisation
        // somebody happened to type.
        var store = NewStore();
        store.Set(Result("Host-B", "c1", CveSource.Container));

        Assert.Equal(0, store.PruneRemovedServers(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "host-b" }));
        Assert.NotNull(store.Get("Host-B", "c1"));
    }

    [Fact]
    public void OnceTheServerIsGoneItsKeysNoLongerCountAsLive()
    {
        // The link back to the age table, which is where this was first noticed. The first-seen rows are only
        // pruned when their identity key is absent from the live set, and that set is built from whatever the
        // store still holds — so as long as the dead server's results stayed, its ages could never expire.
        // This asserts the chain, not just the removal.
        var store = NewStore();
        var goneResult = Result("gone", "c1", CveSource.Container);
        goneResult.Findings.Add(new CveFinding
        {
            ServerId = "gone", Source = CveSource.Container, ContainerId = "c1", ContainerName = "ghostunnel",
            CveId = "CVE-2026-1111", Package = "openssl"
        });
        store.Set(goneResult);

        var beforeKeys = store.GetAll().SelectMany(r => r.Findings).Select(f => f.IdentityKey).ToHashSet();
        Assert.NotEmpty(beforeKeys);

        store.PruneRemovedServers(new HashSet<string> { "live" });

        var afterKeys = store.GetAll().SelectMany(r => r.Findings).Select(f => f.IdentityKey).ToHashSet();
        Assert.Empty(afterKeys);
    }
}

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
}

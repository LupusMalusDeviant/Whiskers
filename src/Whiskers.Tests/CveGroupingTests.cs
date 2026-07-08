using Whiskers.Models.Cve;
using Whiskers.Services.Cve;

namespace Whiskers.Tests;

public class CveGroupingTests
{
    private static CveScanResult ContainerResult(string serverId, string containerId, string image, params CveFinding[] f)
    {
        var r = new CveScanResult { ServerId = serverId, Source = CveSource.Container, ContainerId = containerId, ContainerName = containerId, Image = image };
        r.Findings.AddRange(f);
        return r;
    }

    private static CveFinding Vuln(string serverId, string containerId, string cve, CveSeverity sev, string pkg, string? fix = null) => new()
    {
        ServerId = serverId,
        Source = CveSource.Container,
        ContainerId = containerId,
        ContainerName = containerId,
        Package = pkg,
        CveId = cve,
        Severity = sev,
        FixedVersion = fix,
    };

    [Fact]
    public void Same_cve_on_two_servers_becomes_one_group_with_two_instances()
    {
        var store = new CveFindingsStore();
        store.Set(ContainerResult("s1", "web", "nginx:1", Vuln("s1", "web", "CVE-2024-1", CveSeverity.High, "openssl", "3.1")));
        store.Set(ContainerResult("s2", "web", "nginx:1", Vuln("s2", "web", "CVE-2024-1", CveSeverity.High, "openssl", "3.1")));

        var groups = store.BuildGroups(
            new Dictionary<string, DateTime>(),
            new Dictionary<string, string> { ["s1"] = "Server1", ["s2"] = "Server2" });

        var g = Assert.Single(groups);
        Assert.Equal("CVE-2024-1", g.CveId);
        Assert.Equal(2, g.InstanceCount);
        Assert.Equal(2, g.ServerCount);
        Assert.True(g.HasFix);
    }

    [Fact]
    public void Group_takes_the_worst_severity_across_instances()
    {
        var store = new CveFindingsStore();
        store.Set(ContainerResult("s1", "a", "img", Vuln("s1", "a", "CVE-X", CveSeverity.Medium, "p")));
        store.Set(ContainerResult("s2", "b", "img", Vuln("s2", "b", "CVE-X", CveSeverity.Critical, "p")));

        var g = Assert.Single(store.BuildGroups(new Dictionary<string, DateTime>(), new Dictionary<string, string>()));
        Assert.Equal(CveSeverity.Critical, g.Severity);
    }

    [Fact]
    public void FirstSeen_uses_the_earliest_persisted_timestamp()
    {
        var store = new CveFindingsStore();
        var f = Vuln("s1", "a", "CVE-Y", CveSeverity.High, "p");
        store.Set(ContainerResult("s1", "a", "img", f));
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var g = Assert.Single(store.BuildGroups(
            new Dictionary<string, DateTime> { [f.IdentityKey] = old },
            new Dictionary<string, string>()));

        Assert.Equal(old, g.FirstSeenUtc);
        Assert.True(g.OpenFor.TotalDays > 365);
    }

    [Fact]
    public void Distinct_cves_stay_separate()
    {
        var store = new CveFindingsStore();
        store.Set(ContainerResult("s1", "a", "img",
            Vuln("s1", "a", "CVE-1", CveSeverity.High, "p"),
            Vuln("s1", "a", "CVE-2", CveSeverity.Low, "q")));

        var groups = store.BuildGroups(new Dictionary<string, DateTime>(), new Dictionary<string, string>());
        Assert.Equal(2, groups.Count);
        // Sorted worst-severity first.
        Assert.Equal("CVE-1", groups[0].CveId);
    }
}

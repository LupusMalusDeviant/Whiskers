using ServerWatch.Services.Cve;

namespace ServerWatch.Tests;

public class OsCveScannerTests
{
    // A realistic apt changelog: newest stanza first, older stanzas below.
    private const string CurlChangelog = """
        curl (8.5.0-2ubuntu10.10) noble-security; urgency=medium

          * SECURITY UPDATE: current fixes
            - CVE-2026-8286: something recent
            - CVE-2026-8458: something else recent

         -- Ubuntu Developers <x@ubuntu.com>  Mon, 01 Jun 2026 10:00:00 +0000

        curl (8.5.0-2ubuntu10.9) noble-security; urgency=medium

          * SECURITY UPDATE: already installed
            - CVE-2020-8169: old, already fixed in installed version
            - CVE-2005-4077: ancient, fixed two decades ago

         -- Ubuntu Developers <x@ubuntu.com>  Tue, 01 Jan 2019 10:00:00 +0000
        """;

    [Fact]
    public void Only_returns_cves_from_stanzas_newer_than_installed()
    {
        var cves = OsCveScanner.ExtractNewCveIds(CurlChangelog, "8.5.0-2ubuntu10.9");

        Assert.Equal(new[] { "CVE-2026-8286", "CVE-2026-8458" }, cves);
        // The historical CVEs already fixed in the installed version must NOT be attributed.
        Assert.DoesNotContain("CVE-2020-8169", cves);
        Assert.DoesNotContain("CVE-2005-4077", cves);
    }

    [Fact]
    public void Strips_epoch_when_comparing_versions()
    {
        var log = """
            foo (2:1.2.4) noble-security; urgency=medium
              * fix
                - CVE-2026-1111: new
             -- x  Mon, 01 Jun 2026 10:00:00 +0000

            foo (2:1.2.3) noble-security; urgency=medium
              * old
                - CVE-2019-2222: already installed
             -- x  Mon, 01 Jan 2019 10:00:00 +0000
            """;

        // apt reports the installed version without the epoch here — must still match.
        var cves = OsCveScanner.ExtractNewCveIds(log, "1.2.3");

        Assert.Equal(new[] { "CVE-2026-1111" }, cves);
    }

    [Fact]
    public void No_new_stanza_returns_empty()
    {
        // Installed version is already the newest stanza — nothing newer to attribute.
        var cves = OsCveScanner.ExtractNewCveIds(CurlChangelog, "8.5.0-2ubuntu10.10");
        Assert.Empty(cves);
    }

    [Fact]
    public void Missing_installed_version_is_bounded_by_backstop()
    {
        // If the installed version never appears, the 5-stanza backstop prevents runaway
        // over-attribution of a package's entire CVE history.
        var sb = new System.Text.StringBuilder();
        for (int i = 20; i >= 1; i--)
            sb.AppendLine($"pkg (1.0.{i}) noble-security; urgency=medium")
              .AppendLine($"  * fix\n    - CVE-2026-{1000 + i}: entry")
              .AppendLine(" -- x  Mon, 01 Jun 2026 10:00:00 +0000\n");

        var cves = OsCveScanner.ExtractNewCveIds(sb.ToString(), "0.0.0-not-present");

        // Bounded to the 5 newest stanzas, not all 20.
        Assert.True(cves.Count <= 5, $"expected <=5 but got {cves.Count}");
    }
}

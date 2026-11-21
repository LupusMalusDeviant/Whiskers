using Whiskers.Mcp.Tools;
using Whiskers.Services.ImageUpdate;

namespace Whiskers.Tests;

/// <summary>
/// How the update-risk verdict is worded (GAP-6, 2026-08-28).
///
/// <para>The assessment is only useful if the sentence somebody reads carries its own limits. A configuration
/// diff cannot see a schema migration or a newly required environment variable, so "no findings" must never
/// arrive looking like "safe to update" — and an assessment that failed outright must not arrive looking like
/// an assessment at all.</para>
/// </summary>
public class UpdateRiskReportRenderTests
{
    private static UpdateRisk Risk(RiskLevel level, int? closed = null, params RiskFinding[] findings)
        => new(level, findings, closed, ["Changes inside the application, such as a database migration."]);

    [Fact]
    public void A_failed_assessment_says_it_is_not_a_verdict()
    {
        // THE trap this guards. An operator reading "Update risk: ..." with nothing listed would conclude
        // there is nothing to worry about, when in fact nothing was checked.
        var text = UpdateRiskTools.Render(new UpdateRiskReport(
            "local", "idp", "img:1", false, null, null, null, "host unreachable"));

        Assert.Contains("ASSESSMENT FAILED", text);
        Assert.Contains("This is not a verdict", text);
        Assert.Contains("Nothing was checked", text);
    }

    [Fact]
    public void A_clean_verdict_still_prints_what_it_could_not_see()
    {
        // "Low risk" is a statement about what is detectable, and the text has to say so itself — it will be
        // read by somebody deciding whether to touch production at eleven at night.
        var text = UpdateRiskTools.Render(new UpdateRiskReport(
            "local", "caddy", "caddy:2.8", true, "sha256:aaa", "sha256:bbb",
            Risk(RiskLevel.None, closed: 12), null));

        Assert.Contains("NOT covered by this assessment", text);
        Assert.Contains("does not mean safe", text);
        Assert.Contains("Closes 12 vulnerabilities", text);
    }

    [Fact]
    public void An_unscanned_candidate_reports_the_benefit_as_unknown_not_as_zero()
    {
        // Reporting "closes 0" would argue against an update on the strength of a measurement nobody made.
        var text = UpdateRiskTools.Render(new UpdateRiskReport(
            "local", "caddy", "caddy:2.8", true, "sha256:aaa", "sha256:bbb",
            Risk(RiskLevel.Low, closed: null), null));

        Assert.Contains("Benefit unknown", text);
        Assert.DoesNotContain("Closes 0", text);
    }

    [Fact]
    public void Findings_are_listed_worst_first_with_the_reason()
    {
        // Somebody skimming reads the first line. It had better be the one that breaks things.
        var text = UpdateRiskTools.Render(new UpdateRiskReport(
            "local", "app", "app:2", true, "sha256:aaa", "sha256:bbb",
            Risk(RiskLevel.High, 3,
                new RiskFinding(RiskLevel.Low, "New port 9000 exposed", "Nothing breaks by itself."),
                new RiskFinding(RiskLevel.High, "Runs as a different user", "Existing volumes keep their owner.")),
            null));

        var high = text.IndexOf("Runs as a different user", StringComparison.Ordinal);
        var low = text.IndexOf("New port 9000", StringComparison.Ordinal);
        Assert.True(high < low, "the high finding must be listed before the low one");
        Assert.Contains("Existing volumes keep their owner", text);
    }

    [Fact]
    public void Nothing_newer_means_there_is_nothing_to_decide()
    {
        var text = UpdateRiskTools.Render(new UpdateRiskReport(
            "local", "caddy", "caddy:2.8", false, "sha256:aaa", "sha256:aaa", null, null));

        Assert.Contains("Already on the newest image", text);
        Assert.DoesNotContain("Risk:", text);
    }

    [Theory]
    [InlineData("ghcr.io/owner/app:1.2", "1.2")]
    [InlineData("registry:5000/app:2.0", "2.0")]     // registry port must not be read as the tag
    [InlineData("nginx", "latest")]                   // no tag means latest, and latest is a finding
    [InlineData("app@sha256:abc", null)]              // pinned by digest: no tag semantics at all
    public void The_tag_is_read_out_of_the_reference_without_tripping_over_a_registry_port(
        string imageRef, string? expected)
    {
        Assert.Equal(expected, UpdateRiskService.TagOf(imageRef));
    }
}

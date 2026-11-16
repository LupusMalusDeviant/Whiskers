using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.ImageUpdate;

namespace Whiskers.Tests;

/// <summary>assess_update_risk compares the candidate with the image the container RUNS, also on a second call, when an
/// earlier assessment already moved the tag on (review 1.0.0, Data).</summary>
public sealed class UpdateRiskServiceTests
{
    private const string OldId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static ImageContract Contract(string user) =>
        new([], ["run"], user, new HashSet<string>(), new HashSet<string>(), "/", false, "linux");

    /// <summary>The state after a first assessment: the tag names the pulled candidate, the container runs the old id.</summary>
    private static FakeDocker TagMovedOn(string listedImage, List<string> pulled)
    {
        var docker = new FakeDocker(new ContainerInfo
        {
            Id = "c1", Name = "web", Image = listedImage, ImageId = OldId, ServerId = "local",
        })
        {
            Pull = image => { pulled.Add(image); return Task.CompletedTask; },
            Contract = image => Contract(image == OldId ? "root" : "nobody"),
            Digest = image => Task.FromResult<string?>(image == OldId ? "sha256:d-old" : "sha256:d-new"),
            ConfiguredImage = _ => "nginx:1.27",
        };
        return docker;
    }

    private static UpdateRiskService Service(FakeDocker docker) =>
        new(docker, null!, null!, NullLogger<UpdateRiskService>.Instance);

    [Fact]
    public async Task A_second_assessment_still_compares_the_candidate_with_the_running_image()
    {
        var pulled = new List<string>();
        var report = await Service(TagMovedOn("nginx:1.27", pulled)).AssessAsync("local", "web", scanCandidate: false);

        Assert.Null(report.Error);
        Assert.True(report.UpdateAvailable);                 // the container still runs the old image
        Assert.Equal("sha256:d-old", report.CurrentDigest);
        Assert.Equal("sha256:d-new", report.CandidateDigest);
        Assert.Equal(["nginx:1.27"], pulled);
    }

    [Fact]
    public async Task A_container_listed_by_its_image_id_is_assessed_against_the_name_it_was_created_from()
    {
        var pulled = new List<string>();
        var report = await Service(TagMovedOn(OldId, pulled)).AssessAsync("local", "web", scanCandidate: false);

        Assert.Null(report.Error);
        Assert.Equal("nginx:1.27", report.ImageRef);
        Assert.Equal(["nginx:1.27"], pulled);                // never a "pull" of a bare image id
        Assert.True(report.UpdateAvailable);
        Assert.Equal("sha256:d-old", report.CurrentDigest);
    }
}

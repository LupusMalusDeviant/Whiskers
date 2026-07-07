using ServerWatch.Services.ImageUpdate;

namespace ServerWatch.Tests;

public class ImageUpdatePinTests
{
    [Theory]
    [InlineData("nginx@sha256:abcdef", true)]
    [InlineData("ghcr.io/owner/repo@sha256:deadbeef", true)]
    [InlineData("nginx", false)]
    [InlineData("nginx:1.25", false)]
    [InlineData("ghcr.io/owner/repo:v1", false)]
    [InlineData("registry:5000/app:tag", false)] // has a colon (port + tag) but is NOT digest-pinned
    public void IsDigestPinned(string image, bool expected)
        => Assert.Equal(expected, ImageUpdateChecker.IsDigestPinned(image));
}

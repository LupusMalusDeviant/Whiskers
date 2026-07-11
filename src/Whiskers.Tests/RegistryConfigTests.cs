using Whiskers.Services.Registries;

namespace Whiskers.Tests;

/// <summary>F8 — registry host matching for authenticated pulls (Docker reference conventions).</summary>
public class RegistryConfigTests
{
    [Theory]
    [InlineData("nginx", "docker.io")]                                  // official image
    [InlineData("nginx:1.27", "docker.io")]
    [InlineData("library/nginx", "docker.io")]                          // user/repo, no registry
    [InlineData("ghcr.io/org/app:1.0", "ghcr.io")]
    [InlineData("harbor.example.com:5000/proj/app", "harbor.example.com:5000")]
    [InlineData("localhost/dev-image", "localhost")]
    [InlineData("quay.io/prometheus/node-exporter:v1.8.2", "quay.io")]
    [InlineData("repo@sha256:abc", "docker.io")]
    [InlineData("", "docker.io")]
    public void Registry_host_follows_docker_reference_rules(string imageRef, string expected)
        => Assert.Equal(expected, RegistryConfigService.RegistryHostOf(imageRef));
}

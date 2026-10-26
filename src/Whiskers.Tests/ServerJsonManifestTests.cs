using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Whiskers.Tests;

/// <summary>
/// Keeps <c>server.json</c> (the official MCP Registry entry) in step with what a release actually ships.
///
/// <para>The registry accepts a publish only if the image named in <c>server.json</c> exists and carries the
/// <c>io.modelcontextprotocol.server.name</c> label with the same server name. A release that bumps the app
/// version but forgets <c>server.json</c> would advertise an image tag that does not exist; a renamed label would
/// fail the ownership check. Both drifts are silent until someone runs <c>mcp-publisher publish</c>.</para>
/// </summary>
public class ServerJsonManifestTests
{
    private static string Root => McpToolCatalogSnapshotTests.RepositoryRoot();

    private static JsonElement ServerJson()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "server.json")));
        return doc.RootElement.Clone();
    }

    private static string AppVersion() =>
        XDocument.Load(Path.Combine(Root, "src", "Whiskers", "Whiskers.csproj"))
            .Descendants("Version").Single().Value.Trim();

    [Fact]
    public void Version_and_image_tag_match_the_app_version()
    {
        var json = ServerJson();
        var version = AppVersion();

        Assert.Equal(version, json.GetProperty("version").GetString());
        var package = Assert.Single(json.GetProperty("packages").EnumerateArray());
        Assert.Equal("oci", package.GetProperty("registryType").GetString());
        Assert.Equal($"ghcr.io/lupusmalusdeviant/whiskers:{version}", package.GetProperty("identifier").GetString());
    }

    [Fact]
    public void Dockerfile_label_proves_ownership_of_the_server_name()
    {
        var name = ServerJson().GetProperty("name").GetString();
        var dockerfile = File.ReadAllText(Path.Combine(Root, "Dockerfile"));

        var match = Regex.Match(dockerfile, "^LABEL io\\.modelcontextprotocol\\.server\\.name=\"([^\"]+)\"",
            RegexOptions.Multiline);
        Assert.True(match.Success, "Dockerfile has no io.modelcontextprotocol.server.name label");
        Assert.Equal(name, match.Groups[1].Value);
    }

    [Fact]
    public void Fields_satisfy_the_registry_limits()
    {
        var json = ServerJson();

        Assert.Matches("^io\\.github\\.lupusmalusdeviant/[a-zA-Z0-9._-]+$", json.GetProperty("name").GetString()!);
        Assert.InRange(json.GetProperty("description").GetString()!.Length, 1, 100);

        var transport = json.GetProperty("packages")[0].GetProperty("transport");
        Assert.Equal("streamable-http", transport.GetProperty("type").GetString());
        Assert.EndsWith("/mcp", transport.GetProperty("url").GetString());
    }
}

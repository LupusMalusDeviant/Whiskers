using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Whiskers.Mcp;
using Whiskers.Modules;

namespace Whiskers.Tests;

/// <summary>
/// Asks the running server what it actually serves (Plan-0013 WP3.3).
///
/// <para>Every other test in this package inspects the code: attributes, module lists, a hand-built
/// <c>ServiceCollection</c>. None of them would have caught the failure this package exists for. From 0.12.0 to
/// 0.13.0 the shipped MCP server answered <c>tools/list</c> with nothing at all, across several releases, because
/// one argument bound to the wrong <c>WithTools</c> overload in the real startup path. The code looked right; the
/// server served nothing.</para>
///
/// <para>So this test boots the real application through <see cref="WebApplicationFactory{T}"/>, speaks the real
/// MCP handshake over the real endpoint, and compares the tool list the server hands back against the catalog.
/// It is the only test here that would have failed in 0.12.0.</para>
/// </summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class McpServedSurfaceTests
{
    private static IReadOnlyList<IWhiskersModule> EnabledModules() =>
        ModuleCatalog.DiscoverEnabled(new ConfigurationBuilder().Build());

    [Fact]
    public async Task The_running_server_serves_exactly_the_catalogued_tools()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        // Read eagerly in Program.cs, so it has to be a real env var — same constraint as BootMatrixTests.
        var previousDataDir = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        try
        {
            using var bootSettings = BootSettings.Apply(BootSettings.AuthDisabled);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(BootSettings.AuthDisabled));
                });

            using var client = factory.CreateClient();

            var sessionId = await InitializeAsync(client);
            var served = await ListToolsAsync(client, sessionId);

            var catalogued = McpToolLevelCatalog.Declarations(EnabledModules())
                .Select(d => d.ToolName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            // The assertion that matters: not "more than N", not "the code declares them" — the wire answered.
            Assert.Equal(catalogued, served);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    internal static async Task<string> InitializeAsync(HttpClient client)
    {
        var response = await PostAsync(client, sessionId: null, """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
              "protocolVersion":"2024-11-05",
              "capabilities":{},
              "clientInfo":{"name":"whiskers-tests","version":"1.0"}}}
            """);

        Assert.True(response.IsSuccessStatusCode,
            $"MCP initialize failed with {(int)response.StatusCode} {response.StatusCode}: " +
            await response.Content.ReadAsStringAsync());

        var sessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault()
            : null;

        Assert.False(string.IsNullOrWhiteSpace(sessionId),
            "the server returned no Mcp-Session-Id — the transport contract changed, and every later call in " +
            "this test would silently talk to a fresh session");

        // The spec requires the initialized notification before normal requests are accepted.
        using var notified = await PostAsync(client, sessionId,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.True(notified.IsSuccessStatusCode, $"notifications/initialized failed: {(int)notified.StatusCode}");

        return sessionId!;
    }

    private static async Task<List<string>> ListToolsAsync(HttpClient client, string sessionId)
    {
        using var response = await PostAsync(client, sessionId,
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        Assert.True(response.IsSuccessStatusCode,
            $"tools/list failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var payload = ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(payload);

        // A JSON-RPC error here is the 0.12.0 signature: the server answers, but with -32601 (method not found)
        // because no tools were ever registered. Report it as itself rather than as a parse failure.
        Assert.False(document.RootElement.TryGetProperty("error", out var error),
            $"the server answered tools/list with an error: {error}");

        return document.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    internal static Task<HttpResponseMessage> PostAsync(HttpClient client, string? sessionId, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null) request.Headers.Add("Mcp-Session-Id", sessionId);
        return client.SendAsync(request);
    }

    /// <summary>The transport may answer as plain JSON or as a single SSE event; take the payload from either.</summary>
    internal static string ExtractJsonRpcPayload(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{')) return trimmed;

        var data = body
            .Split('\n')
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => l["data:".Length..].Trim())
            .ToList();

        Assert.True(data.Count > 0, $"neither JSON nor SSE data in the MCP response:\n{body}");
        return string.Concat(data);
    }
}

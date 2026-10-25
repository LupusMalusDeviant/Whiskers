using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Mcp;

namespace Whiskers.Tests;

/// <summary>The prompts and resources the running server advertises and serves, over the real /mcp endpoint.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class McpPromptsAndResourcesTests
{
    [Fact]
    public async Task The_running_server_serves_prompts_and_resources()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-mcppr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var previousDataDir = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                        new List<KeyValuePair<string, string?>> { new("Auth:Disabled", "true") }));
                });
            using var client = factory.CreateClient();

            // serverInfo is what MCP clients and directories (LobeHub, Glama) display for this server.
            using (var init = await McpServedSurfaceTests.PostAsync(client, null, """
                       {"jsonrpc":"2.0","id":19,"method":"initialize","params":{"protocolVersion":"2025-06-18",
                        "capabilities":{},"clientInfo":{"name":"whiskers-tests","version":"1.0"}}}
                       """))
            {
                using var doc = JsonDocument.Parse(McpServedSurfaceTests.ExtractJsonRpcPayload(await init.Content.ReadAsStringAsync()));
                var info = doc.RootElement.GetProperty("result").GetProperty("serverInfo");
                Assert.Equal("whiskers", info.GetProperty("name").GetString());
                Assert.Equal(Whiskers.Utils.AppVersion.Display, info.GetProperty("version").GetString());
                Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("description").GetString()));
            }

            var session = await McpServedSurfaceTests.InitializeAsync(client);

            // Prompts: listed, and a prompt renders its argument and names real tools.
            using (var prompts = await RpcAsync(client, session, 20, "prompts/list", "{}"))
            {
                var names = prompts.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray()
                    .Select(p => p.GetProperty("name").GetString()).ToList();
                Assert.Equal(
                    new[] { "investigate_host_load", "plan_safe_update", "security_review_server", "triage_unhealthy_containers" },
                    names.OrderBy(n => n, StringComparer.Ordinal));
            }
            using (var prompt = await RpcAsync(client, session, 21, "prompts/get",
                       """{"name":"security_review_server","arguments":{"server":"web-01"}}"""))
            {
                var text = prompt.RootElement.GetProperty("result").GetProperty("messages")[0]
                    .GetProperty("content").GetProperty("text").GetString()!;
                Assert.Contains("\"web-01\"", text);
                Assert.Contains("get_server_cves", text);
            }

            // Resources: both listed; the catalog lists tools with levels.
            using (var resources = await RpcAsync(client, session, 22, "resources/list", "{}"))
            {
                var uris = resources.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray()
                    .Select(r => r.GetProperty("uri").GetString()).ToList();
                Assert.Contains("whiskers://tools/catalog", uris);
                Assert.Contains("whiskers://me/permissions", uris);
            }
            Assert.Contains("| `execute_command` | admin |", await ReadResourceAsync(client, session, 23, "whiskers://tools/catalog"));

            // The permissions resource reports a Read key's own, narrower view — not the admin's.
            var perms = factory.Services.GetRequiredService<IMcpPermissionService>();
            var readKey = await perms.AddApiKeyAsync("reader", McpPermissionLevels.Read);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readKey.Key);
            var keySession = await McpServedSurfaceTests.InitializeAsync(client);
            var mine = await ReadResourceAsync(client, keySession, 24, "whiskers://me/permissions");
            Assert.Contains("API key \"reader\"", mine);
            Assert.Contains("Scope: read", mine);
            Assert.Contains("`list_containers`", mine);
            Assert.DoesNotContain("`execute_command`", mine);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static async Task<string> ReadResourceAsync(HttpClient client, string session, int id, string uri)
    {
        using var doc = await RpcAsync(client, session, id, "resources/read", $$"""{"uri":"{{uri}}"}""");
        return doc.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
    }

    private static async Task<JsonDocument> RpcAsync(HttpClient client, string session, int id, string method, string paramsJson)
    {
        using var response = await McpServedSurfaceTests.PostAsync(client, session,
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{paramsJson}}}}""");
        Assert.True(response.IsSuccessStatusCode, $"{method} failed with {(int)response.StatusCode}");
        var doc = JsonDocument.Parse(McpServedSurfaceTests.ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync()));
        Assert.False(doc.RootElement.TryGetProperty("error", out var error), $"{method} answered with an error: {error}");
        return doc;
    }
}

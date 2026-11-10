using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Mcp;
using Whiskers.Models;
using Whiskers.Services.Mcp;

namespace Whiskers.Tests;

/// <summary>Contract 0016: the API-key principal's subject, and the SDK's session binding it feeds.</summary>
public sealed class McpBearerSubjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mcpsubject-{Guid.NewGuid():N}");

    private async Task<McpPermissionService> PermissionsAsync()
    {
        Directory.CreateDirectory(_dir);
        var svc = new McpPermissionService(NullLogger<McpPermissionService>.Instance, new DataPathOptions(_dir));
        await svc.InitializeAsync();
        return svc;
    }

    /// <summary>The only way to an empty or shared record id: someone edits the permission file by hand (the UI save
    /// matches keys by id and cannot rename one). Rewrites one key's id and loads the file into a fresh service.</summary>
    private async Task<McpPermissionService> HandEditedAsync(string fromId, string toId)
    {
        var paths = new DataPathOptions(_dir);
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(paths.McpPermissionsJson))!;
        var keys = (root["ApiKeys"] ?? root["apiKeys"])!.AsArray();
        var entry = keys.Single(k => ((string?)(k!["Id"] ?? k["id"])) == fromId)!.AsObject();
        entry[entry.ContainsKey("Id") ? "Id" : "id"] = toId;
        await File.WriteAllTextAsync(paths.McpPermissionsJson, root.ToJsonString());

        var svc = new McpPermissionService(NullLogger<McpPermissionService>.Instance, paths);
        await svc.InitializeAsync();
        return svc;
    }

    private static async Task<(HttpContext Context, bool NextRan)> RunAsync(IMcpPermissionService permissions, string key)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(permissions)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer " + key;
        var nextRan = false;
        await new McpBearerAuthMiddleware(_ => { nextRan = true; return Task.CompletedTask; }).InvokeAsync(context);
        return (context, nextRan);
    }

    [Fact]
    public async Task The_subject_is_the_key_record_id_and_carries_no_key_material()
    {
        var permissions = await PermissionsAsync();
        var created = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync("ci", McpPermissionLevels.Write));
        var stored = permissions.GetPermissionData().ApiKeys.Single(k => k.Id == created.Id);

        var (context, nextRan) = await RunAsync(permissions, created.Key!);

        Assert.True(nextRan);
        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.True(context.User.HasClaim("mcp-api-key", "true"));
        var subject = Assert.Single(context.User.FindAll(ClaimTypes.NameIdentifier));
        Assert.Equal(McpBearerAuthMiddleware.SubjectPrefix + created.Id, subject.Value);
        Assert.Equal(McpBearerAuthMiddleware.SubjectIssuer, subject.Issuer);

        // Not the secret, not a slice of it, not its hash — on any claim of the principal. (The 4-character hint is
        // covered by the exact-value check above: the subject is the prefix plus the record id and nothing else.)
        foreach (var claim in context.User.Claims)
        {
            Assert.DoesNotContain(created.Key!, claim.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(stored.KeyHash, claim.Value, StringComparison.OrdinalIgnoreCase);
            for (var i = 0; i + 8 <= created.Key!.Length; i++)
                Assert.DoesNotContain(created.Key.Substring(i, 8), claim.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task An_unknown_key_gets_no_principal_and_the_pipeline_decides_as_before()
    {
        var permissions = await PermissionsAsync();

        var (context, nextRan) = await RunAsync(permissions, new string('a', 64));

        Assert.True(nextRan);
        Assert.False(context.User.Identity?.IsAuthenticated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_key_without_a_record_id_is_refused_and_never_reaches_the_endpoint(string id)
    {
        var permissions = await PermissionsAsync();
        var created = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync("ci", McpPermissionLevels.Read));
        var reloaded = await HandEditedAsync(created.Id, id);
        Assert.NotNull(reloaded.ValidateKey(created.Key!)); // the key itself is still valid — only its id is gone

        var (context, nextRan) = await RunAsync(reloaded, created.Key!);

        Assert.False(nextRan);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(context.User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Two_keys_sharing_a_record_id_are_both_refused()
    {
        var permissions = await PermissionsAsync();
        var a = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync("a", McpPermissionLevels.Read));
        var b = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync("b", McpPermissionLevels.Admin));
        var reloaded = await HandEditedAsync(b.Id, a.Id);

        foreach (var key in new[] { a.Key!, b.Key! })
        {
            Assert.NotNull(reloaded.ValidateKey(key));
            var (context, nextRan) = await RunAsync(reloaded, key);
            Assert.False(nextRan);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Contract 0016 C3 over the real wire: a session belongs to the key that opened it.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class McpSessionBindingWireTests
{
    private const string ToolsList = """{"jsonrpc":"2.0","id":7,"method":"tools/list","params":{}}""";

    [Fact]
    public async Task A_session_opened_by_one_key_cannot_be_taken_over_by_another_key()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-mcpsession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var previousDataDir = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b => b.UseEnvironment("Development"));
            using var owner = factory.CreateClient();
            using var intruder = factory.CreateClient();

            var permissions = factory.Services.GetRequiredService<IMcpPermissionService>();
            var ownerKey = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync("owner", McpPermissionLevels.Read));
            // The intruder holds the stronger key: binding is per key, not per level.
            var intruderKey = await GatewayArrange.RunAsync("mcp.key_create", () => permissions.AddApiKeyAsync("intruder", McpPermissionLevels.Admin));
            owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerKey.Key);
            intruder.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", intruderKey.Key);

            var session = await McpServedSurfaceTests.InitializeAsync(owner);

            using (var stolen = await McpServedSurfaceTests.PostAsync(intruder, session, ToolsList))
                Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);

            // Its own requests on the session still work: the refusal did not end or rebind it.
            using var own = await McpServedSurfaceTests.PostAsync(owner, session, ToolsList);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            var payload = McpServedSurfaceTests.ExtractJsonRpcPayload(await own.Content.ReadAsStringAsync());
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            Assert.True(document.RootElement.TryGetProperty("result", out _), payload);

            // And the intruder's key on its own session works — it is refused the foreign session, not the endpoint.
            Assert.False(string.IsNullOrEmpty(await McpServedSurfaceTests.InitializeAsync(intruder)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

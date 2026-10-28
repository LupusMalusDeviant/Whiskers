using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Approvals;
using Whiskers.Services.Docker;
using Whiskers.Services.Guardrails;

namespace Whiskers.Tests;

/// <summary>INV-26 / PC-D8: policy enforcement does not depend on an optional module. With the Agent module switched
/// off, direct MCP calls are still guarded, a Confirm still raises an approval, and the pages that decide them exist.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class GuardrailModuleIndependenceTests
{
    /// <summary>No login, and the agent module off. Both are read while Program sets up the builder.</summary>
    private static readonly KeyValuePair<string, string?>[] Settings =
        [.. BootSettings.AuthDisabled, new("Features:agent:Enabled", "false")];

    [Fact]
    public async Task Disabling_the_agent_module_leaves_direct_mcp_calls_guarded()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-noagent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        var previousDataDir = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        try
        {
            using var bootSettings = BootSettings.Apply(Settings);   // read eagerly by Program
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Development");
                    b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(Settings));
                    b.ConfigureTestServices(s =>
                    {
                        s.AddSingleton<Whiskers.Services.Docker.IDockerService>(new FakeDocker(
                            new ContainerInfo { Id = "c-whiskers", Name = "whiskers" },
                            new ContainerInfo { Id = "c-web-1", Name = "web-1" }));
                        // The target resolver decides the server first (R1-06): a call without one acts on the default server.
                        s.AddSingleton<Whiskers.Services.ServerConfig.IServerConfigService>(new FakeServerConfig(
                            new ServerConfig { Id = "local", Name = "Local", IsDefault = true }));
                    });
                });
            using var client = factory.CreateClient();
            BootSettings.AssertBoot(factory.Services, Settings);

            // The policy services are core; the agent itself is gone.
            Assert.NotNull(factory.Services.GetService<IDirectMcpCallGuard>());
            Assert.NotNull(factory.Services.GetService<IApprovalStore>());
            Assert.Null(factory.Services.GetService<Whiskers.Services.Agent.IAgentService>());

            var session = await McpServedSurfaceTests.InitializeAsync(client);

            var denied = await CallAsync(client, session, 20, "restart_container", """{"containerId":"whiskers"}""");
            Assert.Contains("Blocked by guardrails", denied);

            var confirm = await CallAsync(client, session, 21, "restart_container", """{"containerId":"web-1"}""");
            Assert.Contains("Human approval required", confirm);
            Assert.Single(factory.Services.GetRequiredService<IApprovalStore>().GetPending());

            // The pages that decide approvals and edit the policy are core routes now (no "module disabled" notice).
            foreach (var page in new[] { "/approvals", "/guardrails" })
            {
                using var response = await client.GetAsync(page);
                Assert.True(response.IsSuccessStatusCode, $"{page} answered {(int)response.StatusCode}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> CallAsync(HttpClient client, string session, int id, string tool, string argsJson)
    {
        using var response = await McpServedSurfaceTests.PostAsync(client, session,
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{argsJson}}}}}""");
        Assert.True(response.IsSuccessStatusCode, $"tools/call failed with {(int)response.StatusCode}");
        return McpServedSurfaceTests.ExtractJsonRpcPayload(await response.Content.ReadAsStringAsync());
    }
}

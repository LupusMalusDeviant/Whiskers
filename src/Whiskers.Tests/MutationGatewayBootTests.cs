using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>The gateway and the scope guard in the real composition root, against the real audit table.</summary>
[Collection("WebAppBoot")] // serialized: the boot needs the process-wide WHISKERS_DATA_DIR env var
public class MutationGatewayBootTests
{
    [Fact]
    public async Task The_running_app_guards_mutating_services_and_records_gateway_decisions()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "whiskers-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
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
            using var client = factory.CreateClient();   // forces the host to build and run startup
            var services = factory.Services;

            // The scope guard wraps the mutating Docker service and leaves the gateway resolvable.
            Assert.IsAssignableFrom<DispatchProxy>(services.GetRequiredService<IDockerService>());
            var gateway = services.GetRequiredService<IMutationGateway>();

            // A principal below the capability level is refused, and the refusal reaches the AuditLog table.
            var request = new MutationRequest("compose.deploy",
                new MutationPrincipal(MutationPrincipalKind.Human, "viewer@example.com", "viewer@example.com", McpPermissionLevels.Read),
                MutationSurface.Ui, new MutationTarget("host", "local"), new Dictionary<string, JsonElement>(),
                OperationId: "op-boot-1");
            var ran = false;
            var outcome = await gateway.ExecuteAsync(request, (_, _) => { ran = true; return Task.FromResult(0); });

            Assert.Equal(MutationStatus.Denied, outcome.Status);
            Assert.False(ran);

            // The sink writes one row for intent + outcome and updates it in place.
            var sink = services.GetRequiredService<IMutationAuditSink>();
            var capability = services.GetRequiredService<ICapabilityCatalog>().Find("container.start")!;
            var startRequest = request with { CapabilityId = "container.start", OperationId = "op-boot-2" };
            var auditId = await sink.BeginAsync("op-boot-2", startRequest, capability, null, CancellationToken.None);
            await sink.CompleteAsync(auditId, "op-boot-2", MutationStatus.Executed, VerificationStatus.NotVerifiable,
                null, TimeSpan.FromMilliseconds(12), CancellationToken.None);

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var rows = await db.AuditLog.AsNoTracking()
                .Where(r => r.Details != null && r.Details.Contains("op-boot-"))
                .OrderBy(r => r.Id).ToListAsync();

            Assert.Equal(2, rows.Count);
            Assert.Equal("compose.deploy", rows[0].Action);
            Assert.False(rows[0].Success);
            Assert.Contains("\"status\":\"Denied\"", rows[0].Details);
            Assert.Equal("container.start", rows[1].Action);
            Assert.True(rows[1].Success);
            Assert.Contains("\"phase\":\"outcome\"", rows[1].Details);
            Assert.Contains("\"phase\":\"intent\"", rows[1].Details);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", previousDataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

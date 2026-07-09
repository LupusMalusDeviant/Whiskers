using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.HealthChecks;
using Whiskers.Services.Persistence;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests;

public sealed class HealthCheckTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string TempFile(string ext)
    {
        var p = Path.Combine(Path.GetTempPath(), $"hc-{Guid.NewGuid():N}.{ext}");
        _tempFiles.Add(p);
        return p;
    }

    // --- ServerConfigReadyCheck ---

    [Fact]
    public async Task ServerConfig_check_is_unhealthy_before_init_and_healthy_after()
    {
        var svc = new ServerConfigService(
            Options.Create(new DockerSettings()),
            NullLogger<ServerConfigService>.Instance,
            TempFile("json"));
        var check = new ServerConfigReadyCheck(svc);

        var before = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, before.Status);

        await svc.InitializeAsync();

        var after = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, after.Status);
    }

    // --- DbReadyCheck ---

    [Fact]
    public async Task Db_check_is_healthy_when_database_is_reachable()
    {
        // Resolve the path once: AddDbContext runs its options lambda per scope, so calling TempFile
        // inside it would hand EnsureCreated and the check two different files.
        var dbPath = TempFile("db");
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        using var sp = services.BuildServiceProvider();
        using (var scope = sp.CreateScope())
            scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();

        var check = new DbReadyCheck(sp.GetRequiredService<IServiceScopeFactory>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Db_check_is_unhealthy_when_database_is_unreachable()
    {
        var services = new ServiceCollection();
        // Path under a directory that does not exist — SQLite does not create intermediate dirs, so
        // opening the connection fails and the check must report Unhealthy rather than surface an error.
        services.AddDbContext<MetricsDbContext>(o =>
            o.UseSqlite("Data Source=/whiskers-hc-nonexistent/nested/does-not-exist.db"));
        using var sp = services.BuildServiceProvider();

        var check = new DbReadyCheck(sp.GetRequiredService<IServiceScopeFactory>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}

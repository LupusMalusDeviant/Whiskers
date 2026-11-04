using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Models;
using Whiskers.Services.Database;
using Whiskers.Services.Docker;
using Whiskers.Services.Mutations;
using Whiskers.Services.Server;

namespace Whiskers.Tests.ComponentTests;

/// <summary>The database tab moved out of ContainerDetail into <see cref="ContainerDatabasePanel"/>: it loads the
/// databases and tables of the container it is given the first time the tab is opened, and a role below the one a
/// mutation needs is still refused through the gateway (R1-21) before anything runs.</summary>
public class ContainerDatabasePanelTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private readonly CannedDatabase _db = new();
    private readonly Recording _mutations = new();

    public ContainerDatabasePanelTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IDockerService>(EnvDocker.Create());
        Services.AddSingleton<IDatabaseService>(_db);
        Services.AddSingleton<IHostCommandExecutor>(ServersKubeconfigSaveTests.NullProxy<IHostCommandExecutor>.Create());
        Services.AddSingleton<IUiMutationRunner>(_mutations);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ContainerInfo Postgres() => new() { Id = "abc123", Name = "app-db", Image = "postgres:16", State = "running" };

    private IRenderedComponent<ContainerDatabasePanel> RenderPanel(AppRole role) =>
        Render<ContainerDatabasePanel>(p => p
            .Add(x => x.Container, Postgres())
            .Add(x => x.ContainerId, "abc123")
            .Add(x => x.ServerId, "srv-1")
            .Add(x => x.Role, role));

    [Fact]
    public async Task Opening_the_tab_loads_the_databases_and_tables_of_the_given_container_once()
    {
        var panel = RenderPanel(AppRole.Operator);

        await panel.InvokeAsync(panel.Instance.EnsureLoadedAsync);
        await panel.InvokeAsync(panel.Instance.EnsureLoadedAsync);

        panel.WaitForAssertion(() => Assert.Contains("users", panel.Markup));
        Assert.Contains("appuser", panel.Markup);
        Assert.Equal(1, _db.DatabaseListings);
        Assert.Equal(("abc123", "appdb", "srv-1"), _db.LastTables);
    }

    [Fact]
    public async Task A_migration_below_the_admin_role_is_refused_through_the_gateway_and_runs_nothing()
    {
        var panel = RenderPanel(AppRole.Operator);
        await panel.InvokeAsync(panel.Instance.EnsureLoadedAsync);

        var run = typeof(ContainerDatabasePanel).GetMethod("RunMigration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await panel.InvokeAsync(() => (Task)run.Invoke(panel.Instance, null)!);

        Assert.Equal("container.exec", Assert.Single(_mutations.Refused));
        Assert.Empty(_mutations.Ran);
    }

    private sealed class CannedDatabase : IDatabaseService
    {
        public int DatabaseListings { get; private set; }
        public (string, string, string?) LastTables { get; private set; }

        public Task<List<string>> GetDatabasesAsync(string containerId, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
        {
            DatabaseListings++;
            return Task.FromResult(new List<string> { "appdb", "postgres" });
        }

        public Task<List<TableInfo>> GetTablesAsync(string containerId, string database, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
        {
            LastTables = (containerId, database, serverId);
            return Task.FromResult(new List<TableInfo> { new() { Name = "users", RowCount = 3 } });
        }

        public Task<List<ColumnInfo>> GetSchemaAsync(string containerId, string database, string table, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null) =>
            Task.FromResult(new List<ColumnInfo>());

        public Task<QueryResult> ExecuteQueryAsync(string containerId, string query, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null) =>
            throw new InvalidOperationException("not expected");

        public Task<(bool Success, string FilePath, long SizeBytes, string Error)> BackupDatabaseAsync(string containerId, string database,
            DatabaseType dbType, DatabaseCredentials creds, string? serverId = null) =>
            throw new InvalidOperationException("not expected");
    }

    /// <summary>A Docker service whose only answer is the container env the credentials are read from.</summary>
    private class EnvDocker : ServersKubeconfigSaveTests.NullProxy<IDockerService>
    {
        public static new IDockerService Create() => DispatchProxy.Create<IDockerService, EnvDocker>();

        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IDockerService.GetContainerEnvAsync)
                ? Task.FromResult(new List<KeyValuePair<string, string>>
                {
                    new("POSTGRES_USER", "appuser"), new("POSTGRES_PASSWORD", "pw"), new("POSTGRES_DB", "appdb"),
                })
                : base.Invoke(method, args);
    }

    private sealed class Recording : IUiMutationRunner
    {
        public List<string> Refused { get; } = [];
        public List<string> Ran { get; } = [];

        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default)
        {
            Ran.Add(capabilityId);
            return action(ct);
        }

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default)
        {
            Ran.Add(capabilityId);
            return action(ct);
        }

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default)
        {
            Refused.Add(capabilityId);
            return Task.CompletedTask;
        }
    }
}

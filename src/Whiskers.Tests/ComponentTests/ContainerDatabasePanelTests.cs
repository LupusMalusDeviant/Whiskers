using System.Reflection;
using System.Text.Json;
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

/// <summary>The database tab moved out of ContainerDetail into <see cref="ContainerDatabasePanel"/>. It works on the
/// container, server and role it is given; its state lives in the <see cref="ContainerDatabaseState"/> ContainerDetail
/// owns, so a tab switch (MudTabs disposes the inactive panel) neither reloads nor forgets anything; and a mutation
/// still goes through the gateway — refused below its role (R1-21), run inside the decision at it.</summary>
public class ContainerDatabasePanelTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private readonly CannedDatabase _db = new();
    private readonly Recording _mutations = new();
    private readonly List<(string ContainerId, string? ServerId)> _envReads = [];

    public ContainerDatabasePanelTests()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton<IDockerService>(EnvDocker.Create(_envReads));
        Services.AddSingleton<IDatabaseService>(_db);
        Services.AddSingleton<IHostCommandExecutor>(ServersKubeconfigSaveTests.NullProxy<IHostCommandExecutor>.Create());
        Services.AddSingleton<IUiMutationRunner>(_mutations);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ContainerInfo Postgres() => new() { Id = "abc123", Name = "app-db", Image = "postgres:16", State = "running" };

    private IRenderedComponent<ContainerDatabasePanel> RenderPanel(AppRole role, ContainerDatabaseState state) =>
        Render<ContainerDatabasePanel>(p => p
            .Add(x => x.Container, Postgres())
            .Add(x => x.ContainerId, "abc123")
            .Add(x => x.ServerId, "srv-1")
            .Add(x => x.Role, role)
            .Add(x => x.State, state));

    private static Task InvokePrivate(IRenderedComponent<ContainerDatabasePanel> panel, string name)
    {
        var method = typeof(ContainerDatabasePanel).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return panel.InvokeAsync(() => (Task)method.Invoke(panel.Instance, null)!);
    }

    [Fact]
    public async Task Opening_the_tab_loads_the_given_container_on_the_given_server_once()
    {
        var panel = RenderPanel(AppRole.Operator, new ContainerDatabaseState());

        await panel.InvokeAsync(panel.Instance.EnsureLoadedAsync);
        await panel.InvokeAsync(panel.Instance.EnsureLoadedAsync);

        panel.WaitForAssertion(() => Assert.Contains("users", panel.Markup));
        Assert.Contains("appuser", panel.Markup);
        Assert.Equal(("abc123", (string?)"srv-1"), Assert.Single(_envReads));
        Assert.Equal(1, _db.DatabaseListings);
        Assert.Equal(("abc123", "appdb", (string?)"srv-1"), _db.LastTables);
    }

    [Fact]
    public async Task A_tab_switch_keeps_the_loaded_data_the_query_and_a_running_import()
    {
        var state = new ContainerDatabaseState();
        var first = RenderPanel(AppRole.Admin, state);
        await first.InvokeAsync(first.Instance.EnsureLoadedAsync);
        state.DbQuery = "select 4711";
        state.ShowSeed = true;
        state.SeedFileName = "seed-4711.sql";
        state.DbSeeding = true;

        // Another tab becomes active: MudTabs disposes the panel; coming back renders a new one.
        await DisposeComponentsAsync();
        var second = RenderPanel(AppRole.Admin, state);
        await second.InvokeAsync(second.Instance.EnsureLoadedAsync);

        Assert.Equal(1, _db.DatabaseListings);
        Assert.Single(_envReads);
        // What the new panel shows comes from the shared state: the tables, the query, the chosen file, and the
        // running import's spinner with its button still disabled.
        second.WaitForAssertion(() => Assert.Contains("users", second.Markup));
        Assert.Contains("select 4711", second.Markup);
        Assert.Contains("seed-4711.sql", second.Markup);
        Assert.Contains("mud-progress-circular", second.Markup);
    }

    [Fact]
    public async Task An_operation_that_finishes_after_a_tab_switch_re_renders_the_panel_showing_it()
    {
        var state = new ContainerDatabaseState { ShowSeed = true, SeedFileName = "seed-4711.sql", DbSeeding = true };
        var panel = RenderPanel(AppRole.Admin, state);
        await panel.InvokeAsync(panel.Instance.EnsureLoadedAsync);
        Assert.Contains("mud-progress-circular", panel.Markup);

        // The handler of the disposed panel finishes into the shared state and says so.
        state.DbSeeding = false;
        state.NotifyChanged();

        panel.WaitForAssertion(() => Assert.DoesNotContain("mud-progress-circular", panel.Markup));
    }

    [Fact]
    public async Task A_migration_below_the_admin_role_is_refused_through_the_gateway_with_the_right_target()
    {
        var panel = RenderPanel(AppRole.Operator, new ContainerDatabaseState());
        await panel.InvokeAsync(panel.Instance.EnsureLoadedAsync);

        await InvokePrivate(panel, "RunMigration");

        var refusal = Assert.Single(_mutations.Refused);
        Assert.Equal("container.exec", refusal.Capability);
        Assert.Equal("abc123", refusal.Arguments.GetProperty("containerId").GetString());
        Assert.Equal("app-db", refusal.Arguments.GetProperty("name").GetString());
        Assert.Equal("srv-1", refusal.Arguments.GetProperty("serverId").GetString());
        Assert.Empty(_mutations.Ran);
    }

    [Fact]
    public async Task An_admin_query_runs_inside_the_db_query_decision_for_the_given_container()
    {
        var state = new ContainerDatabaseState();
        var panel = RenderPanel(AppRole.Admin, state);
        await panel.InvokeAsync(panel.Instance.EnsureLoadedAsync);
        state.DbQuery = "select count(*) from users";

        await InvokePrivate(panel, "ExecuteQuery");

        var run = Assert.Single(_mutations.Ran);
        Assert.Equal("db.query", run.Capability);
        Assert.Equal("abc123", run.Arguments.GetProperty("containerId").GetString());
        Assert.Equal("srv-1", run.Arguments.GetProperty("serverId").GetString());
        Assert.Equal(("abc123", "select count(*) from users", (string?)"srv-1"), _db.LastQuery);
        Assert.Empty(_mutations.Refused);
        Assert.True(state.DbResult?.Success);
    }

    private sealed class CannedDatabase : IDatabaseService
    {
        public int DatabaseListings { get; private set; }
        public (string, string, string?) LastTables { get; private set; }
        public (string, string, string?) LastQuery { get; private set; }

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

        public Task<QueryResult> ExecuteQueryAsync(string containerId, string query, DatabaseType dbType, DatabaseCredentials creds, string? serverId = null)
        {
            LastQuery = (containerId, query, serverId);
            return Task.FromResult(new QueryResult { Columns = ["count"], Rows = [["3"]], RowCount = 1 });
        }

        public Task<(bool Success, string FilePath, long SizeBytes, string Error)> BackupDatabaseAsync(string containerId, string database,
            DatabaseType dbType, DatabaseCredentials creds, string? serverId = null, string? containerName = null) =>
            throw new InvalidOperationException("not expected");
    }

    /// <summary>A Docker service whose only answer is the container env the credentials are read from; it records
    /// which container on which server it was asked about.</summary>
    private class EnvDocker : ServersKubeconfigSaveTests.NullProxy<IDockerService>
    {
        private List<(string, string?)> _reads = null!;

        public static IDockerService Create(List<(string, string?)> reads)
        {
            var proxy = DispatchProxy.Create<IDockerService, EnvDocker>();
            ((EnvDocker)(object)proxy)._reads = reads;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IDockerService.GetContainerEnvAsync)) return base.Invoke(method, args);
            _reads.Add(((string)args![0]!, (string?)args[1]));
            return Task.FromResult(new List<KeyValuePair<string, string>>
            {
                new("POSTGRES_USER", "appuser"), new("POSTGRES_PASSWORD", "pw"), new("POSTGRES_DB", "appdb"),
            });
        }
    }

    private sealed class Recording : IUiMutationRunner
    {
        public Task<T> RunAsync<TTarget, T>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task<T>> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            RunAsync(capabilityId, arguments, token => action(TestTargets.Of<TTarget>(capabilityId, arguments), token), ct);

        public Task RunAsync<TTarget>(string capabilityId, object arguments,
            Func<TTarget, CancellationToken, Task> action, CancellationToken ct = default) where TTarget : ResolvedTarget =>
            RunAsync(capabilityId, arguments, token => action(TestTargets.Of<TTarget>(capabilityId, arguments), token), ct);

        public List<(string Capability, JsonElement Arguments)> Refused { get; } = [];
        public List<(string Capability, JsonElement Arguments)> Ran { get; } = [];

        private static JsonElement Json(object arguments) => JsonSerializer.SerializeToElement(arguments);

        public Task<T> RunAsync<T>(string capabilityId, object arguments, Func<CancellationToken, Task<T>> action,
            CancellationToken ct = default)
        {
            Ran.Add((capabilityId, Json(arguments)));
            return action(ct);
        }

        public Task RunAsync(string capabilityId, object arguments, Func<CancellationToken, Task> action,
            CancellationToken ct = default)
        {
            Ran.Add((capabilityId, Json(arguments)));
            return action(ct);
        }

        public Task RefuseAsync(string capabilityId, object arguments, string reason, CancellationToken ct = default)
        {
            Refused.Add((capabilityId, Json(arguments)));
            return Task.CompletedTask;
        }
    }
}

using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Backup;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Persistence;
using Whiskers.Services.Setup;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0010 (INV-25): once a guardrail policy has existed — recorded in the database, written at creation, at every
/// start with a policy file and after saves, never cleared — missing both <c>guardrails.json</c> and <c>.bak</c> is
/// fail-closed, as is a record that cannot be read. The store's two start phases are driven directly against a real
/// SQLite database migrated like the app's.
/// </summary>
public sealed class GuardrailPolicyMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"guardrail-marker-{Guid.NewGuid():N}");
    private readonly List<ServiceProvider> _providers = [];

    public GuardrailPolicyMarkerTests() => Directory.CreateDirectory(_dir);

    private string FilePath => Path.Combine(_dir, "guardrails.json");
    private string BakPath => FilePath + DurableFile.BackupSuffix;

    private static AgentPrincipal Admin => new(AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null,
        UserEmail: "admin@example.com");

    public void Dispose()
    {
        DurableFile.FailingWrites.TryRemove(Path.GetFullPath(FilePath), out _);
        foreach (var provider in _providers) provider.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------------------------------------ the database

    /// <summary>A metrics database at <paramref name="dbPath"/> (migrated unless <paramref name="migrate"/> is false).</summary>
    private async Task<ServiceProvider> DatabaseAsync(string? dbPath = null, bool migrate = true)
    {
        dbPath ??= Path.Combine(_dir, "metrics.db");
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={dbPath}",
            sql => sql.MigrationsAssembly(DatabaseRegistration.SqliteMigrationsAssembly)), ServiceLifetime.Transient);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        if (migrate)
        {
            await using var db = provider.GetRequiredService<MetricsDbContext>();
            await db.Database.MigrateAsync();
        }
        return provider;
    }

    private static DbGuardrailPolicyMarker Marker(IServiceProvider db) =>
        new(db.GetRequiredService<IServiceScopeFactory>());

    private static async Task<InstanceMarkerEntity?> RowAsync(IServiceProvider db)
    {
        await using var ctx = db.GetRequiredService<MetricsDbContext>();
        return await ctx.InstanceMarkers.AsNoTracking().SingleOrDefaultAsync(m => m.Key == DbGuardrailPolicyMarker.Key);
    }

    private static async Task AddSnapshotAsync(IServiceProvider db)
    {
        await using var ctx = db.GetRequiredService<MetricsDbContext>();
        ctx.PolicySnapshots.Add(new PolicySnapshotEntity
        {
            VersionHash = "abc", Preset = "Standard", PolicyJson = "{}", CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task DropMarkerTableAsync(IServiceProvider db)
    {
        await using var ctx = db.GetRequiredService<MetricsDbContext>();
        await ctx.Database.ExecuteSqlRawAsync("DROP TABLE InstanceMarkers;");
    }

    private static string ValidJson(bool killSwitch = false)
    {
        var config = GuardrailConfig.SafeDefault();
        config.Presets[0].Policy.ReadOnlyMode = killSwitch;
        return JsonSerializer.Serialize(config);
    }

    private GuardrailStore Store(IGuardrailPolicyMarker? marker) => new(filePath: FilePath, marker: marker);

    // ------------------------------------------------------------------------------------------------ the table

    [Theory]
    //          guardrails.json  .bak   record     snapshots  enforced  file written  record after
    [InlineData("valid",        false, "absent",  false,     true,     false,        "loaded")]
    [InlineData("valid",        true,  "present", false,     true,     false,        "present")]
    [InlineData("valid",        false, "fails",   false,     true,     false,        null)]
    [InlineData("invalid",      false, "absent",  false,     false,    false,        "backfill")]
    [InlineData("invalid",      true,  "present", false,     false,    false,        "present")]
    [InlineData("directory",    false, "absent",  false,     false,    false,        "backfill")]
    [InlineData("missing",      true,  "absent",  false,     false,    false,        "backfill")]
    [InlineData("missing",      false, "absent",  false,     true,     true,         "created")]
    [InlineData("missing",      false, "absent",  true,      false,    false,        "snapshot-evidence")]
    [InlineData("missing",      false, "present", false,     false,    false,        "present")]
    [InlineData("missing",      false, "fails",   false,     false,    false,        null)]
    public async Task Every_combination_of_file_backup_and_marker_decides_as_the_contract(string file, bool bak,
        string record, bool snapshots, bool enforced, bool fileWritten, string? recordAfter)
    {
        var db = await DatabaseAsync();
        switch (file)
        {
            case "valid": await File.WriteAllTextAsync(FilePath, ValidJson()); break;
            case "invalid": await File.WriteAllTextAsync(FilePath, "{ not json"); break;
            case "directory": Directory.CreateDirectory(FilePath); break;
        }
        if (bak) await File.WriteAllTextAsync(BakPath, ValidJson());
        if (record == "present") await Marker(db).EnsureAsync("present");
        if (snapshots) await AddSnapshotAsync(db);
        if (record == "fails") await DropMarkerTableAsync(db);
        var before = file == "valid" ? await File.ReadAllTextAsync(FilePath) : null;

        var store = await Store(Marker(db)).StartAsync();

        Assert.Equal(enforced, store.LoadError is null);
        if (!enforced)
        {
            var policy = store.Current;
            Assert.True(policy.ReadOnlyMode);
            Assert.Equal(GuardrailPolicy.FailClosed().ProtectedResources, policy.ProtectedResources);
        }
        if (file == "missing")
            Assert.Equal(fileWritten, File.Exists(FilePath));
        else if (file == "valid")
            Assert.Equal(before, await File.ReadAllTextAsync(FilePath));   // a present file decides as it is (D5)
        if (recordAfter is null)
        {
            // The record cannot be read: a present file still decides; a missing one fails closed, nothing is saved.
            if (file == "missing") Assert.Contains("could not be read", store.LoadError);
            else Assert.NotNull(store.MarkerProblem);
        }
        else
        {
            var row = await RowAsync(db);
            Assert.NotNull(row);
            Assert.Equal(recordAfter, row.Detail);
            Assert.True(store.MarkerRecorded);
        }
    }

    [Fact]
    public async Task Deleting_both_files_after_a_first_start_fails_closed_on_the_next_start()
    {
        // The INV-25 gap itself: a first start, an Admin's policy, then both files deleted on the host.
        var db = await DatabaseAsync();
        var first = await Store(Marker(db)).StartAsync();
        Assert.Null(first.LoadError);
        var config = first.Config;
        config.Presets[0].Policy.ProtectedResources.Add("payments-db");
        await first.SaveConfigAsync(config, Admin);
        File.Delete(FilePath);
        File.Delete(BakPath);

        var next = await Store(Marker(db)).StartAsync();

        Assert.NotNull(next.LoadError);
        Assert.Contains("this instance had a guardrail policy", next.LoadError);
        Assert.Contains("--reset-guardrails", next.LoadError);
        Assert.True(next.Current.ReadOnlyMode);
        Assert.False(File.Exists(FilePath));   // no weaker default was created in its place
        Assert.Equal(GuardrailPolicyMarkerSources.Created, (await RowAsync(db))!.Detail);
    }

    [Fact]
    public async Task A_crash_between_file_and_marker_is_backfilled_not_fail_closed()
    {
        // The first start saves the SafeDefault, then the record write fails (as if the process died in between).
        var broken = new InMemoryGuardrailPolicyMarker { WriteFails = new IOException("disk gone") };
        var first = await Store(broken).StartAsync();
        Assert.Null(first.LoadError);
        Assert.True(File.Exists(FilePath));
        Assert.Null(broken.RecordedAt);

        // The next start finds the file: it is loaded and the record is written, not fail-closed.
        var db = await DatabaseAsync();
        var next = await Store(Marker(db)).StartAsync();

        Assert.Null(next.LoadError);
        Assert.Null(next.MarkerProblem);
        Assert.Equal(GuardrailPolicyMarkerSources.Loaded, (await RowAsync(db))!.Detail);
    }

    [Fact]
    public async Task A_marker_write_failure_keeps_a_valid_policy_enforced_and_is_reported()
    {
        await File.WriteAllTextAsync(FilePath, ValidJson());
        var marker = new InMemoryGuardrailPolicyMarker { WriteFails = new InvalidOperationException("table missing") };
        var store = await Store(marker).StartAsync();

        Assert.Null(store.LoadError);
        Assert.Equal("read", store.Current.MaxAutonomousLevel);
        Assert.NotNull(store.MarkerProblem);
        var check = ProductionReadinessService.GuardrailPolicyCheck(store);
        Assert.False(check.Ok);
        Assert.Contains(store.MarkerProblem!, check.Detail);

        // Retried at the next save, and the readiness line turns green.
        marker.WriteFails = null;
        await store.SaveConfigAsync(store.Config, Admin);
        Assert.Null(store.MarkerProblem);
        Assert.True(store.MarkerRecorded);
        Assert.True(ProductionReadinessService.GuardrailPolicyCheck(store).Ok);
        Assert.Equal(GuardrailPolicyMarkerSources.Backfill, marker.Source);
    }

    [Fact]
    public async Task Saves_never_clear_the_marker()
    {
        var db = await DatabaseAsync();
        var store = await Store(Marker(db)).StartAsync();
        var recorded = await RowAsync(db);
        Assert.NotNull(recorded);

        var config = store.Config;
        config.Presets[0].Policy.ReadOnlyMode = true;
        await store.SaveConfigAsync(config, Admin);
        await store.DisableKillSwitchAsync(Admin);
        await store.SaveAsync(GuardrailPolicy.SafeDefault(), Admin);
        DurableFile.FailingWrites[Path.GetFullPath(FilePath)] = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveConfigAsync(store.Config, Admin));
        DurableFile.FailingWrites.TryRemove(Path.GetFullPath(FilePath), out _);

        var after = await RowAsync(db);
        Assert.NotNull(after);
        Assert.Equal(recorded.RecordedAt, after.RecordedAt);
        Assert.Equal(recorded.Detail, after.Detail);

        // D2: nothing in Whiskers can update or remove the record — the marker interface only reads and inserts.
        Assert.Equal(
            ["EnsureAsync", "ExistsAsync", "PolicySnapshotsExistAsync", "RecordedAtAsync"],
            typeof(IGuardrailPolicyMarker).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Phase_one_alone_never_saves_the_safe_default()
    {
        var marker = new InMemoryGuardrailPolicyMarker();
        var store = Store(marker);

        await store.InitializeAsync();   // the Order-80 warm-up; the start fails before phase 2

        Assert.Equal(GuardrailStore.NotDecidedReason, store.LoadError);
        Assert.True(store.Current.ReadOnlyMode);
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(BakPath));
        Assert.Equal(0, marker.Reads);
        Assert.Equal(0, marker.Writes);
    }

    [Theory]
    [InlineData("absent", true)]
    [InlineData("present", false)]
    [InlineData("snapshots", false)]
    [InlineData("fails", false)]
    public async Task Inspect_reads_the_marker_and_writes_nothing(string record, bool enforced)
    {
        var marker = new InMemoryGuardrailPolicyMarker
        {
            RecordedAt = record == "present" ? DateTime.UtcNow : null,
            Snapshots = record == "snapshots",
            ReadFails = record == "fails" ? new IOException("database unreachable") : null,
        };
        var store = Store(marker);

        await store.InspectAsync();

        Assert.Equal(enforced, store.LoadError is null);
        Assert.True(marker.Reads > 0);
        Assert.Equal(0, marker.Writes);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public async Task A_store_without_a_record_store_fails_closed_on_a_missing_file()
    {
        var store = await Store(marker: null).StartAsync();

        Assert.NotNull(store.LoadError);
        Assert.Contains("could not be read", store.LoadError);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task An_unreachable_database_fails_closed_and_saves_nothing()
    {
        // A database file that cannot be opened (a directory in its place) as the record's store.
        var dbPath = Path.Combine(_dir, "unreachable.db");
        Directory.CreateDirectory(dbPath);
        var db = await DatabaseAsync(dbPath, migrate: false);

        var store = await Store(Marker(db)).StartAsync();

        Assert.NotNull(store.LoadError);
        Assert.Contains("could not be read", store.LoadError);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Snapshot_rows_count_as_evidence_of_an_earlier_policy()
    {
        // D8 / Q3: the files were already gone before the upgrade, but decisions were made under a policy here.
        var db = await DatabaseAsync();
        await AddSnapshotAsync(db);

        var store = await Store(Marker(db)).StartAsync();

        Assert.NotNull(store.LoadError);
        Assert.Contains("policy decisions were recorded", store.LoadError);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(GuardrailPolicyMarkerSources.SnapshotEvidence, (await RowAsync(db))!.Detail);
    }

    [Fact]
    public async Task The_second_phase_tells_its_listeners()
    {
        var store = Store(new InMemoryGuardrailPolicyMarker());
        await store.InitializeAsync();
        var changed = 0;
        store.Changed += () => changed++;

        await store.ReconcileMarkerAsync();

        Assert.Equal(1, changed);
        Assert.Null(store.LoadError);
    }

    // ------------------------------------------------------------------------------------------------ --reset-guardrails

    private ServiceProvider CliServices(GuardrailStore store, LocalAccountWorld.RecordingAuditLog audit)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGuardrailStore>(store);
        services.AddSingleton<IAuditLogService>(audit);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    [Theory]
    [InlineData("json")]
    [InlineData("bak")]
    [InlineData("directory")]
    public async Task Reset_cli_refuses_while_a_file_exists(string present)
    {
        switch (present)
        {
            case "json": await File.WriteAllTextAsync(FilePath, "{ not json"); break;
            case "bak": await File.WriteAllTextAsync(BakPath, ValidJson()); break;
            case "directory": Directory.CreateDirectory(FilePath); break;
        }
        var before = string.Join("|", Directory.GetFileSystemEntries(_dir).Order(StringComparer.Ordinal));
        var marker = new InMemoryGuardrailPolicyMarker();
        var audit = new LocalAccountWorld.RecordingAuditLog();
        var output = new StringWriter();

        var rc = await GuardrailResetCli.RunAsync(CliServices(Store(marker), audit), [], output);

        Assert.Equal(3, rc);
        Assert.Contains("Refused", output.ToString());
        Assert.Equal(before, string.Join("|", Directory.GetFileSystemEntries(_dir).Order(StringComparer.Ordinal)));
        if (present == "json") Assert.Equal("{ not json", await File.ReadAllTextAsync(FilePath));
        Assert.Empty(audit.Rows);
        Assert.Equal(0, marker.Writes);
    }

    [Fact]
    public async Task Reset_cli_writes_the_safe_default_with_the_kill_switch_on_and_audits()
    {
        // The instance had a policy, both files are gone: fail-closed until the host resets it.
        var db = await DatabaseAsync();
        await Marker(db).EnsureAsync(GuardrailPolicyMarkerSources.Loaded);
        Assert.NotNull((await Store(Marker(db)).StartAsync()).LoadError);
        var audit = new LocalAccountWorld.RecordingAuditLog();
        var output = new StringWriter();

        var rc = await GuardrailResetCli.RunAsync(CliServices(Store(Marker(db)), audit), [], output);

        Assert.Equal(0, rc);
        Assert.Contains("kill switch ON", output.ToString());
        Assert.Contains("restart", output.ToString());
        var row = Assert.Single(audit.Rows);
        Assert.Equal(GuardrailResetCli.ResetAction, row.Action);
        Assert.Equal(GuardrailResetCli.Actor, row.Actor);
        Assert.True(row.Success);
        Assert.Equal(GuardrailPolicyMarkerSources.Loaded, (await RowAsync(db))!.Detail);   // the record is never rewritten

        // After the restart: the SafeDefault is enforced with the kill switch on; nothing else differs from it.
        var restarted = await Store(Marker(db)).StartAsync();
        Assert.Null(restarted.LoadError);
        var expected = GuardrailPolicy.SafeDefault();
        expected.ReadOnlyMode = true;
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(restarted.Current));

        // A second reset is refused: the file exists now.
        Assert.Equal(3, await GuardrailResetCli.RunAsync(CliServices(Store(Marker(db)), audit), [], new StringWriter()));
    }

    [Fact]
    public async Task Reset_cli_records_the_marker_on_a_fresh_database_and_changes_nothing_without_an_audit_row()
    {
        var audit = new LocalAccountWorld.RecordingAuditLog { Down = true };
        var marker = new InMemoryGuardrailPolicyMarker();
        var output = new StringWriter();

        Assert.Equal(1, await GuardrailResetCli.RunAsync(CliServices(Store(marker), audit), [], output));
        Assert.Contains("audit log could not be written", output.ToString());
        Assert.False(File.Exists(FilePath));
        Assert.Equal(0, marker.Writes);

        audit.Down = false;
        Assert.Equal(0, await GuardrailResetCli.RunAsync(CliServices(Store(marker), audit), [], new StringWriter()));
        Assert.Equal(GuardrailPolicyMarkerSources.ResetCli, marker.Source);
        Assert.Equal(2, await GuardrailResetCli.RunAsync(CliServices(Store(marker), audit), ["extra"], new StringWriter()));
    }

    // ------------------------------------------------------------------------------------------------ restore (F3)

    [Fact]
    public async Task A_restore_of_an_archive_with_the_marker_but_without_guardrails_json_fails_closed_after_the_swap()
    {
        // Live: an instance with a policy file. Archive: a database holding the record, but no guardrails.json.
        var paths = new DataPathOptions(_dir);
        await File.WriteAllTextAsync(paths.GuardrailsJson, ValidJson());
        Directory.CreateDirectory(paths.RestoreStagingDir);
        var archivedDb = await DatabaseAsync(Path.Combine(paths.RestoreStagingDir, "metrics.db"));
        await Marker(archivedDb).EnsureAsync(GuardrailPolicyMarkerSources.Loaded);
        foreach (var p in _providers) await p.DisposeAsync();
        _providers.Clear();
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(paths.RestoreStagingDir, "manifest.json"), "{}");
        await File.WriteAllTextAsync(paths.RestorePendingMarker,
            JsonSerializer.Serialize(new RestoreMarker { StagingDir = paths.RestoreStagingDir, Provider = "sqlite" }));

        RestoreBootHandler.ApplyPendingRestore(paths);

        Assert.False(File.Exists(paths.GuardrailsJson));   // the archive does not hold it: removed by the swap
        var restored = await DatabaseAsync(paths.DbPath);
        var store = await new GuardrailStore(filePath: paths.GuardrailsJson, marker: Marker(restored)).StartAsync();
        Assert.NotNull(store.LoadError);
        Assert.Contains("this instance had a guardrail policy", store.LoadError);
        Assert.False(File.Exists(paths.GuardrailsJson));
    }

    // ------------------------------------------------------------------------------------------------ the record's store

    [Fact]
    public async Task Ensure_inserts_once_and_a_concurrent_duplicate_counts_as_success()
    {
        var db = await DatabaseAsync();
        var marker = Marker(db);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => marker.EnsureAsync(i == 0 ? "created" : "loaded")));
        var first = await RowAsync(db);
        await marker.EnsureAsync(GuardrailPolicyMarkerSources.ResetCli);

        Assert.NotNull(first);
        var again = await RowAsync(db);
        Assert.Equal(first.RecordedAt, again!.RecordedAt);
        Assert.Equal(first.Detail, again.Detail);
        Assert.True(await ((IGuardrailPolicyMarker)marker).ExistsAsync());
        await using var ctx = db.GetRequiredService<MetricsDbContext>();
        Assert.Equal(1, await ctx.InstanceMarkers.CountAsync());
    }
}

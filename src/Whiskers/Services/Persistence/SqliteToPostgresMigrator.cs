using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;

namespace Whiskers.Services.Persistence;

/// <summary>One-time, offline data copy from the default SQLite metrics database to a PostgreSQL target.
/// Invoked via the CLI (<c>--migrate-to-postgres "&lt;conn&gt;"</c>), never during normal boot.
///
/// Data-safety contract (project requirement): the <b>source is never modified</b> — it is only read — and
/// the <b>target must be freshly migrated and empty</b> (the tool aborts on a non-empty target; it never
/// merges). The 15 tables have surrogate <c>long</c> primary keys and <b>no foreign keys between them</b>,
/// so rows are re-inserted WITHOUT their old <c>Id</c> and the target assigns fresh identity values (which
/// also advances the sequence correctly). The meaningful unique business keys (<c>BackupId</c>, <c>TaskId</c>,
/// <c>RuleId</c>, <c>IdentityKey</c>, …) are ordinary columns and copy across unchanged.</summary>
public static class SqliteToPostgresMigrator
{
    private const int BatchSize = 5000;

    /// <summary>CLI entry point: builds the SQLite source + PostgreSQL target contexts, migrates the target
    /// schema, then copies the data. Returns a process exit code (0 = success; non-zero = aborted/failed).</summary>
    public static async Task<int> RunAsync(DataPathOptions dataPaths, string targetConnection, TextWriter log,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetConnection))
        {
            await log.WriteLineAsync("ERROR: a PostgreSQL connection string is required, e.g.:");
            await log.WriteLineAsync("  dotnet Whiskers.dll --migrate-to-postgres \"Host=localhost;Database=whiskers;Username=whiskers;Password=…\"");
            return 2;
        }

        await log.WriteLineAsync("Whiskers — one-time SQLite → PostgreSQL data migration");
        await log.WriteLineAsync($"  source (SQLite): {dataPaths.DbConnectionString}");
        await log.WriteLineAsync("  NOTE: the source is never modified — but back up metrics.db before proceeding.");
        await log.WriteLineAsync("");

        var sqliteOptions = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseSqlite(dataPaths.DbConnectionString, o => o.MigrationsAssembly("Whiskers.Migrations.Sqlite"))
            .Options;
        var pgOptions = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseNpgsql(targetConnection, o => o.MigrationsAssembly("Whiskers.Migrations.Postgres"))
            .Options;

        await using var source = new MetricsDbContext(sqliteOptions);
        await using var target = new MetricsDbContext(pgOptions);

        try
        {
            await log.WriteLineAsync("Bringing the target schema up to date (PostgreSQL migrate)…");
            await target.Database.MigrateAsync(ct);
            return await MigrateDataAsync(source, target, log, ct);
        }
        catch (Exception ex)
        {
            // No secret leak: the connection string (with its password) is never logged, only the message.
            await log.WriteLineAsync($"MIGRATION FAILED: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Provider-agnostic core: aborts if the target holds any data, otherwise copies all 15 tables
    /// and prints a per-table row-count report. Exposed for tests, which drive it with two SQLite contexts.</summary>
    public static async Task<int> MigrateDataAsync(MetricsDbContext source, MetricsDbContext target,
        TextWriter log, CancellationToken ct = default)
    {
        var occupied = await FirstNonEmptyTableAsync(target, ct);
        if (occupied is not null)
        {
            await log.WriteLineAsync($"ABORT: the target already contains data (table '{occupied}' is not empty). " +
                "This tool only migrates into a fresh, empty database — it never merges.");
            return 3;
        }

        // No FK ordering constraints (the 15 tables are independent), so any order is safe.
        var report = new List<(string Table, int Rows)>
        {
            ("ContainerMetrics", await CopyAsync(source.ContainerMetrics, target, e => e.Id = 0, ct)),
            ("ServerMetrics",    await CopyAsync(source.ServerMetrics,    target, e => e.Id = 0, ct)),
            ("AlertHistory",     await CopyAsync(source.AlertHistory,     target, e => e.Id = 0, ct)),
            ("AuditLog",         await CopyAsync(source.AuditLog,         target, e => e.Id = 0, ct)),
            ("McpToolCalls",     await CopyAsync(source.McpToolCalls,     target, e => e.Id = 0, ct)),
            ("VolumeBackups",    await CopyAsync(source.VolumeBackups,    target, e => e.Id = 0, ct)),
            ("ScheduledTasks",   await CopyAsync(source.ScheduledTasks,   target, e => e.Id = 0, ct)),
            ("TaskRunHistory",   await CopyAsync(source.TaskRunHistory,   target, e => e.Id = 0, ct)),
            ("LogAlertRules",    await CopyAsync(source.LogAlertRules,    target, e => e.Id = 0, ct)),
            ("UpdatePolicies",   await CopyAsync(source.UpdatePolicies,   target, e => e.Id = 0, ct)),
            ("UpdateHistory",    await CopyAsync(source.UpdateHistory,    target, e => e.Id = 0, ct)),
            ("Webhooks",         await CopyAsync(source.Webhooks,         target, e => e.Id = 0, ct)),
            ("WebhookLogs",      await CopyAsync(source.WebhookLogs,      target, e => e.Id = 0, ct)),
            ("CveFirstSeen",     await CopyAsync(source.CveFirstSeen,     target, e => e.Id = 0, ct)),
            ("Notifications",    await CopyAsync(source.Notifications,    target, e => e.Id = 0, ct)),
        };

        await log.WriteLineAsync("");
        await log.WriteLineAsync("Done. Rows copied per table:");
        var total = 0;
        foreach (var (table, rows) in report)
        {
            await log.WriteLineAsync($"  {table,-20} {rows,10:N0}");
            total += rows;
        }
        await log.WriteLineAsync($"  {"TOTAL",-20} {total,10:N0}");
        await log.WriteLineAsync("");
        await log.WriteLineAsync("Now start Whiskers with WHISKERS_DB_PROVIDER=postgres and the same connection string.");
        return 0;
    }

    /// <summary>Returns the name of the first non-empty target table, or null if every table is empty.</summary>
    private static async Task<string?> FirstNonEmptyTableAsync(MetricsDbContext t, CancellationToken ct)
    {
        if (await t.ContainerMetrics.AnyAsync(ct)) return "ContainerMetrics";
        if (await t.ServerMetrics.AnyAsync(ct)) return "ServerMetrics";
        if (await t.AlertHistory.AnyAsync(ct)) return "AlertHistory";
        if (await t.AuditLog.AnyAsync(ct)) return "AuditLog";
        if (await t.McpToolCalls.AnyAsync(ct)) return "McpToolCalls";
        if (await t.VolumeBackups.AnyAsync(ct)) return "VolumeBackups";
        if (await t.ScheduledTasks.AnyAsync(ct)) return "ScheduledTasks";
        if (await t.TaskRunHistory.AnyAsync(ct)) return "TaskRunHistory";
        if (await t.LogAlertRules.AnyAsync(ct)) return "LogAlertRules";
        if (await t.UpdatePolicies.AnyAsync(ct)) return "UpdatePolicies";
        if (await t.UpdateHistory.AnyAsync(ct)) return "UpdateHistory";
        if (await t.Webhooks.AnyAsync(ct)) return "Webhooks";
        if (await t.WebhookLogs.AnyAsync(ct)) return "WebhookLogs";
        if (await t.CveFirstSeen.AnyAsync(ct)) return "CveFirstSeen";
        if (await t.Notifications.AnyAsync(ct)) return "Notifications";
        return null;
    }

    /// <summary>Streams one table from the source (never loading it whole) and inserts it into the target in
    /// batches. <paramref name="resetId"/> zeroes the surrogate PK so the target assigns a fresh identity.</summary>
    private static async Task<int> CopyAsync<T>(IQueryable<T> source, MetricsDbContext target,
        Action<T> resetId, CancellationToken ct) where T : class
    {
        var total = 0;
        var buffer = new List<T>(BatchSize);
        await foreach (var row in source.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            resetId(row);
            buffer.Add(row);
            if (buffer.Count >= BatchSize)
                total += await FlushAsync(target, buffer, ct);
        }
        if (buffer.Count > 0)
            total += await FlushAsync(target, buffer, ct);
        return total;
    }

    private static async Task<int> FlushAsync<T>(MetricsDbContext target, List<T> buffer, CancellationToken ct)
        where T : class
    {
        target.Set<T>().AddRange(buffer);
        await target.SaveChangesAsync(ct);
        target.ChangeTracker.Clear(); // keep the tracker (and memory) flat across batches
        var n = buffer.Count;
        buffer.Clear();
        return n;
    }
}

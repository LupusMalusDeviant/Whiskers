using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;

namespace Whiskers.Services.Persistence;

/// <summary>One-time, offline data copy from the default SQLite metrics database to a PostgreSQL target.
/// Invoked via the CLI (<c>--migrate-to-postgres "&lt;conn&gt;"</c>), never during normal boot.
///
/// Data-safety contract (project requirement): the <b>source is never modified</b> — it is only read — and
/// the <b>target must be freshly migrated and empty</b> (the tool aborts on a non-empty target; it never
/// merges). The metrics tables have surrogate <c>long</c> primary keys and <b>no foreign keys between them</b>,
/// so rows are re-inserted WITHOUT their old <c>Id</c> and the target assigns fresh identity values (which
/// also advances the sequence correctly). One column names such a key: <c>Operations.AuditId</c> (R1-18) names its
/// <c>AuditLog</c> row, so it is rewritten to that row's new id (<see cref="CopyRun"/>). The meaningful unique business keys (<c>BackupId</c>, <c>TaskId</c>,
/// <c>RuleId</c>, <c>IdentityKey</c>, …) are ordinary columns and copy across unchanged.
///
/// The local-login users live in a second context (<see cref="WhiskersIdentityDbContext"/>) against the same
/// database and are copied too — with their ids kept, because claims, logins and tokens reference them.
/// Every entity of <see cref="MetricsDbContext"/> must appear in <see cref="MetricsTables"/>; a test enforces
/// that, so a new table cannot silently be left behind (it once was: three tables and the users).</summary>
public static class SqliteToPostgresMigrator
{
    private const int BatchSize = 5000;

    /// <summary>One copyable metrics table: its name for the report, its entity type (for the coverage test),
    /// an emptiness probe for the target and the copy itself.</summary>
    internal sealed record Table(
        string Name,
        Type EntityType,
        Func<MetricsDbContext, CancellationToken, Task<bool>> AnyAsync,
        Func<MetricsDbContext, MetricsDbContext, CopyRun, CancellationToken, Task<int>> CopyAsync);

    /// <summary>What one migration run carries from table to table: the old ids of the audit rows an operation names
    /// (read before the copy), and the new id each of them got in the target.</summary>
    internal sealed class CopyRun
    {
        public HashSet<long> NamedAuditIds { get; init; } = [];
        public Dictionary<long, long> AuditIds { get; } = [];
        internal List<(Whiskers.Models.AuditLogEntity Row, long OldId)> Pending { get; } = [];
    }

    private static Table T<TEntity>(string name, Func<MetricsDbContext, DbSet<TEntity>> set, Action<TEntity> resetId)
        where TEntity : class =>
        T(name, set, _ => resetId);

    private static Table T<TEntity>(string name, Func<MetricsDbContext, DbSet<TEntity>> set,
        Func<CopyRun, Action<TEntity>> resetId, Func<CopyRun, Action>? afterFlush = null)
        where TEntity : class =>
        new(name, typeof(TEntity),
            (ctx, ct) => set(ctx).AnyAsync(ct),
            (src, dst, run, ct) => CopyAsync(set(src), dst, resetId(run), ct, afterFlush?.Invoke(run)));

    /// <summary>The tables a supported older release does not have yet, newest release first: a source may lack
    /// exactly one of these sets — the tables of every release after its own (R1-10: the webhook deliveries; R1-18: the operations; R1-14: the approvals; R1-12: the
    /// decision tables).</summary>
    internal static readonly IReadOnlyList<HashSet<string>> OlderReleaseTables =
    [
        new(StringComparer.Ordinal) { "WebhookDeliveries" },
        new(StringComparer.Ordinal) { "WebhookDeliveries", "Operations" },
        new(StringComparer.Ordinal) { "WebhookDeliveries", "Operations", "Approvals" },
        new(StringComparer.Ordinal) { "WebhookDeliveries", "Operations", "Approvals", "PolicyDecisions", "PolicySnapshots" },
    ];

    /// <summary>Whether the SQLite source has the table at all. Any other provider is assumed to have the full schema.</summary>
    private static async Task<bool> SourceHasTableAsync(MetricsDbContext source, Table table, CancellationToken ct)
    {
        if (!source.Database.IsSqlite()) return true;
        var name = source.Model.FindEntityType(table.EntityType)?.GetTableName() ?? table.Name;
        var count = await source.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = {name}")
            .SingleAsync(ct);
        return count > 0;
    }

    /// <summary>Every metrics table. No foreign keys between them, so any order is safe.</summary>
    internal static readonly IReadOnlyList<Table> MetricsTables =
    [
        T("ContainerMetrics",  c => c.ContainerMetrics,  e => e.Id = 0),
        T("ServerMetrics",     c => c.ServerMetrics,     e => e.Id = 0),
        T("AlertHistory",      c => c.AlertHistory,      e => e.Id = 0),
        // R1-18: an operation names its audit row — the new id of every named row is remembered.
        T("AuditLog",          c => c.AuditLog,
            run => e =>
            {
                if (run.NamedAuditIds.Contains(e.Id)) run.Pending.Add((e, e.Id));
                e.Id = 0;
            },
            run => () =>
            {
                foreach (var (row, oldId) in run.Pending) run.AuditIds[oldId] = row.Id;
                run.Pending.Clear();
            }),
        T("McpToolCalls",      c => c.McpToolCalls,      e => e.Id = 0),
        T("VolumeBackups",     c => c.VolumeBackups,     e => e.Id = 0),
        T("ScheduledTasks",    c => c.ScheduledTasks,    e => e.Id = 0),
        T("TaskRunHistory",    c => c.TaskRunHistory,    e => e.Id = 0),
        T("LogAlertRules",     c => c.LogAlertRules,     e => e.Id = 0),
        T("UpdatePolicies",    c => c.UpdatePolicies,    e => e.Id = 0),
        T("UpdateHistory",     c => c.UpdateHistory,     e => e.Id = 0),
        T("UpdateRollbacks",   c => c.UpdateRollbacks,   e => e.Id = 0),
        T("Webhooks",          c => c.Webhooks,          e => e.Id = 0),
        T("WebhookLogs",       c => c.WebhookLogs,       e => e.Id = 0),
        // R1-10: accepted deliveries by key — their run ids name audit operations, kept as they are.
        T("WebhookDeliveries", c => c.WebhookDeliveries, e => e.Id = 0),
        T("CveFirstSeen",      c => c.CveFirstSeen,      e => e.Id = 0),
        T("Notifications",     c => c.Notifications,     e => e.Id = 0),
        T("SelfMetricSamples", c => c.SelfMetricSamples, e => e.Id = 0),
        T("ActionOutcomes",    c => c.ActionOutcomes,    e => e.Id = 0),
        // R1-12: string keys that approvals and audit rows name — copied as they are.
        T("PolicySnapshots",   c => c.PolicySnapshots,   _ => { }),
        T("PolicyDecisions",   c => c.PolicyDecisions,   _ => { }),
        // R1-14: approvals keep their ids — audit rows and decisions name them.
        T("Approvals",         c => c.Approvals,         _ => { }),
        // R1-18: execution claims keep their operation ids — audit rows, decisions and approvals name them. Their
        // AuditId names the audit row by its new id (null when that row is gone, as retention may have removed it).
        T("Operations",        c => c.Operations,
            run => o => o.AuditId = o.AuditId is { } old && run.AuditIds.TryGetValue(old, out var renumbered) ? renumbered : null),
    ];

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
        // Same database, own history table — mirrors DatabaseRegistration.
        var idSqliteOptions = new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
            .UseSqlite(dataPaths.DbConnectionString, o =>
            {
                o.MigrationsAssembly("Whiskers.Migrations.Sqlite");
                o.MigrationsHistoryTable("__IdentityMigrationsHistory");
            })
            .Options;
        var idPgOptions = new DbContextOptionsBuilder<WhiskersIdentityDbContext>()
            .UseNpgsql(targetConnection, o =>
            {
                o.MigrationsAssembly("Whiskers.Migrations.Postgres");
                o.MigrationsHistoryTable("__IdentityMigrationsHistory");
            })
            .Options;

        await using var source = new MetricsDbContext(sqliteOptions);
        await using var target = new MetricsDbContext(pgOptions);
        await using var idSource = new WhiskersIdentityDbContext(idSqliteOptions);
        await using var idTarget = new WhiskersIdentityDbContext(idPgOptions);

        try
        {
            await log.WriteLineAsync("Bringing the target schema up to date (PostgreSQL migrate)…");
            await target.Database.MigrateAsync(ct);
            await idTarget.Database.MigrateAsync(ct);
            return await MigrateDataAsync(source, target, log, ct, idSource, idTarget);
        }
        catch (Exception ex)
        {
            // No secret leak: the connection string (with its password) is never logged, only the message.
            await log.WriteLineAsync($"MIGRATION FAILED: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Provider-agnostic core: aborts if the target holds any data, otherwise copies every metrics
    /// table (and, when both identity contexts are given, the local users) and prints a per-table row-count
    /// report. Exposed for tests, which drive it with SQLite contexts.</summary>
    public static async Task<int> MigrateDataAsync(MetricsDbContext source, MetricsDbContext target,
        TextWriter log, CancellationToken ct = default,
        WhiskersIdentityDbContext? identitySource = null, WhiskersIdentityDbContext? identityTarget = null)
    {
        var copyIdentity = identitySource is not null && identityTarget is not null;

        var occupied = await FirstNonEmptyTableAsync(target, ct)
                       ?? (copyIdentity ? await FirstNonEmptyIdentityTableAsync(identityTarget!, ct) : null);
        if (occupied is not null)
        {
            await log.WriteLineAsync($"ABORT: the target already contains data (table '{occupied}' is not empty). " +
                "This tool only migrates into a fresh, empty database — it never merges.");
            return 3;
        }

        // The source schema is checked before anything is copied: a missing table would otherwise fail half-way,
        // after earlier tables committed. Only a known older release may lack tables, and only all of a release's
        // tables together (R1-12 added PolicyDecisions and PolicySnapshots, R1-14 Approvals, R1-18 Operations, R1-10 WebhookDeliveries); anything else is not a Whiskers
        // database this tool understands (a wrong path, a damaged file).
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in MetricsTables)
            if (!await SourceHasTableAsync(source, table, ct)) missing.Add(table.Name);
        if (missing.Count > 0 && !OlderReleaseTables.Any(set => set.SetEquals(missing)))
        {
            await log.WriteLineAsync($"ABORT: the source is not a complete Whiskers database (missing: " +
                $"{string.Join(", ", missing.Order(StringComparer.Ordinal))}). Check the path, or start the current " +
                "Whiskers on it once so it migrates its schema, then run this tool again. Nothing was copied.");
            return 4;
        }

        // The audit rows an operation names, read before AuditLog is copied (renumbered) — a handful, not the log.
        var run = new CopyRun
        {
            NamedAuditIds = missing.Contains("Operations")
                ? []
                : (await source.Operations.AsNoTracking().Where(o => o.AuditId != null).Select(o => o.AuditId!.Value)
                    .ToListAsync(ct)).ToHashSet(),
        };
        var report = new List<(string Table, int Rows)>();
        foreach (var table in MetricsTables)
        {
            if (missing.Contains(table.Name))
            {
                await log.WriteLineAsync($"Note: the source has no '{table.Name}' table (older release) — nothing to copy.");
                report.Add((table.Name, 0));
                continue;
            }
            report.Add((table.Name, await table.CopyAsync(source, target, run, ct)));
        }

        if (copyIdentity)
        {
            if ((await identitySource!.Database.GetAppliedMigrationsAsync(ct)).Any())
                report.AddRange(await CopyIdentityAsync(identitySource, identityTarget!, ct));
            else
                await log.WriteLineAsync("Note: the source has no local-login tables (never migrated) — no users to copy.");
        }

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
        foreach (var table in MetricsTables)
            if (await table.AnyAsync(t, ct)) return table.Name;
        return null;
    }

    private static async Task<string?> FirstNonEmptyIdentityTableAsync(WhiskersIdentityDbContext t, CancellationToken ct)
    {
        if (await t.Users.AnyAsync(ct)) return "AspNetUsers";
        if (await t.UserClaims.AnyAsync(ct)) return "AspNetUserClaims";
        if (await t.UserLogins.AnyAsync(ct)) return "AspNetUserLogins";
        if (await t.UserTokens.AnyAsync(ct)) return "AspNetUserTokens";
        return null;
    }

    /// <summary>Users first (claims, logins and tokens reference them), ids and password hashes unchanged.
    /// Only the claims' surrogate int key is reassigned by the target.</summary>
    private static async Task<IReadOnlyList<(string Table, int Rows)>> CopyIdentityAsync(
        WhiskersIdentityDbContext source, WhiskersIdentityDbContext target, CancellationToken ct) =>
    [
        ("AspNetUsers",      await CopyAsync(source.Users,      target, _ => { }, ct)),
        ("AspNetUserClaims", await CopyAsync(source.UserClaims, target, c => c.Id = 0, ct)),
        ("AspNetUserLogins", await CopyAsync(source.UserLogins, target, _ => { }, ct)),
        ("AspNetUserTokens", await CopyAsync(source.UserTokens, target, _ => { }, ct)),
    ];

    /// <summary>Streams one table from the source (never loading it whole) and inserts it into the target in
    /// batches. <paramref name="resetId"/> zeroes the surrogate PK so the target assigns a fresh identity.</summary>
    private static async Task<int> CopyAsync<T>(IQueryable<T> source, DbContext target,
        Action<T> resetId, CancellationToken ct, Action? afterFlush = null) where T : class
    {
        var total = 0;
        var buffer = new List<T>(BatchSize);
        await foreach (var row in source.AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            resetId(row);
            buffer.Add(row);
            if (buffer.Count >= BatchSize)
            {
                total += await FlushAsync(target, buffer, ct);
                afterFlush?.Invoke();
            }
        }
        if (buffer.Count > 0)
        {
            total += await FlushAsync(target, buffer, ct);
            afterFlush?.Invoke();
        }
        return total;
    }

    private static async Task<int> FlushAsync<T>(DbContext target, List<T> buffer, CancellationToken ct)
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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Whiskers.Services.Persistence;
using Whiskers.Services.Backup;

namespace Whiskers.Tests;

/// <summary>
/// The SQLite snapshot of a self-backup (and so of every pre-upgrade backup) uses the online backup API instead of
/// <c>VACUUM INTO</c> (2026-11-12: 1089 s against 52 s for 780 MB on a spinning disk with a cold cache). These tests hold
/// what the snapshot promised before: the committed state including what still sits in the WAL, one file without
/// sidecars, and nothing a writer adds after the snapshot.
/// </summary>
public class SqliteSnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-snap-{Guid.NewGuid():N}");

    public SqliteSnapshotTests() => Directory.CreateDirectory(_dir);

    // Every connection here is unpooled, so nothing outlives a test. No SqliteConnection.ClearAllPools(): that is global
    // and would close idle connections of tests running in parallel (a last close checkpoints their WAL under them).
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MetricsDbContext Context(string db) =>
        new(new DbContextOptionsBuilder<MetricsDbContext>().UseSqlite($"Data Source={db};Pooling=False").Options);

    private static long Count(string db)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False;Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string JournalMode(string db)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        return (string)cmd.ExecuteScalar()!;
    }

    /// <summary>A live database in WAL mode whose rows are still in the WAL: <paramref name="writer"/> stays open with
    /// automatic checkpoints off, as the running app holds it.</summary>
    private static SqliteConnection LiveWalDatabase(string db, int rows)
    {
        var writer = new SqliteConnection($"Data Source={db};Pooling=False");
        writer.Open();
        using var cmd = writer.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);";
        cmd.ExecuteNonQuery();
        for (var i = 0; i < rows; i++)
        {
            cmd.CommandText = $"INSERT INTO t (v) VALUES ('{new string('x', 200)}')";
            cmd.ExecuteNonQuery();
        }
        return writer;
    }

    [Fact]
    public async Task The_snapshot_holds_what_is_still_in_the_wal()
    {
        var live = Path.Combine(_dir, "metrics.db");
        using var writer = LiveWalDatabase(live, rows: 500);
        Assert.True(new FileInfo(live + "-wal").Length > 0, "the test needs rows that are still in the WAL");

        var snapshot = Path.Combine(_dir, "snapshot.db");
        await using (var metrics = Context(live))
            await BackupService.SnapshotSqliteAsync(metrics, snapshot, CancellationToken.None);

        Assert.Equal(500, Count(snapshot));
    }

    [Fact]
    public async Task The_snapshot_is_one_file_with_a_rollback_journal()
    {
        // A restore swaps the file in as it is: no -wal or -shm may be needed next to it, and its header must not ask
        // for a WAL that is not there.
        var live = Path.Combine(_dir, "metrics.db");
        using var writer = LiveWalDatabase(live, rows: 20);

        var snapshot = Path.Combine(_dir, "snapshot.db");
        await using (var metrics = Context(live))
            await BackupService.SnapshotSqliteAsync(metrics, snapshot, CancellationToken.None);

        Assert.False(File.Exists(snapshot + "-wal"));
        Assert.False(File.Exists(snapshot + "-shm"));
        Assert.Equal("delete", JournalMode(snapshot));
    }

    [Fact]
    public async Task A_write_after_the_snapshot_is_not_in_it_and_the_live_database_is_unchanged()
    {
        var live = Path.Combine(_dir, "metrics.db");
        using var writer = LiveWalDatabase(live, rows: 10);

        var snapshot = Path.Combine(_dir, "snapshot.db");
        await using (var metrics = Context(live))
            await BackupService.SnapshotSqliteAsync(metrics, snapshot, CancellationToken.None);

        using (var cmd = writer.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO t (v) VALUES ('later')";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(10, Count(snapshot));
        Assert.Equal(11, Count(live));
        Assert.Equal("wal", JournalMode(live));
    }

    [Fact]
    public async Task A_cancelled_snapshot_writes_nothing()
    {
        var live = Path.Combine(_dir, "metrics.db");
        using var writer = LiveWalDatabase(live, rows: 1);
        var snapshot = Path.Combine(_dir, "snapshot.db");

        await using var metrics = Context(live);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BackupService.SnapshotSqliteAsync(metrics, snapshot, new CancellationToken(canceled: true)));
        Assert.False(File.Exists(snapshot));
    }
}

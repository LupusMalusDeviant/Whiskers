using Microsoft.Data.Sqlite;

namespace Whiskers.Tests;

/// <summary>Releases one test database's pooled connections so its temp file can be deleted.</summary>
/// <remarks><see cref="SqliteConnection.ClearAllPools"/> also tears into the pools of tests running in parallel and made
/// them fail at random (a disposed handle in a transaction rollback); clear only the pool of this database.</remarks>
public static class TestSqlite
{
    /// <param name="path">The file of a database opened with exactly <c>Data Source={path}</c>.</param>
    public static void ClearPool(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>Releases the pools of every database file under <paramref name="directory"/> (an app data dir).</summary>
    /// <remarks>The app names its files <c>{dataDir}/metrics.db</c> (a forward slash on every OS); both spellings are cleared.</remarks>
    public static void ClearPools(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
        {
            ClearPool(file);
            ClearPool(directory + "/" + Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'));
        }
    }
}

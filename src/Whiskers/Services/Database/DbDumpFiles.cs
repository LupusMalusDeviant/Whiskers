using System.Text.RegularExpressions;
using Whiskers.Utils;

namespace Whiskers.Services.Database;

/// <summary>
/// The host files a database dump is written to: <c>{database}_{yyyyMMdd}_{HHmmss}.sql</c>, with <c>.gz</c> for
/// MongoDB (<see cref="DatabaseService.BackupDatabaseAsync"/>). The DB-backup retention and its verifier pick a
/// database's dumps with <see cref="IsDumpOf"/> — the exact name, not a prefix, so pruning <c>app</c> never touches
/// <c>app_db</c>'s recovery points — and both accept exactly the names <see cref="CommandOperands.IsSafeDatabaseName"/>
/// accepts.
/// </summary>
public static partial class DbDumpFiles
{
    /// <summary>True when <paramref name="fileName"/> (a bare file name) is a dump of exactly
    /// <paramref name="database"/>.</summary>
    public static bool IsDumpOf(string fileName, string database)
    {
        if (string.IsNullOrEmpty(fileName) || !CommandOperands.IsSafeDatabaseName(database)) return false;
        var match = DumpName().Match(fileName);
        return match.Success && string.Equals(match.Groups["db"].Value, database, StringComparison.Ordinal);
    }

    /// <summary>The dumps of <paramref name="database"/> that a retention keeping <paramref name="keep"/> removes,
    /// from a listing that is newest first (<c>ls -1t</c>).</summary>
    public static IReadOnlyList<string> ToPrune(string listingNewestFirst, string database, int keep) =>
        (listingNewestFirst ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => IsDumpOf(f, database))
            .Skip(Math.Max(keep, 0))
            .ToList();

    [GeneratedRegex(@"^(?<db>.+)_[0-9]{8}_[0-9]{6}\.sql(?:\.gz)?\z")]
    private static partial Regex DumpName();
}

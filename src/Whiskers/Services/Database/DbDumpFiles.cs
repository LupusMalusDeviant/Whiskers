using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Whiskers.Utils;

namespace Whiskers.Services.Database;

/// <summary>
/// The host files a database dump is written to (<see cref="DatabaseService.BackupDatabaseAsync"/>), all in one backup
/// directory per server:
/// <list type="bullet">
/// <item><c>{container}@{database}_{yyyyMMdd}_{HHmmssfff}_{8 hex}.sql</c> (<c>.sql.gz</c> for MongoDB) since INV-19
/// round 3: the container (<see cref="ContainerKey"/>) and the database name together name the set a retention prunes,
/// so a same-named database in another container is never touched, and the milliseconds plus a random suffix keep two
/// dumps taken in the same second apart. <c>@</c> occurs in neither a container nor a database name, so the split is
/// unambiguous.</item>
/// <item><c>{database}_{yyyyMMdd}_{HHmmss}.sql[.gz]</c>, the format before: it says nothing about the container, so such
/// a file is <b>legacy</b> — still recognised (<see cref="IsLegacyDumpOf"/>), never counted toward a retention's keep
/// and never deleted by one. Removing old dumps is left to an admin.</item>
/// </list>
/// The listing both retention and its verifier read is <c>ls -1Aq</c>: one line per file, with every non-printable
/// character (a line feed included) shown as <c>?</c>, so no file name can pose as another; lines are taken as they are
/// (never trimmed), only exact names count, each once, and they are ordered by the time stamp in the name.
/// </summary>
public static partial class DbDumpFiles
{
    /// <summary>Container names longer than this are keyed by their short id instead, so a dump's file name stays
    /// within 255 characters (64 + database 128 + the fixed parts).</summary>
    public const int MaxContainerKeyLength = 64;

    /// <summary>The container part of a dump's file name: the container's name (stable across a re-create) when it is a
    /// safe, short one, else its 12-character short id. Throws when neither is usable.</summary>
    public static string ContainerKey(string? containerName, string? containerId)
    {
        var name = containerName?.TrimStart('/');
        if (CommandOperands.IsSafeContainerRef(name) && name!.Length <= MaxContainerKeyLength) return name;
        var shortId = containerId is { Length: >= 12 } ? containerId[..12] : containerId;
        if (CommandOperands.IsSafeContainerRef(shortId) && shortId!.Length <= MaxContainerKeyLength) return shortId;
        throw new UnsafeOperandException("container", containerName ?? containerId,
            "a dump needs a container name or id of letters, digits and _ . - (at most 64 characters)");
    }

    /// <summary>A new dump's file name (without <c>.gz</c>) for <paramref name="containerKey"/> and
    /// <paramref name="database"/> at <paramref name="utcNow"/>; unique even within one millisecond.</summary>
    public static string NewFileName(string containerKey, string database, DateTime utcNow)
    {
        if (!CommandOperands.IsSafeContainerRef(containerKey) || containerKey.Length > MaxContainerKeyLength)
            throw new UnsafeOperandException("container", containerKey, null);
        CommandOperands.RequireDatabaseName(database);
        var stamp = utcNow.ToString("yyyyMMdd'_'HHmmssfff", CultureInfo.InvariantCulture);
        return $"{containerKey}@{database}_{stamp}_{RandomNumberGenerator.GetHexString(8, lowercase: true)}.sql";
    }

    /// <summary>True when <paramref name="fileName"/> (a bare file name) is a dump, in the current format, of exactly
    /// <paramref name="database"/> in exactly the container keyed <paramref name="containerKey"/>.</summary>
    public static bool IsDumpOf(string fileName, string containerKey, string database)
    {
        if (string.IsNullOrEmpty(fileName) || !CommandOperands.IsSafeDatabaseName(database)
            || !CommandOperands.IsSafeContainerRef(containerKey)) return false;
        var match = DumpName().Match(fileName);
        return match.Success
            && string.Equals(match.Groups["c"].Value, containerKey, StringComparison.Ordinal)
            && string.Equals(match.Groups["db"].Value, database, StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="fileName"/> is a dump of <paramref name="database"/> in the format before the
    /// container was part of the name (<c>{database}_{yyyyMMdd}_{HHmmss}.sql[.gz]</c>). Never pruned.</summary>
    public static bool IsLegacyDumpOf(string fileName, string database)
    {
        if (string.IsNullOrEmpty(fileName) || !CommandOperands.IsSafeDatabaseName(database)) return false;
        var match = LegacyDumpName().Match(fileName);
        return match.Success && string.Equals(match.Groups["db"].Value, database, StringComparison.Ordinal);
    }

    /// <summary>The current-format dumps of the container and database in an <c>ls -1Aq</c> listing, each once,
    /// newest first (by the time stamp and suffix in the name).</summary>
    public static IReadOnlyList<string> DumpsOf(string? listing, string containerKey, string database) =>
        (listing ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(f => IsDumpOf(f, containerKey, database))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(f => f, StringComparer.Ordinal)   // same prefix: ordinal order is time-stamp order
            .ToList();

    /// <summary>The dumps a retention keeping <paramref name="keep"/> removes: the current-format dumps of the
    /// container and database beyond the newest <paramref name="keep"/>. Legacy dumps are never among them.</summary>
    public static IReadOnlyList<string> ToPrune(string? listing, string containerKey, string database, int keep) =>
        DumpsOf(listing, containerKey, database).Skip(Math.Max(keep, 0)).ToList();

    /// <summary>The listing command both retention and its verifier run (see the type's remarks).</summary>
    public static string ListingCommand(string backupDir) => $"ls -1Aq -- {ShellUtils.Quote(backupDir)}";

    [GeneratedRegex(@"^(?<c>[A-Za-z0-9][A-Za-z0-9_.-]*)@(?<db>[A-Za-z0-9][A-Za-z0-9_.-]*)_[0-9]{8}_[0-9]{9}_[0-9a-f]{8}\.sql(?:\.gz)?\z")]
    private static partial Regex DumpName();

    [GeneratedRegex(@"^(?<db>[A-Za-z0-9][A-Za-z0-9_.-]*)_[0-9]{8}_[0-9]{6}\.sql(?:\.gz)?\z")]
    private static partial Regex LegacyDumpName();
}

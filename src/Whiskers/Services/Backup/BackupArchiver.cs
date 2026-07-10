using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace Whiskers.Services.Backup;

/// <summary>A single file to place into a backup archive: the on-disk source and its entry name in the tar.</summary>
public readonly record struct PackEntry(string Name, string SourcePath);

/// <summary>Result of inspecting an archive without extracting it: the manifest (if present) and the entry
/// names, having already passed the same zip-slip / entry-type guards that extraction enforces.</summary>
public sealed class BackupInspection
{
    public SelfBackupManifest? Manifest { get; init; }
    public IReadOnlyList<string> EntryNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Low-level tar.gz pack/unpack for self-backups, with hardened extraction. Both the extract and the
/// inspect passes reject any entry that is not a regular file or directory (no symlinks/hardlinks/devices —
/// a symlink entry is a write-outside-root vector) and canonicalise every path to enforce containment
/// under the destination root (zip-slip). Encryption is layered separately by <see cref="BackupArchiveCipher"/>.
/// </summary>
public static class BackupArchiver
{
    /// <summary>Writes the given entries as a gzip-compressed tar into <paramref name="tarGzDestination"/>
    /// (not disposed). Entry order is preserved, so callers put <c>manifest.json</c> first for fast reads.</summary>
    public static async Task PackAsync(Stream tarGzDestination, IReadOnlyList<PackEntry> entries, CancellationToken ct = default)
    {
        await using var gzip = new GZipStream(tarGzDestination, CompressionLevel.Optimal, leaveOpen: true);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            await tar.WriteEntryAsync(e.SourcePath, e.Name, ct);
        }
    }

    /// <summary>Extracts a gzip tar into <paramref name="destinationRoot"/> with entry-type and zip-slip
    /// guards. Parent directories are created as needed; existing files are overwritten.</summary>
    public static async Task ExtractAsync(Stream tarGzSource, string destinationRoot, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationRoot);
        var rootFull = Path.GetFullPath(destinationRoot);

        await using var gzip = new GZipStream(tarGzSource, CompressionMode.Decompress, leaveOpen: true);
        await using var tar = new TarReader(gzip, leaveOpen: true);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            GuardEntryType(entry);
            var target = ResolveWithinRoot(rootFull, entry.Name);
            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await entry.ExtractToFileAsync(target, overwrite: true, ct);
        }
    }

    /// <summary>Single decompression pass that enforces the extraction guards on every entry (throwing on a
    /// violation) and captures the manifest — used to validate an uploaded archive before touching live state.
    /// <paramref name="virtualRoot"/> is only used for the containment math; nothing is written.</summary>
    public static async Task<BackupInspection> InspectAsync(Stream tarGzSource, string virtualRoot, CancellationToken ct = default)
    {
        var rootFull = Path.GetFullPath(virtualRoot);
        var names = new List<string>();
        SelfBackupManifest? manifest = null;

        await using var gzip = new GZipStream(tarGzSource, CompressionMode.Decompress, leaveOpen: true);
        await using var tar = new TarReader(gzip, leaveOpen: true);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            GuardEntryType(entry);
            ResolveWithinRoot(rootFull, entry.Name);   // throws on escape
            names.Add(entry.Name);

            if (manifest is null
                && string.Equals(entry.Name, SelfBackupFormat.ManifestEntryName, StringComparison.Ordinal)
                && entry.DataStream is { } ds)
            {
                manifest = await JsonSerializer.DeserializeAsync<SelfBackupManifest>(ds, cancellationToken: ct);
            }
        }

        return new BackupInspection { Manifest = manifest, EntryNames = names };
    }

    private static void GuardEntryType(TarEntry entry)
    {
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
            throw new InvalidDataException(
                $"Refusing tar entry '{entry.Name}' of disallowed type {entry.EntryType} — only regular files and directories are permitted.");
    }

    private static string ResolveWithinRoot(string rootFull, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new InvalidDataException("Refusing tar entry with an empty name.");

        var combined = Path.GetFullPath(Path.Combine(rootFull, entryName));
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (combined != rootFull && !combined.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new InvalidDataException($"Refusing tar entry '{entryName}' that escapes the extraction root.");

        return combined;
    }
}

using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;

namespace Whiskers.Services.Backup;

/// <summary>A single file to place into a backup archive: the on-disk source and its entry name in the tar.</summary>
public readonly record struct PackEntry(string Name, string SourcePath);

/// <summary>Result of inspecting an archive without extracting it: the manifest (if present) and the entry
/// names, having already passed the same guards that extraction enforces.</summary>
public sealed class BackupInspection
{
    public SelfBackupManifest? Manifest { get; init; }
    public IReadOnlyList<string> EntryNames { get; init; } = Array.Empty<string>();

    /// <summary>Entries that are not restore targets (<see cref="BackupEntryKind.NotRestored"/>): never extracted.</summary>
    public IReadOnlyList<string> SkippedEntries { get; init; } = Array.Empty<string>();

    /// <summary>Whether the archive holds the SQLite database.</summary>
    public bool HasDatabase { get; init; }

    /// <summary>The content bytes of the entries a restore extracts.</summary>
    public long ExtractedBytes { get; init; }
}

/// <summary>Bounds on what one archive may unpack to (R1-32): a small upload must fill neither the disk nor the memory.
/// <paramref name="MaxJsonEntryBytes"/> bounds each JSON store at the root (read whole into memory; the manifest has its
/// own, smaller bound); <paramref name="MaxNameChars"/> bounds the characters of all names and the directories they
/// imply together (they live in the headers, outside the content bound).</summary>
public sealed record BackupExtractionLimits(int MaxEntries, long MaxTotalBytes, long MaxJsonEntryBytes = 256L << 20,
    long MaxNameChars = 64L << 20)
{
    /// <summary>100k entries and 32 GiB of content — far above a real data directory.</summary>
    public static BackupExtractionLimits Default { get; } = new(100_000, 32L << 30);

    // Tar headers and padding on top of the content: 1.5 KiB per entry plus a flat 16 MiB.
    internal long MaxDecompressedBytes => MaxTotalBytes + MaxEntries * 1536L + (16L << 20);

    // What reading one entry's headers may take (PAX records and long names are held in memory whole).
    internal const long MaxHeaderBytes = 1L << 20;

    /// <summary>A data-protection key file: loaded whole at validation and at every start.</summary>
    public const long MaxKeyEntryBytes = 1L << 20;

    /// <summary>The manifest: a few KiB in practice, read whole.</summary>
    public const long MaxManifestBytes = 1L << 20;

    /// <summary>An entry name (PATH_MAX) and its depth: far above any data file, and bounding the name bookkeeping.</summary>
    public const int MaxNameLength = 4096;
    public const int MaxNameDepth = 32;
}

/// <summary>
/// Low-level tar.gz pack/unpack for self-backups, with hardened extraction. Both the extract and the inspect passes
/// reject any entry that is not a regular file or directory (no symlinks/hardlinks/devices — a symlink entry is a
/// write-outside-root vector), canonicalise every path to enforce containment under the destination root
/// (zip-slip), and (R1-32) refuse forbidden or conflicting entries (<see cref="SelfBackupLayout"/>) and archives
/// that unpack to more entries or bytes than <see cref="BackupExtractionLimits"/> allow. Entries that are not restore
/// targets are skipped. Encryption is layered separately by <see cref="BackupArchiveCipher"/>.
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

    /// <summary>Extracts the restore targets of a gzip tar into <paramref name="destinationRoot"/> with all guards.
    /// Parent directories are created as needed; files are created readable by the owner only.</summary>
    public static async Task ExtractAsync(Stream tarGzSource, string destinationRoot, BackupExtractionLimits? limits = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationRoot);
        var rootFull = Path.GetFullPath(destinationRoot);

        await ReadAsync(tarGzSource, rootFull, limits ?? BackupExtractionLimits.Default, async (entry, kind, target) =>
        {
            if (kind is BackupEntryKind.NotRestored) return;
            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await WriteOwnerOnlyAsync(entry, target, ct);
        }, ct);
    }

    /// <summary>Single decompression pass that enforces the extraction guards on every entry (throwing on a
    /// violation) and captures the manifest — used to validate an uploaded archive before touching live state.
    /// <paramref name="virtualRoot"/> is only used for the containment math; nothing is written.</summary>
    public static async Task<BackupInspection> InspectAsync(Stream tarGzSource, string virtualRoot, BackupExtractionLimits? limits = null,
        CancellationToken ct = default)
    {
        var rootFull = Path.GetFullPath(virtualRoot);
        var names = new List<string>();
        var skipped = new List<string>();
        var hasDatabase = false;
        long extracted = 0;
        SelfBackupManifest? manifest = null;

        await ReadAsync(tarGzSource, rootFull, limits ?? BackupExtractionLimits.Default, async (entry, kind, _) =>
        {
            names.Add(entry.Name);
            if (kind is BackupEntryKind.NotRestored) skipped.Add(entry.Name);
            else if (entry.EntryType is not TarEntryType.Directory) extracted += entry.Length;
            if (kind is BackupEntryKind.File
                && string.Equals(SelfBackupLayout.Normalize(entry.Name), SelfBackupFormat.DbEntryName, StringComparison.Ordinal))
                hasDatabase = true;
            if (kind is BackupEntryKind.Manifest && entry.DataStream is { } ds)
            {
                try
                {
                    manifest = await JsonSerializer.DeserializeAsync<SelfBackupManifest>(ds, cancellationToken: ct);
                    if (manifest is not null)   // an explicit null overrides the empty defaults
                    {
                        manifest.MetricsMigrations ??= [];
                        manifest.IdentityMigrations ??= [];
                    }
                }
                catch (JsonException) { throw new InvalidDataException("Refusing an archive whose manifest.json is not valid."); }
            }
        }, ct);

        return new BackupInspection
        {
            Manifest = manifest, EntryNames = names, SkippedEntries = skipped, HasDatabase = hasDatabase, ExtractedBytes = extracted,
        };
    }

    private static async Task ReadAsync(Stream tarGzSource, string rootFull, BackupExtractionLimits limits,
        Func<TarEntry, BackupEntryKind, string, Task> onEntry, CancellationToken ct)
    {
        await using var gzip = new GZipStream(tarGzSource, CompressionMode.Decompress, leaveOpen: true);
        await using var bounded = new BoundedReadStream(gzip, limits.MaxDecompressedBytes);
        await using var screen = new TarHeaderScreen(bounded, BackupExtractionLimits.MaxHeaderBytes);
        await using var tar = new TarReader(screen, leaveOpen: true);

        // Every name seen (case-insensitive: the data dir may live on a case-insensitive file system, where two
        // spellings are one file) and the directories they imply: a second entry for one path, or a file where
        // another entry needs a directory, would make the result depend on the entry order.
        var names = new NameLedger(limits.MaxNameChars);
        var count = 0;
        var remaining = limits.MaxTotalBytes;
        long previousLength = 0;

        while (true)
        {
            // Moving to the next entry skips what is left of the previous one's data and reads the next headers.
            bounded.Window = previousLength + 512 + BackupExtractionLimits.MaxHeaderBytes;
            var entry = await tar.GetNextEntryAsync(cancellationToken: ct);
            bounded.Window = long.MaxValue;
            if (entry is null) break;

            if (++count > limits.MaxEntries)
                throw new InvalidDataException($"Refusing an archive with more than {limits.MaxEntries} entries.");
            GuardEntryType(entry);
            // A backslash is a separator on Windows and a name character on Linux: one archive, two meanings.
            if (entry.Name.Contains('\\'))
                throw new InvalidDataException($"Refusing tar entry '{entry.Name}': backslashes are not allowed in names.");
            if (entry.Name.Length > BackupExtractionLimits.MaxNameLength || entry.Name.Count(c => c == '/') >= BackupExtractionLimits.MaxNameDepth)
                throw new InvalidDataException("Refusing a tar entry whose name is too long or too deep.");
            var isDirectory = entry.EntryType is TarEntryType.Directory;
            if (!isDirectory && entry.Name.EndsWith('/'))
                throw new InvalidDataException($"Refusing tar entry '{entry.Name}': a file named like a directory.");
            var target = ResolveWithinRoot(rootFull, entry.Name);
            // The name must already be the path it lands on: "chat/../roles.json", "./x", "a//b" or (on Windows) a
            // trailing dot would reach one file under two names and slip past the list and the duplicate check.
            var name = SelfBackupLayout.Normalize(entry.Name);
            if (!string.Equals(Path.GetRelativePath(rootFull, target).Replace('\\', '/').TrimEnd('/'), name, StringComparison.Ordinal))
                throw new InvalidDataException($"Refusing tar entry '{entry.Name}': its name is not in canonical form.");
            var kind = SelfBackupLayout.Classify(name, isDirectory);
            if (kind is BackupEntryKind.Forbidden)
                throw new InvalidDataException($"Refusing tar entry '{entry.Name}': it is not something a restore may write.");
            names.Track(name, isDirectory);

            // The header's length is what the entry's data stream yields — no more is read for it.
            if (!isDirectory && (remaining -= entry.Length) < 0)
                throw new InvalidDataException($"Refusing an archive that unpacks to more than {limits.MaxTotalBytes} bytes.");
            if (kind is BackupEntryKind.Manifest && entry.Length > BackupExtractionLimits.MaxManifestBytes)
                throw new InvalidDataException($"Refusing tar entry '{entry.Name}': larger than {BackupExtractionLimits.MaxManifestBytes} bytes.");
            if (kind is BackupEntryKind.File && name.EndsWith(".json", StringComparison.Ordinal) && entry.Length > limits.MaxJsonEntryBytes)
                throw new InvalidDataException($"Refusing tar entry '{entry.Name}': larger than {limits.MaxJsonEntryBytes} bytes.");
            if (kind is BackupEntryKind.Directory && !isDirectory && name.StartsWith(SelfBackupLayout.KeyRing + "/", StringComparison.Ordinal)
                && entry.Length > BackupExtractionLimits.MaxKeyEntryBytes)
                throw new InvalidDataException($"Refusing tar entry '{entry.Name}': larger than a data-protection key can be.");
            previousLength = isDirectory ? 0 : entry.Length;
            await onEntry(entry, kind, target);
        }
    }

    /// <summary>Every name of one archive (case-insensitive: the data dir may live on a case-insensitive file system,
    /// where two spellings are one file) and the directories they imply. A second entry for one path, or a file where
    /// another entry needs a directory, would make the result depend on the entry order.</summary>
    private sealed class NameLedger(long maxChars)
    {
        private long _chars;
        private readonly HashSet<string> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

        public void Track(string name, bool isDirectory)
        {
            // The name and each directory it implies, counted before any of them is kept.
            _chars += (long)name.Length * (name.Count(c => c == '/') + 1);
            if (_chars > maxChars)
                throw new InvalidDataException("Refusing an archive whose entry names are larger than allowed together.");
            if (!_entries.Add(name))
                throw new InvalidDataException($"Refusing an archive with more than one entry for '{name}'.");
            if (!isDirectory && _directories.Contains(name))
                throw new InvalidDataException($"Refusing an archive where '{name}' is both a file and a directory.");
            for (var i = name.IndexOf('/'); i > 0; i = name.IndexOf('/', i + 1))
            {
                var parent = name[..i];
                if (_files.Contains(parent))
                    throw new InvalidDataException($"Refusing an archive where '{parent}' is both a file and a directory.");
                _directories.Add(parent);
            }
            if (isDirectory) _directories.Add(name);
            else _files.Add(name);
        }
    }

    private static async Task WriteOwnerOnlyAsync(TarEntry entry, string target, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // never the mode the archive claims
        await using var output = new FileStream(target, options);
        if (entry.DataStream is { } data)
            await data.CopyToAsync(output, ct);
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

    /// <summary>Fails a read that goes past <c>limit</c> decompressed bytes (a gzip bomb never fully inflates).</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;
        private long _window = long.MaxValue;

        /// <summary>Bytes the next reads may take before they fail; reset by each assignment.</summary>
        public long Window { set => _window = value; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => Count(await inner.ReadAsync(buffer, ct));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Count(int read)
        {
            _read += read;
            if (_read > limit)
                throw new InvalidDataException("Refusing an archive that unpacks to more bytes than allowed.");
            if (_window != long.MaxValue && (_window -= read) < 0)
                throw new InvalidDataException("Refusing an archive whose entry headers are larger than allowed.");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

using System.Text;

namespace Whiskers.Services.Persistence;

/// <summary>
/// Publishes a file so a reader never sees it half written and a power loss never leaves it half written (R1-35,
/// TA-39): the content goes to a temporary file of its own in the same directory, is flushed to the disk, and only then
/// takes the target's name by one rename. Optionally the file that was in place is kept first as <c>&lt;file&gt;.bak</c>
/// (the previous generation, itself published the same way), for <see cref="JsonFileStore{T}"/> to fall back to.
/// The directory entry itself is not flushed (.NET has no portable way); a crash right after the rename can leave the
/// previous name in place, never a partial file. On Unix the new file (and the kept one) gets the permissions of the
/// file it replaces, so an owner-only file such as <c>vault.json</c> stays owner-only.
/// </summary>
public static class DurableFile
{
    /// <summary>The suffix of the previous generation.</summary>
    public const string BackupSuffix = ".bak";

    public static async Task WriteAllTextAsync(string path, string content, bool keepPrevious = false, CancellationToken ct = default)
    {
        ThrowIfFailingForTests(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = TempName(path);
        var mode = ModeOf(path);
        try
        {
            await WriteFlushedAsync(temp, Encoding.UTF8.GetBytes(content), mode, ct);
            if (keepPrevious && File.Exists(path))
            {
                var backupTemp = TempName(path + BackupSuffix);
                try
                {
                    await WriteFlushedAsync(backupTemp, await File.ReadAllBytesAsync(path, ct), mode, ct);
                    File.Move(backupTemp, path + BackupSuffix, overwrite: true);
                }
                finally { TryDelete(backupTemp); }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { TryDelete(temp); }
    }

    /// <summary>The synchronous form, for writers that run under a synchronous lock (no previous generation).</summary>
    public static void WriteAllText(string path, string content)
    {
        ThrowIfFailingForTests(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = TempName(path);
        try
        {
            using (var stream = new FileStream(temp, Options(ModeOf(path), FileOptions.None)))
            {
                stream.Write(Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { TryDelete(temp); }
    }

    // The mode of the file replaced, or of its previous generation when the file itself is gone (a recovery), so an
    // owner-only store does not come back readable by others.
    private static UnixFileMode? ModeOf(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        if (File.Exists(path)) return File.GetUnixFileMode(path);
        return File.Exists(path + BackupSuffix) ? File.GetUnixFileMode(path + BackupSuffix) : null;
    }

    private static void ThrowIfFailingForTests(string path)
    {
        if (!FailingWrites.IsEmpty && FailingWrites.ContainsKey(Path.GetFullPath(path)))
            throw new IOException($"{path} cannot be written.");
    }

    private static FileStreamOptions Options(UnixFileMode? mode, FileOptions options)
    {
        var result = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = options,
        };
        if (mode is { } m && !OperatingSystem.IsWindows()) result.UnixCreateMode = m;
        return result;
    }

    /// <summary>Tests only: full paths whose writes fail as on a full or read-only disk (empty in production).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> FailingWrites = new(StringComparer.Ordinal);

    /// <summary>The suffix of the temporary files this class writes (swept at the start, see RestoreBootHandler).</summary>
    public const string TempSuffix = ".durable.tmp";

    // A name of its own per write: two writers never share a temporary file.
    private static string TempName(string path) => $"{path}.{Guid.NewGuid():N}{TempSuffix}";

    private static async Task WriteFlushedAsync(string path, byte[] bytes, UnixFileMode? mode, CancellationToken ct)
    {
        await using var stream = new FileStream(path, Options(mode, FileOptions.Asynchronous));
        await stream.WriteAsync(bytes, ct);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

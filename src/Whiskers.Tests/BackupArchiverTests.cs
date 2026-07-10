using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Whiskers.Services.Backup;

namespace Whiskers.Tests;

/// <summary>tar.gz pack/extract/inspect for self-backups, and the hardened extraction guards (zip-slip path
/// traversal and disallowed entry types such as symlinks).</summary>
public class BackupArchiverTests
{
    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"sw-arch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Pack_then_extract_round_trips_files_and_dirs()
    {
        var src = NewTempDir();
        var dest = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(src, "manifest.json"), "{}");
            Directory.CreateDirectory(Path.Combine(src, "keys"));
            await File.WriteAllTextAsync(Path.Combine(src, "keys", "k1.txt"), "secret");

            var entries = new List<PackEntry>
            {
                new("manifest.json", Path.Combine(src, "manifest.json")),
                new("keys/k1.txt", Path.Combine(src, "keys", "k1.txt")),
            };

            using var archive = new MemoryStream();
            await BackupArchiver.PackAsync(archive, entries);
            archive.Position = 0;

            await BackupArchiver.ExtractAsync(archive, dest);
            Assert.True(File.Exists(Path.Combine(dest, "manifest.json")));
            Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(dest, "keys", "k1.txt")));
        }
        finally { Cleanup(src, dest); }
    }

    [Fact]
    public async Task Inspect_reads_manifest_and_lists_entries()
    {
        var src = NewTempDir();
        try
        {
            var manifest = new SelfBackupManifest { Provider = "sqlite", Label = "x" };
            await File.WriteAllTextAsync(Path.Combine(src, "manifest.json"), JsonSerializer.Serialize(manifest));
            await File.WriteAllTextAsync(Path.Combine(src, "roles.json"), "[]");

            using var archive = new MemoryStream();
            await BackupArchiver.PackAsync(archive, new List<PackEntry>
            {
                new("manifest.json", Path.Combine(src, "manifest.json")),
                new("roles.json", Path.Combine(src, "roles.json")),
            });
            archive.Position = 0;

            var result = await BackupArchiver.InspectAsync(archive, NewTempDir());
            Assert.NotNull(result.Manifest);
            Assert.Equal("sqlite", result.Manifest!.Provider);
            Assert.Contains("roles.json", result.EntryNames);
        }
        finally { Cleanup(src); }
    }

    [Fact]
    public async Task Extract_rejects_path_traversal_entry()
    {
        using var archive = await BuildTarGz(w =>
        {
            var e = new PaxTarEntry(TarEntryType.RegularFile, "../escape.txt");
            e.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
            w.WriteEntry(e);
        });
        var dest = NewTempDir();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.ExtractAsync(archive, dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Extract_rejects_symlink_entry()
    {
        using var archive = await BuildTarGz(w =>
            w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc/passwd" }));
        var dest = NewTempDir();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.ExtractAsync(archive, dest));
        }
        finally { Cleanup(dest); }
    }

    private static async Task<MemoryStream> BuildTarGz(Action<TarWriter> write)
    {
        var ms = new MemoryStream();
        await using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        await using (var tw = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
            write(tw);
        ms.Position = 0;
        return ms;
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var d in dirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }
}

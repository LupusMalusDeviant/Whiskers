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
            await File.WriteAllTextAsync(Path.Combine(src, "keys", "k1.xml"), "secret");

            var entries = new List<PackEntry>
            {
                new("manifest.json", Path.Combine(src, "manifest.json")),
                new("keys/k1.xml", Path.Combine(src, "keys", "k1.xml")),
            };

            using var archive = new MemoryStream();
            await BackupArchiver.PackAsync(archive, entries);
            archive.Position = 0;

            await BackupArchiver.ExtractAsync(archive, dest);
            Assert.True(File.Exists(Path.Combine(dest, "manifest.json")));
            Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(dest, "keys", "k1.xml")));
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

    // ---------------------------------------------------------------- R1-32: what a restore may write

    private static void Entry(TarWriter w, string name, int size = 1)
        => w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(new byte[size]) });

    [Theory]
    [InlineData("backups/self/pre-restore.tar.gz.enc")]
    [InlineData("backups")]
    [InlineData(".restore-pending")]
    [InlineData(".restore-staging/roles.json")]
    [InlineData("metrics.db-wal")]
    [InlineData("metrics.db-shm")]
    [InlineData("roles.json.tmp")]
    [InlineData("chat/session.json.tmp")]
    [InlineData("vault.key")]
    [InlineData("keys")]                  // a file where the key ring directory belongs
    [InlineData("roles.json/inner.json")] // something below a known file
    public async Task Extract_and_inspect_refuse_an_entry_a_restore_may_never_write(string name)
    {
        var dest = NewTempDir();
        try
        {
            using (var archive = await BuildTarGz(w => Entry(w, name)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest));
            using (var archive = await BuildTarGz(w => Entry(w, name)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.ExtractAsync(archive, dest));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
        }
        finally { Cleanup(dest); }
    }

    [Theory]
    [InlineData("roles.json", "roles.json")]
    [InlineData("roles.json", "./roles.json")]
    [InlineData("chat/a.json", "CHAT/A.json")]          // one file on a case-insensitive file system
    [InlineData("chat/a", "chat/a/b.json")]             // a file and a directory under one name
    [InlineData("chat/a/b.json", "chat/a")]
    public async Task An_archive_with_duplicate_or_conflicting_entries_is_refused(string first, string second)
    {
        var dest = NewTempDir();
        try
        {
            using var archive = await BuildTarGz(w => { Entry(w, first); Entry(w, second); });
            await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest));
        }
        finally { Cleanup(dest); }
    }

    [Theory]
    [InlineData("chat/../roles.json")]       // would land on roles.json under the name of the chat directory
    [InlineData("chat/../metrics.db")]
    [InlineData("chat//a.json")]
    [InlineData("chat/./a.json")]
    [InlineData("metrics.db\\")]            // a separator on Windows, part of the name on Linux
    [InlineData("chat\\a.json")]
    public async Task An_entry_whose_name_is_not_the_path_it_lands_on_is_refused(string name)
    {
        var dest = NewTempDir();
        try
        {
            using (var archive = await BuildTarGz(w => Entry(w, name)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest));
            using (var archive = await BuildTarGz(w => Entry(w, name)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.ExtractAsync(archive, dest));
            Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Entry_headers_and_json_stores_larger_than_allowed_are_refused_before_they_are_held_in_memory()
    {
        var dest = NewTempDir();
        try
        {
            // A PAX record of 2 MiB (the reader holds it whole) on an otherwise tiny entry.
            using (var archive = await BuildTarGz(w => w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "roles.json",
                       new Dictionary<string, string> { ["comment"] = new string('x', 2 << 20) }) { DataStream = new MemoryStream(new byte[2]) })))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest));

            var limits = BackupExtractionLimits.Default with { MaxJsonEntryBytes = 1024 };
            using (var archive = await BuildTarGz(w => Entry(w, "manifest.json", size: 4096)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest, limits));
            using (var archive = await BuildTarGz(w => Entry(w, "servers.json", size: 4096)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest, limits));
            using (var archive = await BuildTarGz(w => Entry(w, "chat/long.json", size: 4096)))   // read in parts, not whole
                await BackupArchiver.InspectAsync(archive, dest, limits);

            // A key file is loaded whole at every start.
            using (var archive = await BuildTarGz(w => Entry(w, "keys/key-1.xml", size: (int)BackupExtractionLimits.MaxKeyEntryBytes + 1)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task An_archive_with_more_entries_than_allowed_is_refused()
    {
        var dest = NewTempDir();
        try
        {
            using var archive = await BuildTarGz(w => { for (var i = 0; i < 5; i++) Entry(w, $"chat/{i}.json"); });
            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => BackupArchiver.ExtractAsync(archive, dest, new BackupExtractionLimits(MaxEntries: 4, MaxTotalBytes: 1 << 20)));
            Assert.Contains("entries", ex.Message);
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task An_archive_that_unpacks_to_more_bytes_than_allowed_is_refused_before_it_is_written()
    {
        var dest = NewTempDir();
        try
        {
            // 64 KiB of zeros compress to almost nothing: a small upload, a large unpack.
            using (var archive = await BuildTarGz(w => Entry(w, "roles.json", size: 64 << 10)))
                Assert.True(archive.Length < 4096);
            var limits = new BackupExtractionLimits(MaxEntries: 10, MaxTotalBytes: 16 << 10);
            using (var archive = await BuildTarGz(w => Entry(w, "roles.json", size: 64 << 10)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest, limits));
            using (var archive = await BuildTarGz(w => Entry(w, "roles.json", size: 64 << 10)))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.ExtractAsync(archive, dest, limits));
            Assert.False(File.Exists(Path.Combine(dest, "roles.json")));

            // The bound counts the whole archive, not each entry.
            using (var archive = await BuildTarGz(w => { for (var i = 0; i < 3; i++) Entry(w, $"chat/{i}.json", size: 8 << 10); }))
                await Assert.ThrowsAsync<InvalidDataException>(() => BackupArchiver.InspectAsync(archive, dest, limits));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Entries_that_are_not_restore_targets_are_reported_and_never_written()
    {
        var dest = NewTempDir();
        try
        {
            using (var archive = await BuildTarGz(w => { Entry(w, "roles.json"); Entry(w, "notes.txt"); Entry(w, "setup-token.txt"); }))
            {
                var inspection = await BackupArchiver.InspectAsync(archive, dest);
                Assert.Equal(["notes.txt", "setup-token.txt"], inspection.SkippedEntries);
            }
            using (var archive = await BuildTarGz(w => { Entry(w, "roles.json"); Entry(w, "notes.txt"); Entry(w, "setup-token.txt"); }))
                await BackupArchiver.ExtractAsync(archive, dest);
            Assert.True(File.Exists(Path.Combine(dest, "roles.json")));
            Assert.False(File.Exists(Path.Combine(dest, "notes.txt")));
            Assert.False(File.Exists(Path.Combine(dest, "setup-token.txt")));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Extracted_files_are_readable_by_the_owner_only_whatever_mode_the_archive_claims()
    {
        if (OperatingSystem.IsWindows()) return;   // no file modes
        var dest = NewTempDir();
        try
        {
            using var archive = await BuildTarGz(w => w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "ssh-keys/id")
            {
                DataStream = new MemoryStream(new byte[4]),
                Mode = (UnixFileMode)0b111_111_111,
            }));
            await BackupArchiver.ExtractAsync(archive, dest);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dest, "ssh-keys", "id")));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public async Task Only_the_key_files_of_the_key_ring_are_restored()
    {
        var dest = NewTempDir();
        try
        {
            using (var archive = await BuildTarGz(w => { Entry(w, "keys/key-1.xml"); Entry(w, "keys/notes.txt"); Entry(w, "keys/sub/k.xml"); }))
                await BackupArchiver.ExtractAsync(archive, dest);
            Assert.Equal(["key-1.xml"], Directory.GetFiles(Path.Combine(dest, "keys"), "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).ToArray());
            Assert.Equal(BackupEntryKind.NotRestored, SelfBackupLayout.Classify("keys/notes.txt", isDirectory: false));
        }
        finally { Cleanup(dest); }
    }

    [Fact]
    public void Every_store_checked_by_type_is_a_restore_target()
    {
        // A misspelt name would check nothing.
        Assert.All(StagedRestoreValidator.TypedStores, s => Assert.Contains(s, SelfBackupLayout.Files));
    }

    [Fact]
    public void Every_data_path_is_either_a_restore_target_or_named_as_not_restored()
    {
        // A new store in DataPathOptions must be placed here on purpose: backed up and restored, or not.
        var paths = new Whiskers.Configuration.DataPathOptions("/r");
        var known = SelfBackupLayout.Files.Concat(SelfBackupLayout.Directories).Concat(SelfBackupLayout.NotRestored.Keys).ToHashSet();
        foreach (var property in typeof(Whiskers.Configuration.DataPathOptions).GetProperties())
        {
            if (property.PropertyType != typeof(string) || property.GetMethod!.IsStatic) continue;
            if (property.Name is nameof(paths.RootDir) or nameof(paths.DbConnectionString)) continue;
            var relative = ((string)property.GetValue(paths)!)["/r/".Length..];
            Assert.True(known.Contains(relative) || known.Contains(relative.Split('/')[0]), $"{property.Name} ({relative}) is not placed");
        }
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

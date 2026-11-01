using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Services.Auth;
using Whiskers.Services.Backup;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

// R1-35 (TA-39, D-09): a JSON store is never published half written, an unreadable one comes back from its previous
// generation visibly and with the original kept, and without a readable generation the load stops instead of
// handing out an empty (more permissive) store.
public class JsonFileStoreRecoveryTests : IDisposable
{
    public sealed class Doc
    {
        public int Version { get; set; }
        public List<string> Items { get; set; } = new();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sw-tests", "store-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_dir, "roles.json");

    public JsonFileStoreRecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(_dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static Doc Make(int version) => new() { Version = version, Items = Enumerable.Range(0, 200).Select(i => $"item-{version}-{i}").ToList() };

    private string[] Leftovers() => Directory.EnumerateFiles(_dir).Select(Path.GetFileName).Where(n => n!.EndsWith(".tmp", StringComparison.Ordinal)).ToArray()!;

    [Fact]
    public async Task Concurrent_saves_from_two_instances_leave_one_whole_file_and_no_temporaries()
    {
        var a = new JsonFileStore<Doc>(StorePath);
        var b = new JsonFileStore<Doc>(StorePath);

        await Task.WhenAll(Enumerable.Range(1, 40).Select(i => Task.Run(() => (i % 2 == 0 ? a : b).SaveAsync(Make(i)))));

        var loaded = await new JsonFileStore<Doc>(StorePath).LoadAsync();
        Assert.InRange(loaded.Version, 1, 40);
        Assert.Equal(Make(loaded.Version).Items, loaded.Items);
        var previous = JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(StorePath + ".bak"))!;
        Assert.InRange(previous.Version, 1, 40);
        Assert.Empty(Leftovers());
        Assert.DoesNotContain(JsonStoreRecovery.Recovered, r => r.Path == StorePath);
    }

    [Fact]
    public async Task A_reader_beside_the_saves_never_sees_partial_json()
    {
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(0));
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                string text;
                try
                {
                    // Opened the way that lets a rename replace the file underneath (as on Linux).
                    await using var fs = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    text = await sr.ReadToEndAsync();
                }
                catch (IOException) { continue; }   // between the rename's two steps on Windows
                var doc = JsonSerializer.Deserialize<Doc>(text)!;   // throws on partial content
                Assert.Equal(Make(doc.Version).Items, doc.Items);
                reads++;
            }
            return reads;
        });

        for (var i = 1; i <= 60; i++)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { await store.SaveAsync(Make(i)); break; }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && attempt < 50) { await Task.Delay(5); }   // the reader holds it
                catch (IOException) when (OperatingSystem.IsWindows() && attempt < 50) { await Task.Delay(5); }
            }
        }
        stop.Cancel();
        Assert.True(await reader > 0);
    }

    [Fact]
    public async Task A_torn_file_comes_back_from_the_previous_generation_visibly_with_the_original_kept()
    {
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(1));
        await store.SaveAsync(Make(2));
        var full = await File.ReadAllTextAsync(StorePath);
        var torn = full[..(full.Length / 2)];
        await File.WriteAllTextAsync(StorePath, torn);

        var loaded = await new JsonFileStore<Doc>(StorePath).LoadAsync();

        Assert.Equal(1, loaded.Version);                                         // the previous generation
        var recovery = Assert.Single(JsonStoreRecovery.Recovered, r => r.Path == StorePath);
        Assert.Equal(torn, await File.ReadAllTextAsync(recovery.PreservedAs));   // the unreadable original, kept
        Assert.StartsWith(StorePath + ".corrupt-", recovery.PreservedAs);
        Assert.Equal(1, JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(StorePath))!.Version);   // written back
        Assert.Equal(1, (await new JsonFileStore<Doc>(StorePath).LoadAsync()).Version);
    }

    [Theory]
    [InlineData("{\"Version\": 3, \"Items\": [")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Corrupt_without_a_previous_generation_stops_instead_of_an_empty_store(string content)
    {
        await File.WriteAllTextAsync(StorePath, content);

        var ex = await Assert.ThrowsAsync<JsonStoreCorruptException>(() => new JsonFileStore<Doc>(StorePath).LoadAsync());

        Assert.Equal(StorePath, ex.Path);
        Assert.Equal(content, await File.ReadAllTextAsync(StorePath));           // left as it was
        Assert.Single(Directory.EnumerateFiles(_dir, "roles.json.corrupt-*"));  // and copied aside
        Assert.DoesNotContain(JsonStoreRecovery.Recovered, r => r.Path == StorePath);
    }

    [Fact]
    public async Task Corrupt_with_an_unreadable_previous_generation_stops_too()
    {
        await File.WriteAllTextAsync(StorePath, "{broken");
        await File.WriteAllTextAsync(StorePath + ".bak", "null");

        await Assert.ThrowsAsync<JsonStoreCorruptException>(() => new JsonFileStore<Doc>(StorePath).LoadAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public async Task A_second_failure_keeps_every_earlier_copy()
    {
        await File.WriteAllTextAsync(StorePath, "{first");
        await Assert.ThrowsAsync<JsonStoreCorruptException>(() => new JsonFileStore<Doc>(StorePath).LoadAsync());
        await Assert.ThrowsAsync<JsonStoreCorruptException>(() => new JsonFileStore<Doc>(StorePath).LoadAsync());

        Assert.Equal(2, Directory.EnumerateFiles(_dir, "roles.json.corrupt-*").Count());
    }

    [Fact]
    public async Task A_missing_file_with_a_previous_generation_is_recovered_not_seeded_anew()
    {
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(1));
        await store.SaveAsync(Make(2));
        File.Delete(StorePath);

        Assert.True(store.Exists());   // not a first run
        Assert.Equal(1, (await store.LoadAsync()).Version);
        Assert.Contains(JsonStoreRecovery.Recovered, r => r.Path == StorePath);
    }

    [Fact]
    public async Task The_previous_generation_is_the_save_before()
    {
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(1));
        Assert.False(File.Exists(StorePath + ".bak"));
        await store.SaveAsync(Make(2));
        await store.SaveAsync(Make(3));

        Assert.Equal(2, JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(StorePath + ".bak"))!.Version);
        Assert.Equal(3, (await store.LoadAsync()).Version);
    }

    [Fact]
    public async Task A_file_that_cannot_be_opened_throws_and_is_never_read_as_missing()
    {
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(1));

        using (new FileStream(StorePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());

        Assert.Equal(1, (await store.LoadAsync()).Version);
        Assert.Empty(Directory.EnumerateFiles(_dir, "roles.json.corrupt-*"));
    }

    [Fact]
    public async Task A_save_that_cannot_be_written_throws_and_leaves_the_file_as_it_was()
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess) return;   // root writes anyway
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(1));
        var before = await File.ReadAllTextAsync(StorePath);

        if (OperatingSystem.IsWindows())
            File.SetAttributes(StorePath, FileAttributes.ReadOnly);   // the rename cannot replace it
        else
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // no temporary file can be made

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(Make(2)));

        Assert.Equal(before, await File.ReadAllTextAsync(StorePath));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            File.SetAttributes(StorePath, FileAttributes.Normal);
        Assert.Empty(Leftovers());
    }

    [Fact]
    public async Task Corrupt_roles_stop_the_start_instead_of_seeding_an_admin()
    {
        await File.WriteAllTextAsync(StorePath, "{\"Roles\": {");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WHISKERS_ADMIN_EMAIL"] = "admin@example.com",
        }).Build();
        var roles = new RoleService(config, NullLogger<RoleService>.Instance, StorePath);

        await Assert.ThrowsAsync<JsonStoreCorruptException>(() => roles.InitializeAsync());
        Assert.Equal("{\"Roles\": {", await File.ReadAllTextAsync(StorePath));
    }

    [Fact]
    public void A_restore_removes_the_previous_generation_of_what_it_replaces_or_removes()
    {
        var p = new DataPathOptions(_dir);
        File.WriteAllText(p.RolesJson, "{}");
        File.WriteAllText(p.RolesJson + ".bak", "{\"old\":1}");
        File.WriteAllText(p.WhitelistJson + ".bak", "{\"old\":1}");   // its store is not in the archive
        File.WriteAllText(p.RolesJson + "." + Guid.NewGuid().ToString("N") + DurableFile.TempSuffix, "{\"cut");   // a write cut off
        Directory.CreateDirectory(p.RestoreStagingDir);
        File.WriteAllText(Path.Combine(p.RestoreStagingDir, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(p.RestoreStagingDir, Path.GetFileName(p.RolesJson)), "{\"new\":1}");
        File.WriteAllText(p.RestorePendingMarker,
            JsonSerializer.Serialize(new RestoreMarker { StagingDir = p.RestoreStagingDir, Provider = "sqlite" }));

        RestoreBootHandler.ApplyPendingRestore(p);

        Assert.Equal("{\"new\":1}", File.ReadAllText(p.RolesJson));
        Assert.Empty(Leftovers());
        Assert.False(File.Exists(p.RolesJson + ".bak"));
        Assert.False(File.Exists(p.WhitelistJson + ".bak"));
    }

    [Fact]
    public async Task A_cleared_agent_conversation_does_not_come_back_from_the_previous_generation()
    {
        var transcripts = new Whiskers.Services.Agent.AgentTranscriptStore(basePath: _dir);
        var message = new Whiskers.Models.Agent.AgentMessage(Whiskers.Models.Agent.AgentRole.User, "hello");
        await transcripts.SaveAsync("a@example.com", [message]);
        await transcripts.SaveAsync("a@example.com", [message, message]);

        await transcripts.ClearAsync("a@example.com");

        Assert.Empty(await transcripts.LoadAsync("a@example.com"));
        Assert.Empty(Directory.EnumerateFiles(_dir));
        Assert.DoesNotContain(JsonStoreRecovery.Recovered, r => r.Path.StartsWith(_dir, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_file_is_not_kept_as_the_previous_generation()
    {
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(1));
        await store.SaveAsync(Make(2));            // .bak = 1
        await File.WriteAllTextAsync(StorePath, "{torn");

        await store.SaveAsync(Make(3));            // without a load in between

        Assert.Equal(1, JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(StorePath + ".bak"))!.Version);
    }

    [Fact]
    public async Task A_directory_in_place_of_the_file_is_refused_not_seeded_as_a_first_run()
    {
        Directory.CreateDirectory(StorePath);   // a bind mount of a host file that does not exist
        var store = new JsonFileStore<Doc>(StorePath);

        Assert.True(store.Exists());
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WHISKERS_ADMIN_EMAIL"] = "admin@example.com",
        }).Build();
        await Assert.ThrowsAsync<IOException>(() => new RoleService(config, NullLogger<RoleService>.Instance, StorePath).InitializeAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(StorePath));
    }

    [Fact]
    public async Task Guardrails_with_only_a_previous_generation_stay_fail_closed()
    {
        var path = Path.Combine(_dir, "guardrails.json");
        var first = new Whiskers.Services.Guardrails.GuardrailStore(filePath: path);
        await first.InitializeAsync();
        await File.WriteAllTextAsync(path + ".bak", await File.ReadAllTextAsync(path));
        File.Delete(path);

        var restarted = new Whiskers.Services.Guardrails.GuardrailStore(filePath: path);
        await restarted.InitializeAsync();

        Assert.NotNull(restarted.LoadError);   // not a first run that recreates the SafeDefault
    }

    [Fact]
    public async Task A_store_recovered_after_its_file_went_missing_keeps_the_owner_only_mode()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(1));
        File.SetUnixFileMode(StorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await store.SaveAsync(Make(2));
        File.Delete(StorePath);

        Assert.Equal(1, (await store.LoadAsync()).Version);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
    }

    [Fact]
    public void A_restore_refuses_a_store_holding_null()
    {
        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "roles.json"), "null");

        Assert.Throws<BackupValidationException>(() => StagedRestoreValidator.Validate(staging, new SelfBackupManifest()));
    }

    [Fact]
    public async Task A_plaintext_secret_moved_out_stays_in_no_previous_generation()
    {
        var store = new JsonFileStore<Doc>(StorePath, holdsPlaintextSecret: d => d.Items.Contains("plaintext-secret"));
        await store.SaveAsync(new Doc { Version = 1 });
        await store.SaveAsync(new Doc { Version = 2, Items = ["plaintext-secret"] });   // .bak = version 1
        await store.SaveAsync(new Doc { Version = 3 });                                  // the secret moved out

        Assert.False(File.Exists(StorePath + ".bak"));
        Assert.DoesNotContain("plaintext-secret", await File.ReadAllTextAsync(StorePath));
        await store.SaveAsync(new Doc { Version = 4 });
        Assert.Equal(3, JsonSerializer.Deserialize<Doc>(await File.ReadAllTextAsync(StorePath + ".bak"))!.Version);
    }

    [Fact]
    public async Task A_save_keeps_the_permissions_of_the_file_it_replaces()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = new JsonFileStore<Doc>(StorePath);
        await store.SaveAsync(Make(1));
        File.SetUnixFileMode(StorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await store.SaveAsync(Make(2));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath + ".bak"));
    }

    [Fact]
    public async Task Durable_writes_leave_no_temporary_file_and_replace_whole()
    {
        var path = Path.Combine(_dir, "cve-findings.json");
        await DurableFile.WriteAllTextAsync(path, "{\"a\":1}");
        await DurableFile.WriteAllTextAsync(path, "{\"a\":2}");

        Assert.Equal("{\"a\":2}", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".bak"));   // only on request
        Assert.Empty(Leftovers());
    }
}

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Mcp;

namespace Whiskers.Tests;

// C6 (extended): the permission system also generates a default MCP API key on first run and used to
// log it verbatim — the same leak as the removed legacy McpApiKeyStore. It must never reach the log.
public sealed class McpPermissionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"mcpperm-{Guid.NewGuid():N}");

    [Fact]
    public async Task First_run_creates_no_key_because_a_hashed_key_could_never_be_shown()
    {
        Directory.CreateDirectory(_dir);
        var logger = new ListLogger<McpPermissionService>();
        var svc = new McpPermissionService(logger, new DataPathOptions(_dir));

        await svc.InitializeAsync();

        Assert.Empty(svc.GetPermissionData().ApiKeys);
        Assert.DoesNotContain(logger.Messages, m => Regex.IsMatch(m, "[0-9a-fA-F]{64}"));
    }

    [Fact]
    public async Task A_new_key_is_returned_once_in_plaintext_but_only_its_hash_reaches_the_disk()
    {
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        var logger = new ListLogger<McpPermissionService>();
        var svc = new McpPermissionService(logger, paths);
        await svc.InitializeAsync();

        var created = await svc.AddApiKeyAsync("ci", "write");

        Assert.Matches("^[0-9a-f]{64}$", created.Key!);
        var file = await File.ReadAllTextAsync(paths.McpPermissionsJson);
        Assert.DoesNotContain(created.Key!, file);
        Assert.Contains(McpPermissionService.HashKey(created.Key!), file);
        Assert.Null(Assert.Single(svc.GetPermissionData().ApiKeys).Key);
        Assert.Equal(created.Key![^4..], svc.GetPermissionData().ApiKeys[0].KeyHint);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(created.Key!));

        // The plaintext still authenticates; its hash (what an attacker reading the file gets) does not.
        Assert.Equal(created.Id, svc.ValidateKey(created.Key!)?.Id);
        Assert.Null(svc.ValidateKey(McpPermissionService.HashKey(created.Key!)));

        // ...and it survives a restart.
        var reloaded = new McpPermissionService(new ListLogger<McpPermissionService>(), paths);
        await reloaded.InitializeAsync();
        Assert.Equal(created.Id, reloaded.ValidateKey(created.Key!)?.Id);
    }

    [Fact]
    public async Task Plaintext_keys_from_an_older_file_are_migrated_to_hashes_and_keep_working()
    {
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        const string legacyKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        await File.WriteAllTextAsync(paths.McpPermissionsJson, $$$"""
            {"ApiKeys":[{"Id":"k1","Name":"old","Key":"{{{legacyKey}}}","PermissionLevel":"read","Enabled":true}],"Tools":{}}
            """);

        var svc = new McpPermissionService(new ListLogger<McpPermissionService>(), paths);
        await svc.InitializeAsync();

        Assert.Equal("k1", svc.ValidateKey(legacyKey)?.Id);
        var file = await File.ReadAllTextAsync(paths.McpPermissionsJson);
        Assert.DoesNotContain(legacyKey, file);
        Assert.Contains(McpPermissionService.HashKey(legacyKey), file);

        // R1-35: no previous generation keeps the plaintext key, also after further saves.
        await svc.AddApiKeyAsync("next", "read");
        foreach (var each in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(legacyKey, await File.ReadAllTextAsync(each));
        Assert.True(File.Exists(paths.McpPermissionsJson + ".bak"));   // kept again once the file holds none
    }

    [Fact]
    public async Task Saving_from_the_UI_can_neither_plant_a_key_nor_change_key_material()
    {
        Directory.CreateDirectory(_dir);
        var svc = new McpPermissionService(new ListLogger<McpPermissionService>(), new DataPathOptions(_dir));
        await svc.InitializeAsync();
        var real = await svc.AddApiKeyAsync("real", "read");

        var data = svc.GetPermissionData();
        data.ApiKeys[0].KeyHash = McpPermissionService.HashKey("attacker");
        data.ApiKeys.Add(new Whiskers.Models.McpApiKeyConfig
            { Id = "planted", Name = "x", KeyHash = McpPermissionService.HashKey("planted"), PermissionLevel = "admin" });
        await svc.SavePermissionDataAsync(data);

        Assert.Null(svc.ValidateKey("attacker"));
        Assert.Null(svc.ValidateKey("planted"));
        Assert.Equal(real.Id, svc.ValidateKey(real.Key!)?.Id);
    }

    [Fact]
    public async Task A_stored_level_looser_than_the_code_is_raised_but_a_stricter_one_is_kept()
    {
        // An existing install persisted hetzner_enable_rescue as "write" before the code raised it to admin.
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        await File.WriteAllTextAsync(paths.McpPermissionsJson, """
            {"ApiKeys":[],"Tools":{
              "hetzner_enable_rescue":{"Enabled":true,"RequiredLevel":"write","Category":"Cloud"},
              "list_servers":{"Enabled":true,"RequiredLevel":"admin","Category":"Server"}
            }}
            """);

        var svc = new McpPermissionService(new ListLogger<McpPermissionService>(), paths);
        await svc.InitializeAsync();

        var tools = svc.GetPermissionData().Tools;
        Assert.Equal("admin", tools["hetzner_enable_rescue"].RequiredLevel);
        Assert.Equal("admin", tools["list_servers"].RequiredLevel); // stricter than code (read): operator's choice stays

        // The UI path cannot lower it again either.
        var data = svc.GetPermissionData();
        data.Tools["hetzner_enable_rescue"].RequiredLevel = "read";
        await svc.SavePermissionDataAsync(data);
        Assert.Equal("admin", svc.GetPermissionData().Tools["hetzner_enable_rescue"].RequiredLevel);
    }

    [Fact]
    public async Task Leftover_files_of_the_removed_flat_key_store_are_deleted_on_start()
    {
        // ServerWatch-era installs still carry api-keys.json (unscoped plaintext keys) and possibly the
        // initial-mcp-key.txt next to it. Nothing accepts those keys any more, so they must not linger.
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        await File.WriteAllTextAsync(paths.LegacyApiKeysJson, """{"Keys":["deadbeef"]}""");
        await File.WriteAllTextAsync(paths.LegacyInitialMcpKeyFile, "deadbeef");

        var svc = new McpPermissionService(new ListLogger<McpPermissionService>(), paths);
        await svc.InitializeAsync();

        Assert.False(File.Exists(paths.LegacyApiKeysJson));
        Assert.False(File.Exists(paths.LegacyInitialMcpKeyFile));
        Assert.Null(svc.ValidateKey("deadbeef"));
    }

    // 1.0 review: the previous generation still holds a key deleted by the last save. A damaged or missing file must
    // stop the start rather than bring that key back.
    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public async Task A_deleted_key_never_authenticates_again_from_the_previous_generation(string damage)
    {
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        var svc = new McpPermissionService(new ListLogger<McpPermissionService>(), paths);
        await svc.InitializeAsync();
        var revoked = await svc.AddApiKeyAsync("compromised", "admin");
        await svc.RemoveApiKeyAsync(revoked.Id);
        var previous = await File.ReadAllTextAsync(paths.McpPermissionsJson + ".bak");
        Assert.Contains(McpPermissionService.HashKey(revoked.Key!), previous);   // the previous generation still has it

        if (damage == "corrupt") await File.WriteAllTextAsync(paths.McpPermissionsJson, "{ torn");
        else File.Delete(paths.McpPermissionsJson);
        var restarted = new McpPermissionService(new ListLogger<McpPermissionService>(), paths);

        var refused = await Assert.ThrowsAsync<Whiskers.Services.Persistence.JsonStoreCorruptException>(() => restarted.InitializeAsync());
        Assert.Contains("not restored automatically", refused.Message);
        Assert.Null(restarted.ValidateKey(revoked.Key!));
        Assert.Equal(previous, await File.ReadAllTextAsync(paths.McpPermissionsJson + ".bak"));   // untouched
        Assert.Equal(damage == "corrupt", File.Exists(paths.McpPermissionsJson));                  // nothing written back
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>INV-45 review round 14 (NIT-2): a required level that is not read / write / admin grants nothing to
    /// any key — it used to rank 0, which every key met.</summary>
    [Fact]
    public void An_unknown_required_level_grants_nothing_to_any_key()
    {
        string[] keys = [McpPermissionLevels.Read, McpPermissionLevels.Write, McpPermissionLevels.Admin, "none", "bogus", ""];
        foreach (var unknown in new[] { "none", "None", "NONE", "bogus", "", "Write", " admin" })
            foreach (var key in keys)
                Assert.False(McpPermissionLevels.Grants(key, unknown), $"key '{key}', required '{unknown}'");
        Assert.True(McpPermissionLevels.Grants(McpPermissionLevels.Admin, McpPermissionLevels.Write));
        Assert.False(McpPermissionLevels.Grants(McpPermissionLevels.Read, McpPermissionLevels.Write));
    }

    /// <summary>INV-45 review round 14 (NIT-2): "none" — in any case — or any other unknown level is never stored: a
    /// key cannot be created with it, and a save holding it for a key or a tool is refused and changes nothing.</summary>
    [Fact]
    public async Task None_or_any_unknown_level_is_refused_on_add_and_on_save()
    {
        Directory.CreateDirectory(_dir);
        var svc = new McpPermissionService(new ListLogger<McpPermissionService>(), new DataPathOptions(_dir));
        await svc.InitializeAsync();
        foreach (var level in new[] { "none", "None", " none ", "bogus", "" })
            await Assert.ThrowsAsync<ArgumentException>(() => svc.AddApiKeyAsync("x", level));
        Assert.Empty(svc.GetPermissionData().ApiKeys);

        await svc.AddApiKeyAsync("ops", McpPermissionLevels.Write);
        var keyed = svc.GetPermissionData();
        keyed.ApiKeys[0].PermissionLevel = "None";
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SavePermissionDataAsync(keyed));
        var tooled = svc.GetPermissionData();
        tooled.Tools["list_servers"].RequiredLevel = "none";
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SavePermissionDataAsync(tooled));

        var now = svc.GetPermissionData();
        Assert.Equal(McpPermissionLevels.Write, now.ApiKeys[0].PermissionLevel);
        Assert.Equal(McpPermissionLevels.Read, now.Tools["list_servers"].RequiredLevel);
    }

    /// <summary>INV-45 review round 14 (NIT-2): a file that already holds such a level is made fail-closed on load
    /// and written back — the key is switched off (level read, so an admin re-enables it deliberately), a tool is
    /// raised to admin, also one with a lower code floor (the unknown value is settled before the floor).</summary>
    [Fact]
    public async Task A_stored_unknown_level_is_made_fail_closed_on_load()
    {
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        await File.WriteAllTextAsync(paths.McpPermissionsJson, """
            {"ApiKeys":[{"Id":"k1","Name":"legacy","KeyHash":"00","KeyHint":"","PermissionLevel":"None","Enabled":true}],
             "Tools":{"retired_tool":{"Enabled":true,"RequiredLevel":"none","Category":"Other"},
                      "start_container":{"Enabled":true,"RequiredLevel":"bogus","Category":"Container"}}}
            """);

        var svc = new McpPermissionService(new ListLogger<McpPermissionService>(), paths);
        await svc.InitializeAsync();

        var data = svc.GetPermissionData();
        Assert.False(data.ApiKeys[0].Enabled);
        Assert.Equal(McpPermissionLevels.Read, data.ApiKeys[0].PermissionLevel);
        Assert.Equal(McpPermissionLevels.Admin, data.Tools["retired_tool"].RequiredLevel);
        Assert.Equal(McpPermissionLevels.Admin, data.Tools["start_container"].RequiredLevel);   // not its write floor
        var file = await File.ReadAllTextAsync(paths.McpPermissionsJson);
        Assert.DoesNotContain("\"None\"", file);
        Assert.DoesNotContain("\"none\"", file);
    }

    /// <summary>INV-45 review rounds 15/16 (MINOR-1): another spelling of a known level is not a reason to switch a key
    /// off — on load it is rewritten to its exact form, on a key and on a tool. That is a behaviour change, pinned here:
    /// <list type="bullet">
    /// <item>A key spelled differently without a tool list ranked 0 at the <c>/mcp</c> check, so every MCP tool was
    /// refused; after the rewrite it gets the tools of its level (and no more).</item>
    /// <item>The Claude Code runtime's check (key level ≤ caller level) is stricter for such a key: <c>"Admin"</c>
    /// ranked 0, which any caller met; <c>admin</c> only an admin caller meets.</item>
    /// <item>A tool spelled differently now requires exactly its written level (<c>list_servers</c> at
    /// <c>"Admin"</c> used to fall to its read floor, it now requires admin).</item>
    /// </list></summary>
    [Fact]
    public async Task Another_spelling_of_a_known_level_is_rewritten_on_load_not_switched_off()
    {
        const string plaintext = "inv45-spelled-key-plaintext";
        Directory.CreateDirectory(_dir);
        var paths = new DataPathOptions(_dir);
        await File.WriteAllTextAsync(paths.McpPermissionsJson, $$$"""
            {"ApiKeys":[
               {"Id":"k1","Name":"upper","KeyHash":"00","KeyHint":"","PermissionLevel":"Admin","Enabled":true},
               {"Id":"k2","Name":"spaced","KeyHash":"01","KeyHint":"","PermissionLevel":" write","Enabled":true},
               {"Id":"k3","Name":"real","KeyHash":"{{{McpPermissionService.HashKey(plaintext)}}}","KeyHint":"","PermissionLevel":"Write","Enabled":true}],
             "Tools":{
               "list_servers":{"Enabled":true,"RequiredLevel":"Admin","Category":"Server"},
               "retired_tool":{"Enabled":true,"RequiredLevel":" write","Category":"Other"} } }
            """);

        var svc = new McpPermissionService(new ListLogger<McpPermissionService>(), paths);
        // Before: the level as stored ("Write") ranks 0, which is what the /mcp check of the old code compared, so no
        // tool was granted to it. Only the Grants line pins that old result; the service has loaded nothing yet.
        Assert.False(McpPermissionLevels.Grants("Write", McpPermissionLevels.Read));
        Assert.True(McpPermissionLevels.HasAccess(McpPermissionLevels.Read, "Admin"));   // old runtime check: any caller

        await svc.InitializeAsync();

        var data = svc.GetPermissionData();
        Assert.All(data.ApiKeys, k => Assert.True(k.Enabled, k.Name));
        Assert.Equal(McpPermissionLevels.Admin, data.ApiKeys.Single(k => k.Id == "k1").PermissionLevel);
        Assert.Equal(McpPermissionLevels.Write, data.ApiKeys.Single(k => k.Id == "k2").PermissionLevel);
        Assert.Equal(McpPermissionLevels.Write, data.ApiKeys.Single(k => k.Id == "k3").PermissionLevel);
        Assert.Equal(McpPermissionLevels.Admin, data.Tools["list_servers"].RequiredLevel);   // not its read floor
        Assert.Equal(McpPermissionLevels.Write, data.Tools["retired_tool"].RequiredLevel);

        // After: the real key gets the tools up to its level, and no more.
        Assert.True(svc.IsToolAllowed(plaintext, "list_containers"));    // read
        Assert.True(svc.IsToolAllowed(plaintext, "start_container"));    // write
        Assert.True(svc.IsToolAllowed(plaintext, "retired_tool"));       // " write" → write
        Assert.False(svc.IsToolAllowed(plaintext, "list_servers"));      // "Admin" → admin, above the key
        Assert.Contains("start_container", svc.GetAllowedTools(plaintext));
        Assert.DoesNotContain("list_servers", svc.GetAllowedTools(plaintext));
        // The runtime check is now stricter for the former "Admin" key: a read caller no longer meets it.
        Assert.False(McpPermissionLevels.HasAccess(McpPermissionLevels.Read, data.ApiKeys.Single(k => k.Id == "k1").PermissionLevel));
        var file = await File.ReadAllTextAsync(paths.McpPermissionsJson);
        Assert.DoesNotMatch("\"(PermissionLevel|RequiredLevel)\":\\s*\"(Admin| write)\"", file);   // written back exactly
        Assert.Matches("\"PermissionLevel\":\\s*\"admin\"", file);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly List<string> Messages = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

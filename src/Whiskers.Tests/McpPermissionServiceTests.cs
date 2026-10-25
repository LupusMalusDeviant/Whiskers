using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Whiskers.Configuration;
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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
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

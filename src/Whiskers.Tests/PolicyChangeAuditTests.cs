using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Guardrails;

using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-02 / INV-28: the live policy is a snapshot no caller can alias. What a page reads is a copy; what it saves is
/// copied in; a save that cannot be written leaves the previous policy enforced. (The policy-change audit trail
/// itself comes with a later package.)
/// </summary>
public sealed class PolicyChangeAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"guardrails-{Guid.NewGuid():N}");
    private string FilePath => Path.Combine(_dir, "guardrails.json");

    public PolicyChangeAuditTests() => Directory.CreateDirectory(_dir);

    private static AgentPrincipal Admin => new(AgentPrincipalKind.WebUser, "admin", McpPermissionLevels.Admin, null,
        UserEmail: "admin@example.com");

    [Fact]
    public async Task Unsaved_edits_do_not_change_live_policy()
    {
        var store = new GuardrailStore(filePath: FilePath);
        await store.InitializeAsync();

        // Edits on what the store handed out, never saved.
        var config = store.Config;
        config.Presets[0].Policy.ReadOnlyMode = true;
        config.Presets[0].Policy.ProtectedResources.Clear();
        var current = store.Current;
        current.ForbiddenArgPatterns.Clear();
        current.MaxAutonomousLevel = McpPermissionLevels.Admin;

        var live = store.Current;
        Assert.False(live.ReadOnlyMode);
        Assert.Contains("whiskers*", live.ProtectedResources);
        Assert.NotEmpty(live.ForbiddenArgPatterns);
        Assert.Equal("read", live.MaxAutonomousLevel);
        Assert.Contains("whiskers*", store.Config.Presets[0].Policy.ProtectedResources);
    }

    [Fact]
    public async Task Editing_a_saved_object_afterwards_does_not_change_live_policy()
    {
        var store = new GuardrailStore(filePath: FilePath);
        await store.InitializeAsync();
        var config = store.Config;
        config.Presets[0].Policy.ProtectedResources.Add("payments-db");
        await store.SaveConfigAsync(config, Admin);

        config.Presets[0].Policy.ProtectedResources.Clear();   // the page keeps editing its own object

        Assert.Contains("payments-db", store.Current.ProtectedResources);
        Assert.Contains("whiskers*", store.Current.ProtectedResources);
    }

    [Fact]
    public async Task A_save_that_cannot_be_written_leaves_the_previous_policy_active()
    {
        var store = new GuardrailStore(filePath: FilePath);
        await store.InitializeAsync();
        // The file cannot be written (as on a full or read-only disk).
        DurableFile.FailingWrites[Path.GetFullPath(FilePath)] = 0;
        var config = store.Config;
        config.Presets[0].Policy.ReadOnlyMode = true;
        config.Presets[0].Policy.ProtectedResources.Clear();

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveConfigAsync(config, Admin));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(config.Presets[0].Policy, Admin));

        Assert.False(store.Current.ReadOnlyMode);
        Assert.Contains("whiskers*", store.Current.ProtectedResources);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

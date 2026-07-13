using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Tests;

/// <summary>WP-06: the three starter presets the "Secure AI Operations" onboarding offers must map to
/// sensible, real GuardrailPolicy values and reference only real tool names — never weaker than the
/// safe default, and progressively stricter.</summary>
public class AiOperationsPresetsTests
{
    [Fact]
    public void Observe_only_is_read_only()
    {
        var p = AiOperationsPresets.ObserveOnlyPolicy();
        Assert.True(p.ReadOnlyMode);   // kill switch: everything above read is denied
    }

    [Fact]
    public void Safe_operations_confirms_writes_and_denies_high_risk_tools()
    {
        var p = AiOperationsPresets.SafeOperationsPolicy();
        Assert.False(p.ReadOnlyMode);
        Assert.Equal("read", p.MaxAutonomousLevel);          // reads auto, writes need confirmation
        Assert.True(p.RequireConfirmationForWrites);
        Assert.Contains("execute_command", p.ToolDenyList);  // admin tool always denied
        Assert.Contains("hetzner_delete_snapshot", p.ToolDenyList);
    }

    [Fact]
    public void Approval_required_is_stricter_than_safe()
    {
        var safe = AiOperationsPresets.SafeOperationsPolicy();
        var approval = AiOperationsPresets.ApprovalRequiredPolicy();
        Assert.True(approval.MaxActionsPerSession < safe.MaxActionsPerSession);
        Assert.Equal(safe.ToolDenyList.Count, approval.ToolDenyList.Count);   // at least as locked down
    }

    [Fact]
    public void High_risk_tools_are_all_real_catalog_tools()
    {
        foreach (var tool in AiOperationsPresets.HighRiskTools())
            Assert.True(McpPermissionLevels.DefaultToolLevels.ContainsKey(tool), $"'{tool}' is not a real tool");
        Assert.NotEmpty(AiOperationsPresets.HighRiskTools());
    }

    [Fact]
    public void Starters_expose_the_three_named_presets()
    {
        var names = AiOperationsPresets.Starters().Select(s => s.Name).ToList();
        Assert.Equal(new[]
        {
            AiOperationsPresets.ObserveOnly,
            AiOperationsPresets.SafeOperations,
            AiOperationsPresets.ApprovalRequired,
        }, names);
    }
}

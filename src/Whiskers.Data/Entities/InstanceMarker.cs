namespace Whiskers.Models;

/// <summary>
/// Contract 0010: a fact this instance records once and never forgets — "this has existed". A row is only ever inserted:
/// Whiskers never updates or deletes one (no UI, no MCP tool, no capability, no retention job). The guardrail store's row
/// (<c>guardrail-policy-existed</c>) tells a deleted policy file apart from a first start (INV-25).
/// </summary>
public class InstanceMarkerEntity
{
    /// <summary>What has existed, e.g. <c>guardrail-policy-existed</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>When the row was first written (UTC).</summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>Why it was written (for the guardrail marker: <c>created</c>, <c>loaded</c>, <c>backfill</c>,
    /// <c>snapshot-evidence</c> or <c>reset-cli</c>).</summary>
    public string? Detail { get; set; }
}

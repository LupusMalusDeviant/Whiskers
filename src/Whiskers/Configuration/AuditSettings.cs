namespace Whiskers.Configuration;

/// <summary>Audit trail retention (R1-23, INV-50). Set via <c>Audit__RetentionDays</c>.</summary>
public class AuditSettings
{
    public const string SectionName = "Audit";

    /// <summary>The shortest retention accepted: a smaller value is raised to it, so a typo cannot wipe the trail.</summary>
    public const int MinRetentionDays = 30;

    /// <summary>How long audit events and the MCP call history are kept.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>The retention in effect: <see cref="RetentionDays"/>, at least <see cref="MinRetentionDays"/>.</summary>
    public int EffectiveRetentionDays => Math.Max(MinRetentionDays, RetentionDays);
}

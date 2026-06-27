namespace ServerWatch.Models.Cve;

/// <summary>Persists WHEN a specific vulnerability instance (its <see cref="CveFinding.IdentityKey"/>)
/// was first detected, so the "open for N days" age survives restarts and scan cycles.</summary>
public class CveFirstSeenEntity
{
    public long Id { get; set; }

    /// <summary>The finding's stable IdentityKey (server|source|container|package|cve).</summary>
    public string IdentityKey { get; set; } = string.Empty;

    public string CveId { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; }
}

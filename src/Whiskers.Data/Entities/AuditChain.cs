namespace Whiskers.Models;

/// <summary>One link of the audit hash chain (R1-23, ADR-0006): one written version of an audit row. Appended in the
/// same transaction as the row write, never changed; <see cref="Hash"/> =
/// SHA256(<see cref="PrevHash"/> ‖ <see cref="Seq"/> ‖ <see cref="AuditId"/> ‖ <see cref="RowHash"/>).</summary>
public class AuditChainEntry
{
    /// <summary>The position in the chain, assigned by the appender (not by the database): it is part of the hash.</summary>
    public long Seq { get; set; }
    public long AuditId { get; set; }
    /// <summary>SHA-256 of the audit row's canonical form at this version.</summary>
    public string RowHash { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTime At { get; set; }
    /// <summary>HMAC-SHA-256 over <c>link|{Hash}</c> under the key derived from VAULT_KEY; null when the vault is not
    /// configured. Once the trail is signed every new link carries one.</summary>
    public string? Mac { get; set; }
}

/// <summary>A signed statement "the chain had reached <see cref="Seq"/> with <see cref="Hash"/>" (R1-23). A retention
/// checkpoint also names the new anchor of the chain and the audit ids pruned up to it.</summary>
public class AuditCheckpointEntity
{
    public long Id { get; set; }
    /// <summary><c>head</c>, <c>genesis</c> or <c>retention</c>.</summary>
    public string Kind { get; set; } = "";
    public long Seq { get; set; }
    public string Hash { get; set; } = "";
    /// <summary>Retention: every audit row with an id up to this one was pruned; 0 otherwise.</summary>
    public long PrunedThroughId { get; set; }
    public DateTime At { get; set; }
    /// <summary>HMAC-SHA-256 under the key derived from VAULT_KEY; null when the vault is not configured.</summary>
    public string? Mac { get; set; }
}

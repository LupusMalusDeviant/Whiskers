namespace Whiskers.Models.Dns;

/// <summary>One record of a DNS zone, provider-neutral. <see cref="Name"/> is relative to the zone
/// (<c>"@"</c> = apex, <c>"holler.app"</c> = holler.app.&lt;zone&gt;), never an FQDN.</summary>
public sealed record DnsRecord(
    string? Id,
    string Name,
    string Type,
    string Value,
    int Ttl)
{
    public override string ToString() => $"{Name} {Type} {Value} (TTL {Ttl})";
}

/// <summary>What <c>set_dns_record</c> did.</summary>
public enum DnsSetAction
{
    Created,
    Updated,
    Unchanged,
}

/// <summary>Before/after of an idempotent set, for a human-readable answer.</summary>
public sealed record DnsSetResult(DnsSetAction Action, DnsRecord? Before, DnsRecord After)
{
    /// <summary>Further records of the same name and type the set deleted (an A/AAAA/CNAME set collapses several onto
    /// one), so the answer and the audit name everything that went away (Fable review H3). Never a TXT record.</summary>
    public IReadOnlyList<DnsRecord> Removed { get; init; } = [];
}

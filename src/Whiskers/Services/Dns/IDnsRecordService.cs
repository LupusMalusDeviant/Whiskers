using Whiskers.Services.Mutations;
using Whiskers.Models.Dns;

namespace Whiskers.Services.Dns;

/// <summary>Provider-neutral record management on top of <see cref="IDnsProviderClient"/>: resolves the
/// configured provider + token, fences zones to <c>DnsSettings.AllowedZones</c>, normalises names and
/// validates values per type, and makes <see cref="SetAsync"/> idempotent (same name+type → update, same
/// value → unchanged).</summary>
public interface IDnsRecordService
{
    /// <summary>True when a provider token is configured; the tools answer a hint instead of failing otherwise.</summary>
    bool IsConfigured { get; }

    Task<List<DnsRecord>> ListAsync(string zone, CancellationToken ct = default);

    [MutatingOperation("dns.record_set")]
    Task<DnsSetResult> SetAsync(string zone, string name, string type, string value, int? ttl, CancellationToken ct = default);

    /// <summary>Deletes every record with this name and type; returns what was removed (empty = nothing there).</summary>
    [MutatingOperation("dns.record_delete")]
    Task<List<DnsRecord>> DeleteAsync(string zone, string name, string type, CancellationToken ct = default);
}

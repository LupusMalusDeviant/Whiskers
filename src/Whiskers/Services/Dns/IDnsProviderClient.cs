using Whiskers.Models.Dns;

namespace Whiskers.Services.Dns;

/// <summary>The HTTP layer of one DNS provider. Every call takes the token explicitly (like the cloud
/// clients) so the client itself holds no secret and is trivially testable against a fake handler.
/// Names in and out are zone-relative (<c>"@"</c> for the apex); provider-specific spellings of the apex
/// stay inside the implementation.</summary>
public interface IDnsProviderClient
{
    /// <summary>Provider id this client serves, e.g. <c>"infomaniak"</c>.</summary>
    string ProviderId { get; }

    Task<List<DnsRecord>> ListRecordsAsync(string token, string zone, CancellationToken ct = default);

    /// <summary>Creates the record; returns it with the provider-assigned id.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("dns.record_set")]
    Task<DnsRecord> CreateRecordAsync(string token, string zone, DnsRecord record, CancellationToken ct = default);

    /// <summary>Replaces value/TTL of the record with the given id; name and type stay as they are.</summary>
    [Whiskers.Services.Mutations.MutatingOperation("dns.record_set")]
    Task<DnsRecord> UpdateRecordAsync(string token, string zone, string recordId, DnsRecord record, CancellationToken ct = default);

    [Whiskers.Services.Mutations.MutatingOperation("dns.record_delete")]
    Task DeleteRecordAsync(string token, string zone, string recordId, CancellationToken ct = default);
}

/// <summary>A provider answered with an error (HTTP status and/or an error envelope). The message carries the
/// status and the provider's own text, so an MCP caller sees why — never the token.</summary>
public sealed class DnsProviderException : Exception
{
    public int? StatusCode { get; }

    public DnsProviderException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;
}

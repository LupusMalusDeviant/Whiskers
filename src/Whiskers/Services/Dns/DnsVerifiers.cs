using Whiskers.Services.Mutations;

namespace Whiskers.Services.Dns;

/// <summary>
/// Read-backs of the DNS capabilities (R1-37, INV-43): after the call returned, the provider's zone is listed again and
/// the record compared in the same normal form the service writes it in. A provider error on that read fails the
/// outcome with the state unknown.
/// </summary>
public sealed class DnsRecordVerifier(IServiceProvider services, bool set) : ServiceVerifier(services)
{
    public override string CapabilityId => set ? "dns.record_set" : "dns.record_delete";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var dns = Module<IDnsRecordService>("DNS");
        string zone, name, type;
        try
        {
            zone = DnsRecordService.NormalizeZone(VerifierSupport.Arg(context, "zone") ?? context.Request.Target.Id);
            name = DnsRecordService.NormalizeName(VerifierSupport.Arg(context, "name"), zone);
            type = DnsRecordService.NormalizeType(VerifierSupport.Arg(context, "type"));
        }
        catch (ArgumentException ex)
        {
            throw new MutationNotVerifiableException($"the call named no valid record to read back ({ex.Message}).");
        }

        List<Whiskers.Models.Dns.DnsRecord> records;
        try
        {
            records = await dns.ListAsync(zone, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MutationVerificationFailedException($"the zone '{zone}' could not be read ({ex.Message}).", stateUnknown: true);
        }
        var matching = records.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!set)
        {
            if (matching.Count > 0)
                throw new MutationVerificationFailedException($"{name} {type} is still listed in '{zone}'.");
            return;
        }

        string value;
        try
        {
            value = DnsRecordService.NormalizeValue(type, VerifierSupport.Arg(context, "value"));
        }
        catch (ArgumentException ex)
        {
            throw new MutationNotVerifiableException($"the call named no valid value to compare ({ex.Message}).");
        }
        // The same provider-tolerant equality the service uses to answer "unchanged" (quoted TXT, IP spellings).
        if (!matching.Any(r => DnsRecordService.ValuesEqual(type, r.Value, value)))
            throw new MutationVerificationFailedException(matching.Count == 0
                ? $"{name} {type} is not listed in '{zone}'."
                : $"{name} {type} in '{zone}' holds '{matching[0].Value}', not '{value}'.");
    }
}

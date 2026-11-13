using System.ComponentModel;
using System.Text;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using Whiskers.Models;
using Whiskers.Models.Dns;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Dns;
using Whiskers.Services.Mcp;
using Whiskers.Services.Mutations;

namespace Whiskers.Mcp.Tools;

/// <summary>
/// DNS zone records at the configured provider (Infomaniak today). One global account token
/// (<i>Settings → DNS</i>), optionally fenced to a list of zones. <c>set_dns_record</c> is idempotent:
/// same name+type is updated, same value answers "unchanged". Only A/AAAA/CNAME/TXT — the zone's skeleton
/// (NS/SOA) and structured types (MX/SRV/CAA) are out of reach on purpose.
/// </summary>
[McpServerToolType]
public class DnsTools
{
    [McpToolLevel(McpPermissionLevels.Read)]
    [McpServerTool, Description("List the DNS records of a zone (e.g. 'lupusmalus.dev') at the configured DNS provider. Names are relative to the zone ('@' = the zone itself). Optionally filter by name and/or type. Read-only.")]
    public static async Task<string> ListDnsRecords(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService,
        IDnsRecordService dns,
        [Description("Zone / domain, e.g. lupusmalus.dev")] string zone,
        [Description("Optional: only records with this name (relative, e.g. 'holler.app' or '@')")] string? name = null,
        [Description("Optional: only records of this type (A, AAAA, CNAME, TXT, …)")] string? type = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "list_dns_records");
        if (denied != null) return denied;
        if (!dns.IsConfigured) return NotConfigured;

        try
        {
            var z = DnsRecordService.NormalizeZone(zone);
            var records = await dns.ListAsync(z);
            if (!string.IsNullOrWhiteSpace(name))
            {
                var n = DnsRecordService.NormalizeName(name, z);
                records = records.Where(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(type))
            {
                var t = type.Trim().ToUpperInvariant();
                records = records.Where(r => r.Type == t).ToList();
            }
            if (records.Count == 0) return $"Keine passenden Einträge in Zone {z}.";

            var sb = new StringBuilder();
            sb.AppendLine($"Zone {z} — {records.Count} Einträge:");
            foreach (var r in records)
                sb.AppendLine($"- {r.Name,-24} {r.Type,-6} {r.Value}  (TTL {r.Ttl})");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DnsProviderException)
        {
            return "Fehler: " + ex.Message;
        }
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Create or update one DNS record at the configured provider — idempotent: A, AAAA and CNAME: an existing record with the same name and type is updated (never duplicated), further records of that name and type are removed and listed in the answer. TXT: the value is added next to the other TXT records of the name (SPF, verifications), which stay untouched; an existing record with that value only gets its TTL adjusted. An identical record answers 'unchanged'. Types: A (IPv4), AAAA (IPv6), CNAME (hostname), TXT. Name is relative to the zone ('holler.app' → holler.app.<zone>, '@' = zone apex). Returns before/after.")]
    public static async Task<string> SetDnsRecord(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService,
        IDnsRecordService dns, IToolAuditLog auditLog,
        [Description("Zone / domain, e.g. lupusmalus.dev")] string zone,
        [Description("Record name relative to the zone, e.g. 'holler.app' or '@' for the zone itself")] string name,
        [Description("Record type: A, AAAA, CNAME or TXT")] string type,
        [Description("Record value: IPv4 for A, IPv6 for AAAA, hostname for CNAME, text for TXT")] string value,
        [Description("TTL in seconds (60–86400). Default 300.")] int? ttl = null)
    {
        // R1-37: a refusal, a missing configuration and a provider error each fail the operation (the text still goes
        // back to the caller); only a set or an unchanged record is an executed call.
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "set_dns_record");
        if (denied != null) return MutationScope.Fail(denied);
        if (!dns.IsConfigured) return MutationScope.Fail(NotConfigured);

        try
        {
            var z = DnsRecordService.NormalizeZone(zone);
            var result = await dns.SetAsync(z, name, type, value, ttl);

            if (result.Action != DnsSetAction.Unchanged)
            {
                var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
                await auditLog.LogAsync(actor, actorType, $"dns.record_{result.Action.ToString().ToLowerInvariant()}", "dns-zone",
                    z, $"{result.After.Name} {result.After.Type}", Describe(result));
            }

            return result.Action switch
            {
                DnsSetAction.Unchanged => $"Unverändert: {Fqdn(result.After.Name, z)} {result.After.Type} {result.After.Value} (TTL {result.After.Ttl}) existiert bereits so.",
                DnsSetAction.Created => $"Angelegt: {Fqdn(result.After.Name, z)} {result.After.Type} {result.After.Value} (TTL {result.After.Ttl})\n" +
                    (result.After.Type == "TXT"
                        ? "Vorher: kein TXT-Eintrag mit diesem Wert; andere TXT-Einträge unter dem Namen bleiben unverändert."
                        : $"Vorher: kein {result.After.Type}-Eintrag unter diesem Namen."),
                _ => $"Aktualisiert: {Fqdn(result.After.Name, z)} {result.After.Type}\nVorher: {result.Before!.Value} (TTL {result.Before.Ttl})\nNachher: {result.After.Value} (TTL {result.After.Ttl})"
                     + RemovedLines(result, z),
            };
        }
        catch (DnsPartialSetException partial)
        {
            // The zone changed even though the set failed: the audit and the answer name what was changed, what went
            // and what is still there (Fable review).
            var z = DnsRecordService.NormalizeZone(zone);
            var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
            await auditLog.LogAsync(actor, actorType, "dns.record_updated", "dns-zone", z,
                $"{partial.After.Name} {partial.After.Type}",
                $"partial: updated {partial.Before} -> {partial.After}; removed {Join(partial.Removed)}; still there or unclear {Join(partial.Remaining)}; error {partial.InnerException?.Message}", success: false);
            return MutationScope.Fail(
                $"Fehler: nur teilweise ausgeführt ({partial.InnerException?.Message}).\n" +
                $"Aktualisiert: {Fqdn(partial.After.Name, z)} {partial.After.Type} {partial.Before.Value} → {partial.After.Value} (TTL {partial.After.Ttl})\n" +
                (partial.Removed.Count > 0 ? "Entfernt:\n" + Lines(partial.Removed, z) + "\n" : "") +
                "Noch vorhanden oder unklar (erneut ausführen):\n" + Lines(partial.Remaining, z));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DnsProviderException)
        {
            return MutationScope.Fail("Fehler: " + ex.Message);
        }
    }

    [McpToolLevel(McpPermissionLevels.Write)]
    [McpServerTool, Description("Delete the DNS record(s) with the given name and type in a zone at the configured provider — all of them, or with 'value' only the record(s) holding that value (e.g. one TXT value among SPF and verification records). Only A, AAAA, CNAME and TXT can be deleted. Answers what was removed; nothing there = no-op.")]
    public static async Task<string> DeleteDnsRecord(
        IHttpContextAccessor httpContextAccessor, IMcpPermissionService permissionService,
        IDnsRecordService dns, IToolAuditLog auditLog,
        [Description("Zone / domain, e.g. lupusmalus.dev")] string zone,
        [Description("Record name relative to the zone, e.g. 'holler.app' or '@'")] string name,
        [Description("Record type: A, AAAA, CNAME or TXT")] string type,
        [Description("Optional: delete only the record(s) with this value; without it every record of name and type goes")] string? value = null)
    {
        var denied = McpPermissionCheck.CheckAccess(httpContextAccessor, permissionService, "delete_dns_record");
        if (denied != null) return MutationScope.Fail(denied);
        if (!dns.IsConfigured) return MutationScope.Fail(NotConfigured);

        try
        {
            var z = DnsRecordService.NormalizeZone(zone);
            var removed = await dns.DeleteAsync(z, name, type, value);
            if (removed.Count == 0)
                return string.IsNullOrEmpty(value)
                    ? $"Nichts zu löschen: kein {type.Trim().ToUpperInvariant()}-Eintrag '{name}' in Zone {z}."
                    : $"Nichts zu löschen: kein {type.Trim().ToUpperInvariant()}-Eintrag '{name}' mit diesem Wert in Zone {z}.";

            var (actor, actorType) = IAuditLogService.GetActorFromHttpContext(httpContextAccessor.HttpContext, permissionService);
            await auditLog.LogAsync(actor, actorType, "dns.record_deleted", "dns-zone",
                z, $"{removed[0].Name} {removed[0].Type}", string.Join("; ", removed.Select(r => r.ToString())));

            return $"Gelöscht in Zone {z}:\n" + string.Join('\n', removed.Select(r => $"- {Fqdn(r.Name, z)} {r.Type} {r.Value} (TTL {r.Ttl})"));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DnsProviderException)
        {
            return MutationScope.Fail("Fehler: " + ex.Message);
        }
    }

    private const string NotConfigured =
        "Kein DNS-Provider konfiguriert. Der Betreiber hinterlegt das Infomaniak-Token unter Einstellungen → DNS.";

    private static string Fqdn(string name, string zone) => name == "@" ? zone : $"{name}.{zone}";

    private static string Join(IEnumerable<DnsRecord> records) => string.Join("; ", records);

    private static string Lines(IEnumerable<DnsRecord> records, string zone) =>
        string.Join('\n', records.Select(x => $"- {Fqdn(x.Name, zone)} {x.Type} {x.Value} (TTL {x.Ttl})"));

    private static string RemovedLines(DnsSetResult r, string zone) => r.Removed.Count == 0 ? "" :
        "\nEntfernt (weitere Einträge gleichen Namens und Typs):\n" + Lines(r.Removed, zone);

    private static string Describe(DnsSetResult r) => r.Action switch
    {
        DnsSetAction.Created => $"created {r.After}",
        DnsSetAction.Updated => $"updated {r.Before} -> {r.After}" + (r.Removed.Count > 0 ? $"; removed {string.Join("; ", r.Removed)}" : ""),
        _ => $"unchanged {r.After}",
    };
}

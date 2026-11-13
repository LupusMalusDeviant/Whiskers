using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Whiskers.Configuration;
using Whiskers.Models.Dns;

namespace Whiskers.Services.Dns;

/// <inheritdoc cref="IDnsRecordService"/>
public sealed class DnsRecordService : IDnsRecordService
{
    /// <summary>Record types the tools accept. Deliberately narrow: MX/SRV/CAA carry structured extras the
    /// provider models differently, and NS/SOA are the zone's skeleton — an agent has no business there.</summary>
    public static readonly IReadOnlySet<string> SupportedTypes =
        new HashSet<string>(StringComparer.Ordinal) { "A", "AAAA", "CNAME", "TXT" };

    public const int MinTtl = 60;
    public const int MaxTtl = 86400;

    private static readonly Regex LabelPattern = new(
        @"^(?!-)[A-Za-z0-9_-]{1,63}(?<!-)$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly IEnumerable<IDnsProviderClient> _clients;
    private readonly IOptionsMonitor<DnsSettings> _settings;
    private readonly ILogger<DnsRecordService> _logger;

    private readonly Whiskers.Services.Vault.IVaultService? _vault;

    public DnsRecordService(IEnumerable<IDnsProviderClient> clients, IOptionsMonitor<DnsSettings> settings, ILogger<DnsRecordService> logger,
        Whiskers.Services.Vault.IVaultService? vault = null)
    {
        _vault = vault;
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConfigured => _settings.CurrentValue.IsConfigured;

    public async Task<List<DnsRecord>> ListAsync(string zone, CancellationToken ct = default)
    {
        var (client, token, z) = Resolve(zone);
        var records = await client.ListRecordsAsync(token, z, ct);
        return records
            .OrderBy(r => r.Name == "@" ? "" : r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Type, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<DnsSetResult> SetAsync(string zone, string name, string type, string value, int? ttl, CancellationToken ct = default)
    {
        var (client, token, z) = Resolve(zone);
        var t = NormalizeType(type);
        var n = NormalizeName(name, z);
        var v = NormalizeValue(t, value);
        var effectiveTtl = ttl ?? _settings.CurrentValue.DefaultTtl;
        if (effectiveTtl < MinTtl || effectiveTtl > MaxTtl)
            throw new ArgumentException($"TTL muss zwischen {MinTtl} und {MaxTtl} Sekunden liegen (angegeben: {effectiveTtl}).");

        var wanted = new DnsRecord(null, n, t, v, effectiveTtl);

        // One listing serves the match and the CNAME-coexistence check alike.
        var all = await client.ListRecordsAsync(token, z, ct);
        var sameName = all.Where(r => string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
        var existing = sameName.Where(r => string.Equals(r.Type, t, StringComparison.Ordinal)).ToList();

        // A CNAME must not coexist with other data at the same name (RFC 1034 §3.6.2) — refuse instead of
        // producing a name the resolvers will treat as broken.
        if (t == "CNAME")
        {
            var clash = sameName.FirstOrDefault(r => r.Type != "CNAME");
            if (clash is not null)
                throw new ArgumentException($"Unter '{n}' existiert bereits ein {clash.Type}-Eintrag ({clash.Value}); ein CNAME darf nicht daneben stehen. Erst den anderen Eintrag löschen.");
        }
        else
        {
            var cname = sameName.FirstOrDefault(r => r.Type == "CNAME");
            if (cname is not null)
                throw new ArgumentException($"Unter '{n}' existiert bereits ein CNAME ({cname.Value}); daneben darf kein {t}-Eintrag stehen. Erst den CNAME löschen.");
        }

        if (t == "TXT")
            return await SetTxtAsync(client, token, z, wanted, existing, ct);

        if (existing.Count == 0)
        {
            var created = await client.CreateRecordAsync(token, z, wanted, ct);
            _logger.LogInformation("DNS record created: {Zone} {Name} {Type} (TTL {Ttl})", z, n, t, effectiveTtl);
            return new DnsSetResult(DnsSetAction.Created, null, created with { Name = n, Type = t, Value = v, Ttl = effectiveTtl });
        }

        // Idempotent update: one record of this name+type is the norm. Several (round-robin A records) are
        // collapsed onto one and the rest removed, so the result is exactly what the caller asked for. The one kept is
        // the record that already holds the value, if any — never a rewrite of another record into its duplicate,
        // which a provider refusing exact duplicates would turn down every time (Fable review).
        var primary = existing.FirstOrDefault(r => ValuesEqual(t, r.Value, v)) ?? existing[0];
        existing.Remove(primary);
        existing.Insert(0, primary);
        var same = ValuesEqual(t, primary.Value, v) && primary.Ttl == effectiveTtl && existing.Count == 1;
        if (same)
            return new DnsSetResult(DnsSetAction.Unchanged, primary, primary);

        if (string.IsNullOrEmpty(primary.Id))
            throw new DnsProviderException($"Der bestehende Eintrag {primary} hat keine ID — Aktualisierung nicht möglich.");

        // Every further record goes, so each needs an ID before anything is written: a half-collapsed set is refused up front.
        var extras = existing.Skip(1).ToList();
        var noId = extras.FirstOrDefault(e => string.IsNullOrEmpty(e.Id));
        if (noId is not null)
            throw new DnsProviderException($"Der weitere Eintrag {noId} hat keine ID — Zusammenführen nicht möglich.");

        var updated = await client.UpdateRecordAsync(token, z, primary.Id, wanted, ct);
        var removed = new List<DnsRecord>();
        foreach (var extra in extras)
        {
            try
            {
                await client.DeleteRecordAsync(token, z, extra.Id!, ct);
            }
            catch (Exception ex)
            {
                // Half collapsed: the primary is rewritten and some records are gone. The caller and the audit learn
                // all of it — what was changed, what went, what is still there (Fable review: a failure here used to
                // surface as a bare provider error).
                var remaining = extras.Except(removed).ToList();
                _logger.LogWarning(ex, "DNS set on {Zone} {Name} {Type} stopped half way: updated {Primary}, removed {Removed}, still there {Remaining}",
                    z, n, t, primary, removed.Count, remaining.Count);
                throw new DnsPartialSetException(primary, wanted with { Id = primary.Id }, removed, remaining, ex);
            }
            removed.Add(extra);
        }

        _logger.LogInformation("DNS record updated: {Zone} {Name} {Type} (TTL {Ttl}), {Removed} duplicate(s) removed",
            z, n, t, effectiveTtl, removed.Count);
        return new DnsSetResult(DnsSetAction.Updated, primary,
            updated with { Id = updated.Id ?? primary.Id, Name = n, Type = t, Value = v, Ttl = effectiveTtl })
        {
            Removed = removed,
        };
    }

    /// <summary>
    /// TXT records under one name are independent of each other — SPF, site verifications and DKIM keys share the apex —
    /// so a set adds the value or adjusts the TTL of the record that already holds it, and never touches the others
    /// (Fable review H3: a set used to replace every TXT record of the name).
    /// </summary>
    private async Task<DnsSetResult> SetTxtAsync(IDnsProviderClient client, string token, string z, DnsRecord wanted,
        List<DnsRecord> existing, CancellationToken ct)
    {
        var match = existing.FirstOrDefault(r => ValuesEqual("TXT", r.Value, wanted.Value));
        if (match is null)
        {
            var created = await client.CreateRecordAsync(token, z, wanted, ct);
            _logger.LogInformation("DNS record created: {Zone} {Name} TXT (TTL {Ttl}), {Others} other TXT record(s) kept",
                z, wanted.Name, wanted.Ttl, existing.Count);
            return new DnsSetResult(DnsSetAction.Created, null, created with { Name = wanted.Name, Type = "TXT", Value = wanted.Value, Ttl = wanted.Ttl });
        }

        if (match.Ttl == wanted.Ttl)
            return new DnsSetResult(DnsSetAction.Unchanged, match, match);

        if (string.IsNullOrEmpty(match.Id))
            throw new DnsProviderException($"Der bestehende Eintrag {match} hat keine ID — Aktualisierung nicht möglich.");

        var updated = await client.UpdateRecordAsync(token, z, match.Id, wanted, ct);
        _logger.LogInformation("DNS record updated: {Zone} {Name} TXT (TTL {Ttl})", z, wanted.Name, wanted.Ttl);
        return new DnsSetResult(DnsSetAction.Updated, match,
            updated with { Id = updated.Id ?? match.Id, Name = wanted.Name, Type = "TXT", Value = wanted.Value, Ttl = wanted.Ttl });
    }

    public async Task<List<DnsRecord>> DeleteAsync(string zone, string name, string type, string? value = null,
        CancellationToken ct = default)
    {
        var (client, token, z) = Resolve(zone);
        var t = NormalizeType(type);
        var n = NormalizeName(name, z);
        var v = string.IsNullOrEmpty(value) ? null : NormalizeValue(t, value);

        var victims = (await client.ListRecordsAsync(token, z, ct))
            .Where(r => Matches(r, n, t) && (v is null || ValuesEqual(t, r.Value, v)))
            .ToList();
        // Every ID is checked before the first delete, so a missing one never leaves the name half deleted.
        if (victims.FirstOrDefault(r => string.IsNullOrEmpty(r.Id)) is { } noId)
            throw new DnsProviderException($"Der Eintrag {noId} hat keine ID — Löschen nicht möglich.");
        foreach (var r in victims)
            await client.DeleteRecordAsync(token, z, r.Id!, ct);
        if (victims.Count > 0)
            _logger.LogInformation("DNS record(s) deleted: {Zone} {Name} {Type} ({Count})", z, n, t, victims.Count);
        return victims;
    }

    // --- resolution + validation ----------------------------------------------------------------------------

    private (IDnsProviderClient client, string token, string zone) Resolve(string zone)
    {
        var settings = _settings.CurrentValue;
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Kein DNS-Provider konfiguriert. Token unter Einstellungen → DNS hinterlegen.");

        var providerId = (settings.Provider ?? "").Trim().ToLowerInvariant();
        var client = _clients.FirstOrDefault(c => string.Equals(c.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unbekannter DNS-Provider '{settings.Provider}'. Unterstützt: {string.Join(", ", _clients.Select(c => c.ProviderId))}.");

        var z = NormalizeZone(zone);
        var allowed = settings.AllowedZones?
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().TrimEnd('.').ToLowerInvariant())
            .ToList() ?? new();
        if (allowed.Count > 0 && !allowed.Contains(z, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Zone '{z}' ist nicht freigegeben. Erlaubte Zonen: {string.Join(", ", allowed)}.");

        return (client, TokenOf(settings), z);
    }

    /// <summary>The provider token: from the vault (R1-29), else a plaintext one not migrated yet or set by the
    /// environment. A token in the vault needs a usable vault — a locked or disabled vault is an error.</summary>
    private string TokenOf(DnsSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiTokenRef)) return settings.ApiToken;
        if (_vault is not { IsEnabled: true })
            throw new InvalidOperationException(_vault?.KeyState == Whiskers.Services.Vault.VaultKeyState.Wrong
                ? "Der DNS-Token liegt im Vault, und der Vault ist gesperrt (VAULT_KEY passt nicht)."
                : "Der DNS-Token liegt im Vault, und der Vault ist deaktiviert (VAULT_KEY fehlt).");
        return _vault.GetSecret(settings.ApiTokenRef)
               ?? throw new InvalidOperationException("Der DNS-Token fehlt im Vault. Token unter Einstellungen → DNS neu hinterlegen.");
    }

    public static string NormalizeZone(string? zone)
    {
        var z = (zone ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (z.Length == 0) throw new ArgumentException("Zone fehlt (z. B. 'example.org').");
        if (!z.Contains('.') || z.Split('.').Any(l => !LabelPattern.IsMatch(l)))
            throw new ArgumentException($"'{zone}' ist kein gültiger Zonenname.");
        return z;
    }

    /// <summary>"@", "", the zone itself or "zone." → "@"; "www.zone" → "www"; "www" stays. Labels validated.</summary>
    public static string NormalizeName(string? name, string zone)
    {
        var n = (name ?? "").Trim().TrimEnd('.');
        if (n.Length == 0 || n == "@") return "@";
        n = n.ToLowerInvariant();
        if (string.Equals(n, zone, StringComparison.OrdinalIgnoreCase)) return "@";
        if (n.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            n = n[..^(zone.Length + 1)];
        if (n.Length == 0) return "@";
        if (n.Length > 200 || n.Split('.').Any(l => l != "*" && !LabelPattern.IsMatch(l)))
            throw new ArgumentException($"'{name}' ist kein gültiger Eintragsname (Labels: Buchstaben, Ziffern, '-', '_'; '*' für Wildcards).");
        return n;
    }

    public static string NormalizeType(string? type)
    {
        var t = (type ?? "").Trim().ToUpperInvariant();
        if (!SupportedTypes.Contains(t))
            throw new ArgumentException($"Typ '{type}' wird nicht unterstützt. Erlaubt: {string.Join(", ", SupportedTypes)}.");
        return t;
    }

    /// <summary>Type-specific value validation: A = IPv4, AAAA = IPv6, CNAME = hostname, TXT = non-empty and
    /// free of control characters. Returns the value as it goes on the wire.</summary>
    public static string NormalizeValue(string type, string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) throw new ArgumentException("Wert fehlt.");

        switch (type)
        {
            case "A":
                // IPAddress.TryParse accepts shorthand like "1.2.3" — insist on four dotted octets.
                if (v.Count(c => c == '.') != 3 || !IPAddress.TryParse(v, out var ip4) || ip4.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException($"'{v}' ist keine gültige IPv4-Adresse.");
                return ip4.ToString();
            case "AAAA":
                if (!IPAddress.TryParse(v, out var ip6) || ip6.AddressFamily != AddressFamily.InterNetworkV6)
                    throw new ArgumentException($"'{v}' ist keine gültige IPv6-Adresse.");
                return ip6.ToString();
            case "CNAME":
                var host = v.TrimEnd('.').ToLowerInvariant();
                if (host.Length == 0 || host.Length > 253 || host.Split('.').Any(l => !LabelPattern.IsMatch(l)))
                    throw new ArgumentException($"'{v}' ist kein gültiger Hostname für einen CNAME.");
                return host;
            case "TXT":
                if (v.Length > 4096 || v.Any(char.IsControl))
                    throw new ArgumentException("TXT-Wert enthält Steuerzeichen oder ist länger als 4096 Zeichen.");
                return v;
            default:
                throw new ArgumentException($"Typ '{type}' wird nicht unterstützt.");
        }
    }

    private static bool Matches(DnsRecord r, string name, string type) =>
        string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Type, type, StringComparison.Ordinal);

    /// <summary>Provider-tolerant equality: hostnames case-insensitively and without trailing dot, TXT with or
    /// without the surrounding quotes some providers add, IPs by parsed value.</summary>
    public static bool ValuesEqual(string type, string a, string b)
    {
        switch (type)
        {
            case "A":
            case "AAAA":
                return IPAddress.TryParse(a, out var x) && IPAddress.TryParse(b, out var y) && x.Equals(y);
            case "CNAME":
                return string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
            case "TXT":
                return string.Equals(Unquote(a), Unquote(b), StringComparison.Ordinal);
            default:
                return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    private static string Unquote(string s)
    {
        var t = s.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            t = t[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return t;
    }
}

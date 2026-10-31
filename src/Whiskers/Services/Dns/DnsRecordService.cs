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

        if (existing.Count == 0)
        {
            var created = await client.CreateRecordAsync(token, z, wanted, ct);
            _logger.LogInformation("DNS record created: {Zone} {Name} {Type} (TTL {Ttl})", z, n, t, effectiveTtl);
            return new DnsSetResult(DnsSetAction.Created, null, created with { Name = n, Type = t, Value = v, Ttl = effectiveTtl });
        }

        // Idempotent update: one record of this name+type is the norm. Several (round-robin A records) are
        // collapsed onto the first and the rest removed, so the result is exactly what the caller asked for.
        var primary = existing[0];
        var same = ValuesEqual(t, primary.Value, v) && primary.Ttl == effectiveTtl && existing.Count == 1;
        if (same)
            return new DnsSetResult(DnsSetAction.Unchanged, primary, primary);

        if (string.IsNullOrEmpty(primary.Id))
            throw new DnsProviderException($"Der bestehende Eintrag {primary} hat keine ID — Aktualisierung nicht möglich.");

        var updated = await client.UpdateRecordAsync(token, z, primary.Id, wanted, ct);
        foreach (var extra in existing.Skip(1).Where(e => !string.IsNullOrEmpty(e.Id)))
            await client.DeleteRecordAsync(token, z, extra.Id!, ct);

        _logger.LogInformation("DNS record updated: {Zone} {Name} {Type} (TTL {Ttl}), {Removed} duplicate(s) removed",
            z, n, t, effectiveTtl, existing.Count - 1);
        return new DnsSetResult(DnsSetAction.Updated, primary,
            updated with { Id = updated.Id ?? primary.Id, Name = n, Type = t, Value = v, Ttl = effectiveTtl });
    }

    public async Task<List<DnsRecord>> DeleteAsync(string zone, string name, string type, CancellationToken ct = default)
    {
        var (client, token, z) = Resolve(zone);
        var t = NormalizeType(type);
        var n = NormalizeName(name, z);

        var victims = (await client.ListRecordsAsync(token, z, ct)).Where(r => Matches(r, n, t)).ToList();
        foreach (var r in victims)
        {
            if (string.IsNullOrEmpty(r.Id))
                throw new DnsProviderException($"Der Eintrag {r} hat keine ID — Löschen nicht möglich.");
            await client.DeleteRecordAsync(token, z, r.Id, ct);
        }
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

using System.Text.RegularExpressions;
using Whiskers.Services.Mutations;
using Whiskers.Utils;

namespace Whiskers.Services.Server;

/// <summary>
/// Read-backs of the host capabilities (R1-37, INV-43): after the call returned, the host is asked again — ufw, the
/// nginx site files, systemctl, certbot's certificates. A read that could not be made (no connection, a failed command)
/// fails the outcome with the state unknown; a read that shows another state fails it as a bad state.
/// </summary>
public sealed partial class FirewallRuleAddVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "firewall.rule_add";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var server = RequireServer(context);
        var port = VerifierSupport.Arg(context, "port")
                   ?? throw new MutationNotVerifiableException("the call named no port to read back.");
        // The MCP tool takes "both" for any protocol, as add_firewall_rule's description says.
        var protocol = (VerifierSupport.Arg(context, "protocol") ?? "tcp").Trim().ToLowerInvariant() switch
        {
            "both" => "any",
            var p => p,
        };
        var action = (VerifierSupport.Arg(context, "action") ?? "allow").ToLowerInvariant();
        var from = VerifierSupport.Arg(context, "from");

        // `ufw show added` lists the rules also while ufw is inactive, when `ufw status` shows none.
        var added = await VerifierSupport.HostOutputAsync(Services, server, "ufw show added", ct);
        if (!added.Split('\n').Any(line => Matches(line.Trim(), action, port, protocol, from)))
            throw new MutationVerificationFailedException(
                $"ufw lists no '{action}' rule for {port}/{protocol}{(from is null ? "" : $" from {from}")}.");
    }

    /// <summary>ufw lists a source as it normalised it: an IPv6 address lower-cased, a network with its host bits cleared
    /// (<c>10.0.0.5/24</c> → <c>10.0.0.0/24</c>). Compared as addresses; anything that is no address, as text.</summary>
    internal static bool SameSource(string listed, string asked)
    {
        if (IsAnySource(listed) && IsAnySource(asked)) return true;
        if (Network(listed) is { } a && Network(asked) is { } b)
            return a.Prefix == b.Prefix && a.Address.Equals(b.Address);
        return string.Equals(listed, asked, StringComparison.OrdinalIgnoreCase);

        static (System.Net.IPAddress Address, int Prefix)? Network(string text)
        {
            var slash = text.IndexOf('/');
            if (!System.Net.IPAddress.TryParse(slash < 0 ? text : text[..slash], out var ip)) return null;
            var bits = ip.GetAddressBytes();
            var prefix = bits.Length * 8;
            if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > bits.Length * 8)) return null;
            for (var bit = prefix; bit < bits.Length * 8; bit++) bits[bit / 8] &= (byte)~(0x80 >> (bit % 8));
            return (new System.Net.IPAddress(bits), prefix);
        }
    }

    /// <summary>Every source: <c>Anywhere</c> (which the dialog accepts), <c>any</c>, <c>0.0.0.0/0</c>, <c>::/0</c>.</summary>
    internal static bool IsAnySource(string source) =>
        source.Equals("anywhere", StringComparison.OrdinalIgnoreCase) || source.Equals("any", StringComparison.OrdinalIgnoreCase)
        || source is "0.0.0.0/0" or "::/0";

    /// <summary>A line of <c>ufw show added</c> (<c>ufw allow 80/tcp</c>, <c>ufw allow from 10.0.0.1 to any port 22
    /// proto tcp</c>) that holds the rule.</summary>
    internal static bool Matches(string line, string action, string port, string protocol, string? from)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3 || tokens[0] != "ufw" || !string.Equals(tokens[1], action, StringComparison.OrdinalIgnoreCase))
            return false;
        var anyProto = protocol is "any";
        // A rule from every source may be listed without a source at all.
        if (from is not null && IsAnySource(from) && Matches(line, action, port, protocol, null)) return true;
        if (from is not null)
        {
            var i = Array.IndexOf(tokens, "from");
            if (i < 0 || i + 1 >= tokens.Length || !SameSource(tokens[i + 1], from)) return false;
            var p = Array.IndexOf(tokens, "port");
            if (p < 0 || p + 1 >= tokens.Length || tokens[p + 1] != port) return false;
            var proto = Array.IndexOf(tokens, "proto");
            var named = proto >= 0 && proto + 1 < tokens.Length ? tokens[proto + 1] : null;
            // The service writes "proto any" for any; ufw itself may also list such a rule without a proto.
            return anyProto ? named is null or "any" : named == protocol;
        }
        if (tokens.Contains("from")) return false;
        return anyProto
            ? tokens[^1] == port || tokens[^1] == $"{port}/any"
            : tokens[^1] == $"{port}/{protocol}";
    }
}

/// <summary>firewall.set_status: ufw reports the status the call asked for.</summary>
public sealed class FirewallStatusVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "firewall.set_status";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var server = RequireServer(context);
        var enable = VerifierSupport.BoolArg(context, "enable")
                     ?? throw new MutationNotVerifiableException("the call did not say whether to enable or disable ufw.");
        var status = FirewallService.Parse(await VerifierSupport.HostOutputAsync(Services, server, "ufw status", ct));
        if (status.Active != enable)
            throw new MutationVerificationFailedException($"ufw is {(status.Active ? "active" : "inactive")}.");
    }
}

/// <summary>nginx.update: the site's file in sites-available holds the configuration the call wrote.</summary>
public sealed partial class NginxUpdateVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "nginx.update";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var server = RequireServer(context);
        var site = NginxSite(context);
        var content = VerifierSupport.Arg(context, "content")
                      ?? throw new MutationNotVerifiableException("the call carried no configuration to compare.");
        var read = await VerifierSupport.HostReadAsync(Services, server,
            $"cat {ShellUtils.Quote($"/etc/nginx/sites-available/{site}")}", ct);
        // cat answers 1 for a missing file: the effect is not there. Anything else is no answer.
        if (read.ExitCode == 1)
            throw new MutationVerificationFailedException($"site '{site}' has no file in sites-available.");
        if (!read.Success)
            throw new MutationVerificationFailedException(
                $"the site file could not be read (exit code {read.ExitCode}: {VerifierSupport.FirstLine(read.Error)}).", stateUnknown: true);
        if (Normalize(read.Output) != Normalize(content))
            throw new MutationVerificationFailedException($"the file of site '{site}' does not hold the written configuration.");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    /// <summary>The site name, as the nginx service accepts it (no paths): anything else names nothing to read.</summary>
    internal static string NginxSite(MutationContext context)
    {
        var site = VerifierSupport.Arg(context, "siteName") ?? context.Request.Target.Name;
        return site is not null && SafeName().IsMatch(site)
            ? site
            : throw new MutationNotVerifiableException("the call named no valid site to read back.");
    }

    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$")]
    private static partial Regex SafeName();
}

/// <summary>nginx.site_enable / nginx.site_disable: the site's link in sites-enabled is there, or gone.</summary>
public sealed class NginxSiteVerifier(IServiceProvider services, bool enable) : ServiceVerifier(services)
{
    public override string CapabilityId => enable ? "nginx.site_enable" : "nginx.site_disable";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var server = RequireServer(context);
        var site = NginxUpdateVerifier.NginxSite(context);
        var link = await VerifierSupport.HostReadAsync(Services, server,
            $"test -L {ShellUtils.Quote($"/etc/nginx/sites-enabled/{site}")}", ct);
        // test answers 0 (there) or 1 (not there); anything else is no answer.
        if (link.ExitCode is not (0 or 1))
            throw new MutationVerificationFailedException($"sites-enabled could not be read (exit code {link.ExitCode}).", stateUnknown: true);
        if (link.Success != enable)
            throw new MutationVerificationFailedException(enable
                ? $"site '{site}' is not linked in sites-enabled."
                : $"site '{site}' is still linked in sites-enabled.");
    }
}

/// <summary>nginx.reload: nginx is still active after the reload (a reload that broke it leaves it failed).</summary>
public sealed class NginxReloadVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "nginx.reload";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var state = await SystemdVerifier.UnitStateAsync(Services, RequireServer(context), "is-active", "nginx", ct);
        if (state != "active") throw new MutationVerificationFailedException($"nginx is '{state}' after the reload.");
    }
}

/// <summary>systemd.manage: systemctl reports the unit active (start, restart), not active (stop), enabled (enable) or
/// disabled (disable).</summary>
public sealed partial class SystemdVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    public override string CapabilityId => "systemd.manage";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var server = RequireServer(context);
        var unit = VerifierSupport.Arg(context, "serviceName") ?? context.Request.Target.Name;
        if (unit is null || !SafeUnit().IsMatch(unit))
            throw new MutationNotVerifiableException("the call named no valid unit to read back.");
        var action = VerifierSupport.Arg(context, "action")?.Trim().ToLowerInvariant();
        switch (action)
        {
            case "start" or "restart":
                var active = await UnitStateAsync(Services, server, "is-active", unit, ct);
                if (active == "active" || await RanToCompletionAsync(server, unit, ct)) break;
                throw new MutationVerificationFailedException($"{unit} is '{active}', not active.");
            case "stop":
                var stopped = await UnitStateAsync(Services, server, "is-active", unit, ct);
                if (stopped is "active" or "activating" or "reloading")
                    throw new MutationVerificationFailedException($"{unit} is still '{stopped}'.");
                break;
            case "enable" or "disable":
                var enabled = await UnitStateAsync(Services, server, "is-enabled", unit, ct);
                // Units without an [Install] section, aliases and generated units are not enabled or disabled themselves.
                if (enabled is "static" or "indirect" or "generated" or "alias" or "transient")
                    throw new MutationNotVerifiableException($"{unit} is '{enabled}': it is not enabled or disabled itself.");
                var want = action == "enable" ? "enabled" : "disabled";
                if (enabled != want) throw new MutationVerificationFailedException($"{unit} is '{enabled}', not {want}.");
                break;
            default:
                throw new MutationNotVerifiableException($"the action '{action}' has no read-back.");
        }
    }

    /// <summary>A <c>Type=oneshot</c> unit ends inactive after a successful start: it ran, its <c>Result</c> says how.</summary>
    private async Task<bool> RanToCompletionAsync(string server, string unit, CancellationToken ct)
    {
        var show = await VerifierSupport.HostOutputAsync(Services, server,
            $"systemctl show -p Type -p Result {ShellUtils.Quote(unit)}", ct);
        var props = show.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        return props.GetValueOrDefault("Type") == "oneshot" && props.GetValueOrDefault("Result") == "success";
    }

    /// <summary>The one-word answer of <c>systemctl is-active</c> / <c>is-enabled</c>. Their exit code is not 0 for
    /// "inactive" or "disabled", so the word decides; no word at all is no answer.</summary>
    internal static async Task<string> UnitStateAsync(IServiceProvider services, string server, string query, string unit,
        CancellationToken ct)
    {
        var read = await VerifierSupport.HostReadAsync(services, server, $"systemctl {query} {ShellUtils.Quote(unit)}", ct);
        var word = read.Output.Trim().Split('\n')[0].Trim();
        if (word.Length == 0)
            throw new MutationVerificationFailedException($"systemctl {query} gave no answer ({VerifierSupport.FirstLine(read.Error)}).",
                stateUnknown: true);
        return word;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._@-]*$")]
    private static partial Regex SafeUnit();
}

/// <summary>ssl.renew: the renewed certificate (or every certificate, for "all") exists and is valid beyond certbot's
/// renewal window — a certificate certbot should have renewed and did not still expires within it.</summary>
public sealed class SslRenewVerifier(IServiceProvider services) : ServiceVerifier(services)
{
    /// <summary>certbot renews certificates with less than 30 days left; one day of slack for the date-only expiry.</summary>
    internal const int MinDaysAfterRenewal = 29;

    public override string CapabilityId => "ssl.renew";

    protected override async Task CheckAsync(MutationContext context, object? result, CancellationToken ct)
    {
        var server = RequireServer(context);
        var name = VerifierSupport.Arg(context, "certName");
        // The tool takes "all" in any case, as renew_ssl_certificate does.
        var all = string.IsNullOrWhiteSpace(name) || string.Equals(name, "all", StringComparison.OrdinalIgnoreCase);
        var certs = await Services.GetRequiredService<ISslCertService>().ListCertificatesAsync(server);
        if (certs.Count == 0 && all)
        {
            // The listing answers empty also when it could not be read: ask once more, so that only a certbot that
            // ran and manages nothing (or no certbot at all) counts as nothing to renew.
            var check = await VerifierSupport.HostReadAsync(Services, server,
                "command -v certbot >/dev/null 2>&1 || exit 0; certbot certificates >/dev/null 2>&1", ct);
            if (!check.Success)
                throw new MutationVerificationFailedException(
                    $"certbot's certificates could not be read (exit code {check.ExitCode}).", stateUnknown: true);
            throw new MutationNotVerifiableException("certbot manages no certificate on this host: there was nothing to renew.");
        }
        if (certs.Count == 0)
            throw new MutationVerificationFailedException("certbot listed no certificate.", stateUnknown: true);
        var renewed = all ? certs : certs.Where(c => c.CertName == name).ToList();
        if (renewed.Count == 0)
            throw new MutationVerificationFailedException($"certbot lists no certificate '{name}'.");
        var stale = renewed.FirstOrDefault(c => c.DaysUntilExpiry is not { } d || d < MinDaysAfterRenewal);
        if (stale is not null)
            throw stale.DaysUntilExpiry is { } days
                ? new MutationVerificationFailedException($"'{stale.CertName}' still expires in {days} day(s).")
                : new MutationVerificationFailedException($"the expiry of '{stale.CertName}' could not be read.", stateUnknown: true);
    }
}

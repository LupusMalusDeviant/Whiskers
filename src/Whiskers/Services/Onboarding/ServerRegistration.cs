using System.Net;
using System.Net.Sockets;
using Whiskers.Models;
using Whiskers.Services.Certificates;
using Whiskers.Services.Cloud;
using Whiskers.Services.Hetzner;
using Whiskers.Services.Hostinger;
using Whiskers.Services.Mutations;
using Whiskers.Services.ServerConfig;
using Whiskers.Services.Vault;
using Whiskers.Utils;

namespace Whiskers.Services.Onboarding;

/// <summary>
/// Contract 0018 part B (D7, D10, D11): registers a server from name and public IP. The record is SSH to
/// <c>root@ip:22</c> with no credential; a stored cloud credential of the provider is found by the VM's exact public IP
/// (never by name) and shared by reference — no secret is copied, nothing is written to the vault.
/// </summary>
public sealed class ServerRegistration : IServerRegistration, ICloudVmReader
{
    public async Task<CloudBinding?> BoundVmAsync(Models.ServerConfig server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.CloudProvider != CloudProvider.Hetzner || server.CloudServerId is not { } vmId) return null;
        var hetzner = _services.GetService<IHetznerService>() ?? throw new InvalidOperationException("The Hetzner client is not available.");
        var vault = _services.GetService<IVaultService>() ?? throw new InvalidOperationException("No vault is available.");
        var token = CloudCredentials.KeyOf(server, vault) ?? throw new InvalidOperationException("The record has no Hetzner token.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProviderTimeout);
        var vm = await hetzner.GetServerAsync(token, vmId, cts.Token) ?? throw new InvalidOperationException($"Hetzner knows no VM {vmId}.");
        return new CloudBinding(CloudProvider.Hetzner, vm.Id, vm.Name, vm.Created,
            new Dictionary<string, string>(vm.Labels ?? [], StringComparer.Ordinal), vm.Status)
        { Type = vm.ServerType?.Name, PublicIp = vm.Ipv4 };
    }

    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(20);

    private readonly IServerConfigService _servers;
    private readonly IServiceProvider _services;

    public ServerRegistration(IServerConfigService servers, IServiceProvider services)
    {
        _servers = servers;
        _services = services;
    }

    public async Task<ServerRegistrationResult> AddAsync(ServerRegistrationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = (request.Name ?? "").Trim();
        if (name.Length is < 1 or > 64) throw new ServerRegistrationException("The name needs 1 to 64 characters.");
        var ip = PublicIp(request.PublicIp, request.CloudProvider);
        var all = _servers.GetServers();
        if (all.Any(s => SameAddress(s.SshHost, ip) || SameAddress(s.TcpHost, ip)))
            throw new ServerRegistrationException($"Another server already uses {ip}.");

        var warnings = new List<string>();
        Match? match = null;
        if (request.CloudProvider != CloudProvider.None)
        {
            match = await FindVmAsync(request, ip, all, ct);
            if (match is null)
                warnings.Add($"No stored {request.CloudProvider} credential: the server is registered without a VM binding. " +
                             "Enter the provider token in the server's form (Advanced) to bind it.");
            else if (all.Any(s => s.CloudProvider == request.CloudProvider && s.CloudServerId == match.Binding.CloudServerId))
                throw new ServerRegistrationException($"Another server is already bound to VM {match.Binding.CloudServerId}.");
        }

        var id = UniqueId(OnboardingCommands.Slug(name), all);
        var record = new Models.ServerConfig
        {
            Id = id,
            Name = name,
            ConnectionType = ConnectionType.SSH,
            SshHost = ip,
            SshUser = "root",
            SshPort = 22,
            Enabled = true,
            Group = string.IsNullOrWhiteSpace(request.Group) ? null : request.Group.Trim(),
            CloudProvider = request.CloudProvider,
            CloudKeyRef = match?.CredentialRef,
            CloudServerId = match?.Binding.CloudServerId,
        };
        await MtlsOnboardingState.HoldHostAsync(_services.GetService<IMtlsClientCredentialStore>(), id, name, async () =>
        {
            await _servers.AddServerAsync(record);
            return true;
        });
        MutationScope.Note($"server.create: {name} at {ip}, provider {request.CloudProvider}" +
                           (match is null ? ", no VM binding"
                               : $", credential of server {match.SourceServerId}, VM {match.Binding.CloudServerId} '{match.Binding.CloudServerName}'"));
        return new ServerRegistrationResult(id, match?.Binding, warnings) { CredentialFrom = match?.SourceServerId };
    }

    private sealed record Match(CloudBinding Binding, string CredentialRef, string SourceServerId);

    /// <summary>Rule 2: an IPv4 or IPv6 literal, normalised, that is a public address; Hetzner: IPv4 only.</summary>
    internal static string PublicIp(string? value, CloudProvider provider)
    {
        var text = (value ?? "").Trim();
        if (!IPAddress.TryParse(text, out var address) || !CommandOperands.IsSafeHost(text))
            throw new ServerRegistrationException("The IP must be an IPv4 or IPv6 address (a host name is not accepted).");
        // IPAddress.TryParse also accepts shorthand IPv4 ("10.1") and zone ids; only the normal form is taken.
        var normal = address.ToString();
        if (address.AddressFamily == AddressFamily.InterNetwork && normal != text)
            throw new ServerRegistrationException("The IPv4 address must be written in full (a.b.c.d).");
        if (text.Contains('%') || (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0))
            throw new ServerRegistrationException("An IPv6 address with a zone is not accepted.");
        if (provider == CloudProvider.Hetzner && address.AddressFamily != AddressFamily.InterNetwork)
            throw new ServerRegistrationException("Hetzner servers are matched by their public IPv4.");
        if (!IsPublic(address))
            throw new ServerRegistrationException($"{normal} is not a public address (loopback, link-local, private, CGNAT and ULA are refused).");
        return normal;
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] is >= 64 and <= 127));
        }
        var v6 = address.GetAddressBytes();
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                 || (v6[0] & 0xFE) == 0xFC);
    }

    private static bool SameAddress(string? host, string ip) =>
        !string.IsNullOrWhiteSpace(host)
        && (string.Equals(host.Trim(), ip, StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim(), out var a) && IPAddress.TryParse(ip, out var b) && a.Equals(b)));

    private static string UniqueId(string slug, IReadOnlyList<Models.ServerConfig> all)
    {
        var taken = all.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(slug)) return slug;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{slug}-{n}")) return $"{slug}-{n}";
    }

    /// <summary>Rule 5 (D7): the VM with exactly this public IP, in exactly one stored credential of the provider. A
    /// credential is a vault reference (a plaintext legacy key cannot be shared without copying it, so it is skipped).
    /// Null when no such credential is stored at all.</summary>
    private async Task<Match?> FindVmAsync(ServerRegistrationRequest request, string ip, IReadOnlyList<Models.ServerConfig> all,
        CancellationToken ct)
    {
        var vault = _services.GetService<IVaultService>() ?? throw new ServerRegistrationException("No vault is available.");
        List<Models.ServerConfig> sources;
        if (!string.IsNullOrWhiteSpace(request.CloudCredentialFrom))
        {
            var named = all.FirstOrDefault(s => string.Equals(s.Id, request.CloudCredentialFrom, StringComparison.Ordinal))
                        ?? throw new ServerRegistrationException($"Unknown server '{request.CloudCredentialFrom}'.");
            if (named.CloudProvider != request.CloudProvider || string.IsNullOrWhiteSpace(named.CloudKeyRef))
                throw new ServerRegistrationException($"{named.Name} holds no stored {request.CloudProvider} credential.");
            sources = [named];
        }
        else
        {
            sources = all.Where(s => s.CloudProvider == request.CloudProvider && !string.IsNullOrWhiteSpace(s.CloudKeyRef))
                .GroupBy(s => s.CloudKeyRef!, StringComparer.Ordinal).Select(g => g.First()).ToList();
            if (sources.Count == 0) return null;
        }

        var matches = new List<Match>();
        foreach (var source in sources)
        {
            string token;
            try
            {
                token = CloudCredentials.KeyOf(source, vault) ?? throw new InvalidOperationException("no key");
            }
            catch (InvalidOperationException ex)
            {
                // A locked vault or a missing entry refuses: a partial search could pick the wrong account.
                throw new ServerRegistrationException($"The credential of {source.Name} cannot be read: {ex.Message}");
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProviderTimeout);
            try
            {
                foreach (var binding in await VmsWithIpAsync(request.CloudProvider, token, ip, cts.Token))
                    matches.Add(new Match(binding, source.CloudKeyRef!, source.Id));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                throw new ServerRegistrationException($"The VMs of the credential of {source.Name} cannot be listed: {ex.Message}");
            }
        }
        var names = string.Join(", ", sources.Select(s => s.Name));
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ServerRegistrationException(
                $"No {request.CloudProvider} VM has the public IP {ip} in the stored credentials (of: {names})."),
            _ => throw new ServerRegistrationException(
                $"Several {request.CloudProvider} VMs have the public IP {ip} (credentials of: " +
                $"{string.Join(", ", matches.Select(m => all.First(s => s.Id == m.SourceServerId).Name).Distinct())}); name the credential to use."),
        };
    }

    private async Task<IReadOnlyList<CloudBinding>> VmsWithIpAsync(CloudProvider provider, string token, string ip, CancellationToken ct)
    {
        switch (provider)
        {
            case CloudProvider.Hetzner:
                var hetzner = _services.GetService<IHetznerService>() ?? throw new InvalidOperationException("The Hetzner client is not available.");
                return (await hetzner.ListServersAsync(token, ct)).Where(v => string.Equals(v.Ipv4, ip, StringComparison.Ordinal))
                    .Select(v => new CloudBinding(CloudProvider.Hetzner, v.Id, v.Name, v.Created,
                        new Dictionary<string, string>(v.Labels ?? [], StringComparer.Ordinal), v.Status) { Type = v.ServerType?.Name })
                    .ToList();
            case CloudProvider.Hostinger:
                var hostinger = _services.GetService<IHostingerService>() ?? throw new InvalidOperationException("The Hostinger client is not available.");
                return (await hostinger.ListVmsAsync(token, ct)).Where(v => v.Ipv4?.Any(a => string.Equals(a.Address, ip, StringComparison.Ordinal)) == true)
                    .Select(v => new CloudBinding(CloudProvider.Hostinger, v.Id, v.Hostname ?? v.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        v.CreatedAt, new Dictionary<string, string>(), v.State ?? "") { Type = v.Plan })
                    .ToList();
            default:
                return [];
        }
    }
}

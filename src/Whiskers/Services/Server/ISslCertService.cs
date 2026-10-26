using Whiskers.Services.Mutations;
namespace Whiskers.Services.Server;

/// <summary>Lists and renews TLS certificates (certbot) on a server.</summary>
public interface ISslCertService
{
    Task<List<SslCertificate>> ListCertificatesAsync(string serverId);
    [MutatingOperation("ssl.renew")]
    Task<CommandResult> RenewAsync(string serverId, string certName);
    [MutatingOperation("ssl.renew")]
    Task<CommandResult> RenewAllAsync(string serverId);
}

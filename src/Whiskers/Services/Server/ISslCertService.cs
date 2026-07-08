namespace Whiskers.Services.Server;

/// <summary>Lists and renews TLS certificates (certbot) on a server.</summary>
public interface ISslCertService
{
    Task<List<SslCertificate>> ListCertificatesAsync(string serverId);
    Task<CommandResult> RenewAsync(string serverId, string certName);
    Task<CommandResult> RenewAllAsync(string serverId);
}

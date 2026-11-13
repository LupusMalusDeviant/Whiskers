using Whiskers.Models;

namespace Whiskers.Services.Docker;

/// <summary>Manages local SSH tunnels to remote Docker hosts (one local forward per server).</summary>
public interface ISshTunnelManager : IDisposable
{
    /// <summary>The Docker endpoint of the server's tunnel: a 0600 unix socket in a 0700 directory (a loopback port only on
    /// Windows).</summary>
    Task<Uri> EstablishTunnelAsync(Models.ServerConfig server);
    void CloseTunnel(string serverId);
    bool IsTunnelActive(string serverId);
}

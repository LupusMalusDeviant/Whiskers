using Whiskers.Models;

namespace Whiskers.Services.Docker;

/// <summary>Manages local SSH tunnels to remote Docker hosts (one local port per server).</summary>
public interface ISshTunnelManager : IDisposable
{
    Task<int> EstablishTunnelAsync(Models.ServerConfig server);
    void CloseTunnel(string serverId);
    bool IsTunnelActive(string serverId);
}

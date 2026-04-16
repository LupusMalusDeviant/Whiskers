using Docker.DotNet;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;

namespace ServerWatch.Services.Docker;

public class DockerConnectionFactory : IDisposable
{
    private readonly DockerClient _client;

    public DockerConnectionFactory(IOptions<DockerSettings> settings)
    {
        _client = new DockerClientConfiguration(
            new Uri(settings.Value.SocketPath))
            .CreateClient();
    }

    public DockerClient Client => _client;

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System.Net.Http;
using System.Net.Sockets;
using Whiskers.Services.Docker;

namespace Whiskers.Tests;

public class DockerConnectionFailureTests
{
    [Fact]
    public void ObjectDisposedException_CountsAsConnectionFailure()
    {
        // MIT-16: a client disposed mid-flight must retry against a fresh one, not surface the dispose.
        Assert.True(DockerConnectionManager.IsConnectionFailure(new ObjectDisposedException("DockerClient")));
    }

    [Fact]
    public void NestedSocketException_CountsAsConnectionFailure()
    {
        var ex = new HttpRequestException("connection refused", new SocketException(10061));
        Assert.True(DockerConnectionManager.IsConnectionFailure(ex));
    }

    [Fact]
    public void TimeoutAndIo_CountAsConnectionFailure()
    {
        Assert.True(DockerConnectionManager.IsConnectionFailure(new TimeoutException()));
        Assert.True(DockerConnectionManager.IsConnectionFailure(new IOException("pipe broken")));
    }

    [Fact]
    public void UnrelatedException_IsNotConnectionFailure()
    {
        Assert.False(DockerConnectionManager.IsConnectionFailure(new ArgumentException("bad arg")));
        Assert.False(DockerConnectionManager.IsConnectionFailure(new InvalidOperationException()));
    }
}

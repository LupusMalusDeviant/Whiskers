namespace Whiskers.Services.ImageUpdate;

/// <summary>Queries a container registry for the current remote image digest.</summary>
public interface IRegistryClient
{
    Task<string?> GetRemoteDigestAsync(string imageRef);
}

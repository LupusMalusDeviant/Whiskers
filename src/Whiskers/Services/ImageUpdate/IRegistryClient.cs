namespace Whiskers.Services.ImageUpdate;

/// <summary>Queries a container registry for the current remote image digest.</summary>
public interface IRegistryClient
{
    Task<string?> GetRemoteDigestAsync(string imageRef);

    /// <summary>R1-41: <see cref="GetRemoteDigestAsync(string)"/> whose cancellation reaches the registry requests (token,
    /// manifest); a cancelled caller gets an <see cref="OperationCanceledException"/>, not a null that reads as "could not
    /// reach the registry". The default implementation only stops waiting; <see cref="RegistryClient"/> cancels the
    /// requests.</summary>
    Task<string?> GetRemoteDigestAsync(string imageRef, CancellationToken ct) => GetRemoteDigestAsync(imageRef).WaitAsync(ct);
}

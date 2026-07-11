using Whiskers.Models;

namespace Whiskers.Services.Registries;

/// <summary>Registry credentials managed in the UI (F8). Consumed by the Docker image pull path:
/// <see cref="GetCredentialForImage"/> matches an image reference's registry host against the
/// configured registries and returns the vault-stored credential for authenticated pulls.</summary>
public interface IRegistryConfigService
{
    Task<List<RegistryConfig>> GetRegistriesAsync();

    /// <summary>Creates/updates. A non-null <paramref name="credential"/> is stored in the vault;
    /// null keeps an existing one; empty string removes it.</summary>
    Task<RegistryConfig> SaveRegistryAsync(RegistryConfig registry, string? credential);

    Task DeleteRegistryAsync(string registryId);

    /// <summary>Resolves (username, password, serverAddress) for the image reference's registry,
    /// or null when no configured registry matches / no credential is stored.</summary>
    (string Username, string Password, string ServerAddress)? GetCredentialForImage(string imageRef);
}

using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Onboarding;

/// <summary>Adds a server from the few facts a person knows: name, public IP, group, cloud provider. Capability
/// "server.create" (Admin), decided and audited through the gateway. Never takes a cloud API key.</summary>
public interface IServerRegistration
{
    [MutatingOperation("server.create")]
    Task<ServerRegistrationResult> AddAsync(ServerRegistrationRequest request, CancellationToken ct);
}

/// <param name="CloudCredentialFrom">Optional: the server whose stored cloud credential to reuse. Null = find it by IP (D7).</param>
public sealed record ServerRegistrationRequest(
    string Name,
    string PublicIp,
    string? Group,
    CloudProvider CloudProvider,
    string? CloudCredentialFrom);

/// <param name="CredentialFrom">The server whose stored credential matched (its id, never the token); null without one.</param>
public sealed record ServerRegistrationResult(string ServerId, CloudBinding? Cloud, IReadOnlyList<string> Warnings)
{
    public string? CredentialFrom { get; init; }
}

/// <summary>The provider VM the record was bound to at registration: by exact public IP, never by name.</summary>
public sealed record CloudBinding(CloudProvider Provider, long CloudServerId, string CloudServerName,
    string? CloudServerCreated, IReadOnlyDictionary<string, string> Labels, string Status)
{
    /// <summary>The VM type as the provider names it (shown in the dialog), when known.</summary>
    public string? Type { get; init; }

    /// <summary>The VM's public IPv4 as the provider reports it, when known.</summary>
    public string? PublicIp { get; init; }
}

/// <summary>Contract 0018 UI step 3: the bound VM as the provider reports it now (a read; nothing changes).</summary>
public interface ICloudVmReader
{
    /// <summary>The VM the record is bound to (<see cref="Models.ServerConfig.CloudServerId"/>), read now; null when the
    /// record has no Hetzner binding. Throws with a reason when it cannot be read.</summary>
    Task<CloudBinding?> BoundVmAsync(Models.ServerConfig server, CancellationToken ct);
}

/// <summary>A registration that was refused, with a reason fit for the person (no secret, no token).</summary>
public sealed class ServerRegistrationException(string message) : InvalidOperationException(message);

using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Whiskers.Services.Auth;

namespace Whiskers.Services.Mutations;

/// <summary>
/// Records that a user was seen signed in through Google or OIDC (R1-04), so an automation owner without a local
/// account counts as present (<see cref="IOwnerAccounts.RememberFederated"/>). Only observes the authentication state
/// a circuit already has; it changes nothing in the sign-in itself.
/// </summary>
public sealed class FederatedSignInRecorder : CircuitHandler
{
    /// <summary>Identities that are not a federated sign-in.</summary>
    private static readonly HashSet<string> NotFederated = new(StringComparer.Ordinal)
    {
        LocalPasswordAuthenticationType, AuthConstants.AuthDisabledScheme, AuthConstants.AgentSyntheticScheme, "McpBearer",
    };

    /// <summary>The authentication type of a local username/password sign-in.</summary>
    public const string LocalPasswordAuthenticationType = "LocalPassword";

    private readonly IServiceProvider _services;
    private readonly IOwnerAccounts _accounts;
    private readonly ILogger<FederatedSignInRecorder> _logger;

    // The circuit's AuthenticationStateProvider is resolved when the circuit connects, not at construction: it is
    // registered by the Blazor host, which a container built for a single module does not have.
    public FederatedSignInRecorder(IServiceProvider services, IOwnerAccounts accounts,
        ILogger<FederatedSignInRecorder> logger)
    {
        _services = services;
        _accounts = accounts;
        _logger = logger;
    }

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        try
        {
            if (_services.GetService<AuthenticationStateProvider>() is not { } auth) return;
            var user = (await auth.GetAuthenticationStateAsync()).User;
            if (FederatedEmailOf(user) is { } email) _accounts.RememberFederated(email);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording is best effort: an unrecorded owner stays unverified (refused), never the other way round.
            _logger.LogWarning(ex, "A federated sign-in could not be recorded");
        }
    }

    /// <summary>The email of a federated sign-in; <c>null</c> for a local, synthetic or anonymous identity.</summary>
    internal static string? FederatedEmailOf(ClaimsPrincipal? user)
    {
        var identity = user?.Identity;
        if (identity is not { IsAuthenticated: true } || string.IsNullOrEmpty(identity.AuthenticationType)
            || NotFederated.Contains(identity.AuthenticationType))
            return null;
        var email = user!.FindFirstValue(ClaimTypes.Email);
        return string.IsNullOrWhiteSpace(email) ? null : email;
    }
}

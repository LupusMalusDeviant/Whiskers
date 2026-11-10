using System.Security.Claims;
using System.Security.Cryptography;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Auth;

/// <summary>
/// Contract 0014 (D2): what identifies a person's web session and the credential it was signed in with. The session id
/// (<see cref="AuthConstants.SessionIdClaim"/>) is 128 random bits issued at sign-in; the credential is the federated
/// binding (<c>binding:&lt;id&gt;</c>), the local account (<c>local:&lt;Identity user id&gt;</c>) or
/// <see cref="PrincipalCredentials.AuthDisabled"/>. Neither is derived from a secret.
/// </summary>
public static class WebSessionClaims
{
    /// <summary>A new session id: 128 random bits, base64url.</summary>
    public static string NewSessionId() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>The claim a sign-in handler adds to the session it builds.</summary>
    public static Claim NewSessionClaim() => new(AuthConstants.SessionIdClaim, NewSessionId());

    /// <summary>The credential and session of a signed-in person, or nulls for what the principal does not carry.</summary>
    public static (string? CredentialId, string? SessionId) Of(ClaimsPrincipal? user)
    {
        if (user?.Identity is not { IsAuthenticated: true } identity) return (null, null);
        var session = user.FindFirst(AuthConstants.SessionIdClaim)?.Value;
        if (string.IsNullOrWhiteSpace(session)) session = null;
        string? credential = identity.AuthenticationType switch
        {
            AuthConstants.FederatedScheme when user.FindFirst(AuthConstants.BindingIdClaim)?.Value is { Length: > 0 } binding
                => PrincipalCredentials.Binding(binding),
            AuthConstants.LocalPasswordScheme when user.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } userId
                => PrincipalCredentials.LocalAccount(userId),
            AuthConstants.AuthDisabledScheme => PrincipalCredentials.AuthDisabled,
            _ => null,
        };
        return (credential, session);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

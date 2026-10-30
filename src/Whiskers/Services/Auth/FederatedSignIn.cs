using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace Whiskers.Services.Auth;

/// <summary>
/// The events of the Google and OIDC sign-in: the shared whitelist gate that runs after a provider authenticated the
/// user and before the local cookie is issued, and the record of a failed provider round trip. Both outcomes are
/// recorded in the audit log (R1-22); the record never changes the decision.
/// </summary>
public static class FederatedSignIn
{
    /// <summary>Enforces the email whitelist (both Google and OIDC deliver a <see cref="TicketReceivedContext"/>);
    /// a refused user is sent to <c>{pathBase}/login?error=unauthorized</c>.</summary>
    public static async Task WhitelistGateAsync(TicketReceivedContext context, string pathBase)
    {
        var whitelist = context.HttpContext.RequestServices.GetRequiredService<IWhitelistService>();
        var authAudit = context.HttpContext.RequestServices.GetRequiredService<IAuthAuditLog>();
        var email = context.Principal?.FindFirstValue(ClaimTypes.Email);
        var method = context.Scheme.Name.ToLowerInvariant();
        if (!whitelist.IsEmailAllowed(email))
        {
            context.Response.Redirect(pathBase + "/login?error=unauthorized");
            context.HandleResponse();
            await authAudit.SignInRefusedAsync(context.HttpContext, method, email, AuthAuditReasons.NotWhitelisted);
            return;
        }
        await authAudit.SignedInAsync(context.HttpContext, method, email ?? "");
    }

    /// <summary>A provider round trip that failed (IdP error, correlation failure): recorded, then handled as before —
    /// the response is not marked handled, so the default failure path still runs.</summary>
    public static Task RecordRemoteFailureAsync(RemoteFailureContext context) =>
        context.HttpContext.RequestServices.GetRequiredService<IAuthAuditLog>().SignInRefusedAsync(context.HttpContext,
            context.Scheme.Name.ToLowerInvariant(), null, AuthAuditReasons.ProviderFailure);
}

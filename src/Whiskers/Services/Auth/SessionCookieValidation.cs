using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Whiskers.Services.Auth;

/// <summary>
/// The cookie's <c>OnValidatePrincipal</c> (R1-26, INV-03/09): every request re-checks the session through
/// <see cref="ISessionValidator"/>. A session that is no longer valid — synthetic identity, deleted or changed local
/// account, changed security stamp, email no longer allowed to sign in, a store that cannot be read — is rejected and
/// its cookie removed; the revocation is audited (<c>auth.session_revoked</c>) with the reason only. Fail closed: the
/// former "any error allows through" is gone. The role is not part of the cookie; it is read per request.
/// </summary>
public static class SessionCookieValidation
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.HttpContext.RequestServices;
        SessionCheck check;
        try
        {
            check = await services.GetRequiredService<ISessionValidator>()
                .CheckAsync(context.Principal, context.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No validator, or one that broke its contract: the session cannot be shown to be valid.
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(SessionCookieValidation))
                .LogError("The session check failed ({Error}); the cookie is rejected", ex.GetType().Name);
            check = SessionCheck.Revoked(SessionRevocationReasons.StoreUnavailable);
        }
        if (check.Valid) return;

        var account = context.Principal?.FindFirstValue(ClaimTypes.Email);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (services.GetService<IAuthAuditLog>() is { } audit)
            await audit.SessionRevokedAsync(context.HttpContext, account, "cookie", check.Reason ?? SessionRevocationReasons.NotSignedIn);
    }
}

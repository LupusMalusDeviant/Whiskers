using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Whiskers.Configuration;

namespace Whiskers.Services.Auth;

/// <summary>
/// The events of the Google and OIDC sign-in: the shared gate that runs after a provider authenticated the user and
/// before the local cookie is issued, and the record of a failed provider round trip. Both outcomes are recorded in the
/// audit log (R1-22); the record never changes the decision.
/// <para>R1-27 (contract 0003): the gate's decision is <see cref="IFederatedAdmission"/>'s — on the provider's stable
/// subject, not on the email claim alone — and an admitted sign-in gets the minimal Whiskers principal in place of the
/// provider's: authentication type <see cref="AuthConstants.FederatedScheme"/>, the account handle as email and name, the
/// binding id. Nothing else from the IdP is carried. Every refusal is the same <c>/login?error=unauthorized</c>.</para>
/// </summary>
public static class FederatedSignIn
{
    /// <summary>Decides the sign-in (both Google and OIDC deliver a <see cref="TicketReceivedContext"/>); a refused user
    /// is sent to <c>{pathBase}/login?error=unauthorized</c>.</summary>
    public static async Task WhitelistGateAsync(TicketReceivedContext context, string pathBase)
    {
        var services = context.HttpContext.RequestServices;
        var authAudit = services.GetRequiredService<IAuthAuditLog>();
        var method = context.Scheme.Name.ToLowerInvariant();
        AdmissionDecision decision;
        try
        {
            var claims = ClaimsOf(context.Principal, method, services.GetService<IConfiguration>());
            decision = await services.GetRequiredService<IFederatedAdmission>().AdmitAsync(claims, context.HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            // Fail closed: no admission without a decision. The type only.
            services.GetService<ILoggerFactory>()?.CreateLogger(typeof(FederatedSignIn))
                .LogError("A {Provider} sign-in could not be decided ({Error}); it is refused", method, ex.GetType().Name);
            decision = AdmissionDecision.Refused(AuthAuditReasons.StoreUnavailable);
        }

        if (!decision.Admitted || string.IsNullOrEmpty(decision.Account) || string.IsNullOrEmpty(decision.BindingId))
        {
            // Contract 0009 (D5): a binding the kill switch blocked says so; every other refusal stays the same answer.
            context.Response.Redirect(pathBase + (decision.Reason == AuthAuditReasons.KillSwitch
                ? "/login?error=kill_switch"
                : "/login?error=unauthorized"));
            context.HandleResponse();
            // The account only when the address is a configured entry; a stranger is recorded as anonymous.
            await authAudit.SignInRefusedAsync(context.HttpContext, method, decision.Account,
                decision.Admitted ? AuthAuditReasons.StoreUnavailable : decision.Reason);
            return;
        }

        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, decision.Account),
            new Claim(ClaimTypes.Name, decision.Account),
            new Claim(AuthConstants.BindingIdClaim, decision.BindingId),
            // Contract 0014 (D2): the session's own id, so every action of it is attributed to this sign-in.
            WebSessionClaims.NewSessionClaim(),
        ], AuthConstants.FederatedScheme));
        await authAudit.SignedInAsync(context.HttpContext, method, decision.Account);
    }

    /// <summary>What the admission needs from the provider's principal (contract 0003, "Interfaces").</summary>
    internal static FederatedSignInClaims ClaimsOf(ClaimsPrincipal? principal, string provider, IConfiguration? configuration)
    {
        var google = provider == "google";
        // The subject: the handler's NameIdentifier (Google userinfo "sub"; the ID token's "sub" as mapped inbound),
        // else a "sub" claim. For OIDC its issuer is the validated ID token's.
        var subjectClaim = principal?.FindFirst(ClaimTypes.NameIdentifier) ?? principal?.FindFirst("sub");
        var issuer = google ? AuthConstants.GoogleIssuer : FederatedAdmission.IssuerOf(subjectClaim?.Issuer);
        var emailClaim = configuration?[$"{OidcSettings.SectionName}:{nameof(OidcSettings.EmailClaim)}"] ?? "email";
        return new FederatedSignInClaims(provider, issuer, subjectClaim?.Value, principal?.FindFirstValue(ClaimTypes.Email),
            EmailVerifiedOf(principal), EmailIsStandardClaim: google || string.Equals(emailClaim, "email", StringComparison.Ordinal));
    }

    /// <summary>The provider's <c>email_verified</c>: <c>true</c> only when every such claim says so (an ID token and a
    /// userinfo answer that disagree are not verified); <c>null</c> when there is none.</summary>
    private static string? EmailVerifiedOf(ClaimsPrincipal? principal)
    {
        var values = principal?.FindAll(AuthConstants.EmailVerifiedClaim).Select(c => c.Value).ToList() ?? [];
        if (values.Count == 0) return null;
        return values.FirstOrDefault(v => !IdentityBindingStore.IsTrue(v)) ?? "true";
    }

    /// <summary>A provider round trip that failed (IdP error, correlation failure): recorded, then handled as before —
    /// the response is not marked handled, so the default failure path still runs.</summary>
    public static Task RecordRemoteFailureAsync(RemoteFailureContext context) =>
        context.HttpContext.RequestServices.GetRequiredService<IAuthAuditLog>().SignInRefusedAsync(context.HttpContext,
            context.Scheme.Name.ToLowerInvariant(), null, AuthAuditReasons.ProviderFailure);
}

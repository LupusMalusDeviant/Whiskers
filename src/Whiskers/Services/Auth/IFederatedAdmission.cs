namespace Whiskers.Services.Auth;

/// <summary>
/// R1-27 (INV-05, contract 0003): decides whether a Google or OIDC sign-in is admitted, on the stable subject the
/// provider vouches for — the pair (issuer, subject) — not on the email claim alone. The email claim is used once per
/// provider: to bind a new subject to an address an administrator configured (a role entry or an enabled whitelist
/// listing), and only when a trusted provider asserts <c>email_verified=true</c> for the standard <c>email</c> claim;
/// everything else becomes a pending request an Admin or the host operator confirms. An instance without an explicit
/// Admin admits no federated sign-in. Fail closed: any error is a refusal.
/// </summary>
public interface IFederatedAdmission
{
    /// <summary>The decision for one provider round trip. Never throws, except <see cref="OperationCanceledException"/>
    /// for <paramref name="ct"/>.</summary>
    Task<AdmissionDecision> AdmitAsync(FederatedSignInClaims claims, CancellationToken ct = default);
}

/// <summary>What <see cref="FederatedSignIn"/> takes from the provider's ticket.</summary>
/// <param name="Provider">The scheme, lower case: <c>google</c> or <c>oidc</c>.</param>
/// <param name="Issuer">Google: <see cref="AuthConstants.GoogleIssuer"/>; OIDC: the issuer of the subject claim as the
/// handler set it from the validated ID token. Empty or <c>LOCAL AUTHORITY</c> is missing.</param>
/// <param name="Subject">The provider's <c>sub</c>.</param>
/// <param name="Email">The configured email claim (<c>ClaimTypes.Email</c>).</param>
/// <param name="EmailVerified">The provider's <c>email_verified</c> as delivered; only <c>true</c> (any case) counts.</param>
/// <param name="EmailIsStandardClaim">Whether the email came from the standard <c>email</c> claim (<c>Oidc:EmailClaim</c>
/// is <c>email</c>; always for Google) — <c>email_verified</c> vouches for nothing else.</param>
public sealed record FederatedSignInClaims(string Provider, string? Issuer, string? Subject, string? Email,
    string? EmailVerified, bool EmailIsStandardClaim)
{
    /// <summary>Never prints the email or the subject (a record that reaches a log stays clean).</summary>
    public override string ToString() =>
        $"FederatedSignInClaims {{ Provider = {Provider}, Issuer = {(string.IsNullOrEmpty(Issuer) ? "-" : "[set]")}, " +
        $"Subject = {(string.IsNullOrEmpty(Subject) ? "-" : "[set]")}, Email = {(string.IsNullOrEmpty(Email) ? "-" : "[set]")}, " +
        $"EmailVerified = {EmailVerified ?? "-"}, EmailIsStandardClaim = {EmailIsStandardClaim} }}";
}

/// <summary>The admission's answer. <paramref name="Account"/> is the account handle (the configured address): on an
/// admission the account the session is for, on a refusal only when the address is a configured entry (a stranger is
/// recorded as <c>anonymous</c>). <paramref name="Reason"/> is one of <see cref="AuthAuditReasons"/> (empty when
/// admitted). <paramref name="BoundNow"/> is true when this sign-in created the binding.</summary>
public sealed record AdmissionDecision(bool Admitted, string? Account, string? BindingId, string Reason, bool BoundNow)
{
    public static AdmissionDecision Refused(string reason, string? account = null) => new(false, account, null, reason, false);
    public static AdmissionDecision Admit(string account, string bindingId, bool boundNow) => new(true, account, bindingId, "", boundNow);

    public override string ToString() =>
        $"AdmissionDecision {{ Admitted = {Admitted}, Account = {(Account is null ? "-" : "[set]")}, BindingId = {BindingId ?? "-"}, " +
        $"Reason = {Reason}, BoundNow = {BoundNow} }}";
}

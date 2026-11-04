using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.Auth;

/// <summary>
/// R1-27: <see cref="IFederatedAdmission"/>, the rules of contract 0003 (rule 1) in their order. Any exception is a
/// refusal (<see cref="AuthAuditReasons.StoreUnavailable"/>); the log carries reasons, providers, binding ids and
/// exception types only — never an address or a subject.
/// </summary>
public sealed class FederatedAdmission : IFederatedAdmission
{
    private readonly IIdentityBindingStore _bindings;
    private readonly IRoleService _roles;
    private readonly IWhitelistService _whitelist;
    private readonly bool _trustGoogle;
    private readonly bool _trustOidc;
    private readonly ILogger<FederatedAdmission>? _logger;

    public FederatedAdmission(IIdentityBindingStore bindings, IRoleService roles, IWhitelistService whitelist,
        IConfiguration configuration, ILogger<FederatedAdmission>? logger = null)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        ArgumentNullException.ThrowIfNull(configuration);
        // Decision 3 b): Google's email_verified is trusted, a generic OIDC provider's is not unless the operator says so.
        _trustGoogle = Trusts(configuration, "google");
        _trustOidc = Trusts(configuration, "oidc");
        _logger = logger;
    }

    /// <summary>Whether <paramref name="provider"/>'s <c>email_verified</c> is trusted for an automatic binding (decision
    /// 3 b: Google by default, a generic OIDC provider only when the operator says so).</summary>
    internal static bool Trusts(IConfiguration configuration, string provider) => provider switch
    {
        "google" => configuration.GetValue($"{GoogleAuthSettings.SectionName}:{nameof(GoogleAuthSettings.TrustEmailVerified)}", true),
        "oidc" => configuration.GetValue($"{OidcSettings.SectionName}:{nameof(OidcSettings.TrustEmailVerified)}", false),
        _ => false,
    };

    public async Task<AdmissionDecision> AdmitAsync(FederatedSignInClaims claims, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        try
        {
            var decision = await DecideAsync(claims, ct);
            if (decision.Admitted)
                _logger?.LogInformation("A {Provider} sign-in was admitted on binding {Binding}{Bound}", claims.Provider,
                    decision.BindingId, decision.BoundNow ? " (bound now)" : "");
            else
                _logger?.LogInformation("A {Provider} sign-in was refused ({Reason})", claims.Provider, decision.Reason);
            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type only: a message may carry an address.
            _logger?.LogError("A {Provider} sign-in could not be decided ({Error}); it is refused", claims.Provider, ex.GetType().Name);
            return AdmissionDecision.Refused(AuthAuditReasons.StoreUnavailable);
        }
    }

    private async Task<AdmissionDecision> DecideAsync(FederatedSignInClaims claims, CancellationToken ct)
    {
        // 1. An instance without an explicit Admin admits no federated sign-in (decision 1 a).
        var roles = _roles.GetRoleData();
        if (!AccessEntries.HasExplicitAdmin(roles)) return AdmissionDecision.Refused(AuthAuditReasons.SetupIncomplete);

        // 2. The subject is the identity: without issuer and subject there is nothing to admit.
        var issuer = IssuerOf(claims.Issuer);
        if (issuer is null || string.IsNullOrEmpty(claims.Subject)) return AdmissionDecision.Refused(AuthAuditReasons.NoSubject);
        claims = claims with { Issuer = issuer };
        // An unavailable store gets one re-read and prune (a file repaired, a failed save behind it); still unavailable → refuse.
        if (!_bindings.IsAvailable && !await _bindings.RefreshAsync(ct))
            return AdmissionDecision.Refused(AuthAuditReasons.StoreUnavailable);

        // 3. A bound subject keeps its account, whatever address the IdP sends now.
        if (_bindings.Find(issuer, claims.Subject) is { } bound)
        {
            if (!_whitelist.IsEmailAllowed(bound.Account)) return AdmissionDecision.Refused(AuthAuditReasons.NotWhitelisted, bound.Account);
            var touched = await _bindings.RecordSignInAsync(bound.Id, ct);
            return touched.Success
                ? AdmissionDecision.Admit(bound.Account, bound.Id, boundNow: false)
                : AdmissionDecision.Refused(AuthAuditReasons.StoreUnavailable, bound.Account);
        }

        // 4. Unbound: the address must be a configured entry (a stranger is recorded as anonymous and leaves nothing).
        if (string.IsNullOrWhiteSpace(claims.Email)) return AdmissionDecision.Refused(AuthAuditReasons.NoEmail);
        if (AccessEntries.EntryOf(roles, _whitelist.GetWhitelist(), claims.Email) is not { } account)
            return AdmissionDecision.Refused(AuthAuditReasons.NotWhitelisted);
        if (!_whitelist.IsEmailAllowed(account)) return AdmissionDecision.Refused(AuthAuditReasons.NotWhitelisted, account);

        // 5. + 6. Whatever a person has to confirm becomes a pending request.
        var existing = _bindings.BindingsOf(account);
        var pending = existing.Any(b => b.Issuer == issuer) ? PendingIdentityReasons.SubjectChanged
            : existing.Count > 0 ? PendingIdentityReasons.SecondIssuer
            : !claims.EmailIsStandardClaim ? PendingIdentityReasons.NonstandardEmailClaim
            : !Trusted(claims.Provider) ? PendingIdentityReasons.ProviderUntrusted
            : !IdentityBindingStore.IsTrue(claims.EmailVerified) ? PendingIdentityReasons.EmailUnverified
            : null;
        if (pending is not null)
        {
            var recorded = await _bindings.RecordPendingAsync(claims, account, pending, ct);
            if (recorded.Success) return AdmissionDecision.Refused(AuthAuditReasons.PendingConfirmation, account);
            // Nothing waits for confirmation: the refusal says why, not "pending".
            _logger?.LogWarning("A binding request ({Reason}) could not be recorded ({Error})", pending, recorded.Error);
            return AdmissionDecision.Refused(recorded.Error == IdentityBindingErrors.NotFound
                ? AuthAuditReasons.NotWhitelisted
                : AuthAuditReasons.StoreUnavailable, account);
        }

        // 7. A trusted provider vouches for the configured address: bind (audited first), then admit.
        var bind = await _bindings.BindAtSignInAsync(claims, account, ct);
        if (bind is { Success: true, BindingId: { } id }) return AdmissionDecision.Admit(account, id, boundNow: true);
        return AdmissionDecision.Refused(bind.Error switch
        {
            IdentityBindingErrors.AuditUnavailable => AuthAuditReasons.AuditUnavailable,
            IdentityBindingErrors.Conflict => AuthAuditReasons.SubjectConflict,
            IdentityBindingErrors.NotFound => AuthAuditReasons.NotWhitelisted,
            _ => AuthAuditReasons.StoreUnavailable,
        }, account);
    }

    private bool Trusted(string provider) => provider switch
    {
        "google" => _trustGoogle,
        "oidc" => _trustOidc,
        _ => false,
    };
    // (Trusts is read again by the store under its gate, contract 0003 rule 1.7.)

    /// <summary>The issuer, or <c>null</c> when the handler set none (empty or the claims library's default).</summary>
    internal static string? IssuerOf(string? issuer) =>
        string.IsNullOrWhiteSpace(issuer) || string.Equals(issuer, System.Security.Claims.ClaimsIdentity.DefaultIssuer, StringComparison.Ordinal)
            ? null
            : issuer;
}

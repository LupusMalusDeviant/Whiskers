using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Whiskers.Models;
using Whiskers.Services.Mutations;

namespace Whiskers.Services.Auth;

/// <summary>
/// R1-27 (contract 0003): the identity bindings of Google/OIDC sign-ins — <c>(issuer, subject) → account handle</c> —
/// and the binding requests waiting for confirmation, in <c>identity-bindings.json</c>. A binding is created once (at a
/// trusted, verified first sign-in, by an Admin's confirmation or by the host CLI) and from then on the subject is the
/// identity; the account handle (the configured address) stays the key of roles, owners, approvals and audit.
/// <para>Every write runs under <see cref="AccessControlWriteGate"/> and is persisted before it is published. A file that
/// cannot be read makes the store unavailable: every read throws <see cref="IdentityBindingStoreUnavailableException"/>
/// and every write refuses <see cref="IdentityBindingErrors.StoreUnavailable"/> (fail closed). Nothing here logs an
/// email address or a subject; the people-facing views carry a fingerprint instead of the subject.</para>
/// </summary>
public interface IIdentityBindingStore
{
    /// <summary>False when the file could not be read or parsed at start (a directory in its place included).</summary>
    bool IsAvailable { get; }

    /// <summary>The binding of <paramref name="issuer"/> and <paramref name="subject"/> (both compared ordinally), or
    /// <c>null</c>.</summary>
    IdentityBindingInfo? Find(string issuer, string subject);

    /// <summary>The bindings of <paramref name="account"/> (compared without case).</summary>
    IReadOnlyList<IdentityBindingInfo> BindingsOf(string account);

    /// <summary>Every binding and every pending request that has not expired.</summary>
    IdentityBindingSnapshot Snapshot();

    /// <summary>The admission's automatic binding at a trusted, verified first sign-in — a designed exemption from the
    /// gateway like the setup wizard (S-13): nobody exists yet whom it could decide for. Under the gate it re-checks that
    /// the subject is unbound, that the account has no binding yet and is still a configured entry; then it writes
    /// <c>auth.identity_bound</c> — without that row nothing is written (<see cref="IdentityBindingErrors.AuditUnavailable"/>)
    /// — and persists the binding. Only <see cref="FederatedAdmission"/> calls it (pinned).</summary>
    Task<IdentityBindingResult> BindAtSignInAsync(FederatedSignInClaims claims, string account, CancellationToken ct = default);

    /// <summary>Records a binding request for <paramref name="account"/>, only when it is a configured entry (strangers leave
    /// no record). One per account and issuer (the newest wins), 24 h lifetime, at most 64 in total.</summary>
    Task<IdentityBindingResult> RecordPendingAsync(FederatedSignInClaims claims, string account, string reason,
        CancellationToken ct = default);

    /// <summary>Notes the day of an admitted sign-in on its binding, at most once per day. A failed save refuses the
    /// sign-in (fail closed).</summary>
    Task<IdentityBindingResult> RecordSignInAsync(string bindingId, CancellationToken ct = default);

    /// <summary>Admin: turns a pending request into a binding; an older binding of the same account and issuer is
    /// replaced.</summary>
    [MutatingOperation("identity.binding_confirm")]
    Task<IdentityBindingResult> ConfirmAsync(string pendingId, CancellationToken ct = default);

    /// <summary>Admin: removes a binding or a pending request. Refuses to remove the last way the last Admin who can sign
    /// in has (<see cref="IdentityBindingErrors.LastAdmin"/>).</summary>
    [MutatingOperation("identity.binding_remove")]
    Task<IdentityBindingResult> RemoveAsync(string id, CancellationToken ct = default);

    /// <summary>The host CLI (<c>--confirm-identity-binding</c>): confirms the newest pending request of
    /// <paramref name="account"/>, of <paramref name="issuer"/> when several issuers wait
    /// (<see cref="IdentityBindingErrors.Ambiguous"/> otherwise, with the candidates). Writes <c>auth.identity_bound</c>
    /// (actor <c>cli (--confirm-identity-binding)</c>) before the binding; without the row nothing changes. Only
    /// <see cref="IdentityBindingCli"/> calls it (pinned).</summary>
    Task<IdentityBindingResult> ConfirmFromHostAsync(string account, string? issuer, CancellationToken ct = default);

    /// <summary>Removes the bindings and pending requests of addresses that are no longer configured entries. Called by
    /// the role and whitelist writers <b>before</b> their write, under their held <paramref name="lease"/>, with the state
    /// they are about to write (<paramref name="nextRoles"/> / <paramref name="nextWhitelist"/>; the other half is read as
    /// it is); at start without either. Throws when the store is unavailable or the save fails — the caller's write is
    /// then refused. Returns how many records were removed.</summary>
    Task<int> PruneOrphansAsync(AccessControlWriteGate.Lease lease, UserRoleData? nextRoles = null,
        WhitelistData? nextWhitelist = null, CancellationToken ct = default);
}

/// <summary>A binding as the panel and the session check see it: never the subject, its fingerprint instead.</summary>
public sealed record IdentityBindingInfo(string Id, string Account, string Provider, string Issuer, string Fingerprint,
    string Method, DateTime BoundUtc, string BoundBy, string? LastSignInDay);

/// <summary>A pending binding request as the panel sees it.</summary>
public sealed record PendingIdentityInfo(string Id, string Account, string Provider, string Issuer, string Fingerprint,
    string Reason, bool EmailVerifiedAsserted, DateTime RequestedUtc, DateTime ExpiresUtc);

public sealed record IdentityBindingSnapshot(IReadOnlyList<IdentityBindingInfo> Bindings, IReadOnlyList<PendingIdentityInfo> Pending);

/// <summary>The outcome of a store write. <see cref="Success"/> is read by the gateway (a refused call is recorded as
/// Failed); <see cref="Error"/> is one of <see cref="IdentityBindingErrors"/>.</summary>
public sealed record IdentityBindingResult(bool Success, string? Error = null)
{
    /// <summary>The binding that was created or touched.</summary>
    public string? BindingId { get; init; }

    /// <summary>For <see cref="IdentityBindingErrors.Ambiguous"/>: the pending requests to choose from.</summary>
    public IReadOnlyList<PendingIdentityInfo> Candidates { get; init; } = [];

    public static IdentityBindingResult Ok(string? bindingId = null) => new(true) { BindingId = bindingId };
    public static IdentityBindingResult Refused(string error) => new(false, error);

    public override string ToString() => $"IdentityBindingResult {{ Success = {Success}, Error = {Error ?? "-"}, BindingId = {BindingId ?? "-"} }}";
}

/// <summary>Why a binding write was refused (the panel shows them through the <c>IdentityBindings_Error_*</c> texts).</summary>
public static class IdentityBindingErrors
{
    public const string NotAuthorized = "not_authorized";
    public const string SessionInvalid = "session_invalid";
    public const string NotFound = "not_found";
    public const string LastAdmin = "last_admin";
    public const string Ambiguous = "ambiguous";
    public const string StoreUnavailable = "store_unavailable";
    public const string AuditUnavailable = "audit_unavailable";
    public const string Conflict = "conflict";
    public const string AuthDisabled = "auth_disabled";
}

/// <summary>How a binding came about (<c>method</c> in the file).</summary>
public static class IdentityBindingMethods
{
    public const string VerifiedEmail = "verified_email";
    public const string Admin = "admin";
    public const string HostCli = "host_cli";
}

/// <summary>Why a binding request waits for confirmation (<c>reason</c> in the file).</summary>
public static class PendingIdentityReasons
{
    public const string EmailUnverified = "email_unverified";
    public const string ProviderUntrusted = "provider_untrusted";
    public const string SecondIssuer = "second_issuer";
    public const string SubjectChanged = "subject_changed";
    public const string NonstandardEmailClaim = "nonstandard_email_claim";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        EmailUnverified, ProviderUntrusted, SecondIssuer, SubjectChanged, NonstandardEmailClaim,
    };
}

/// <summary>The identity-binding file could not be read: nothing it would decide is decided (fail closed).</summary>
public sealed class IdentityBindingStoreUnavailableException(string message) : InvalidOperationException(message);

/// <summary>The configured entries (contract 0003, "Terms"): an explicit role entry, or a listing in an enabled whitelist.
/// The entry's address as configured is the account handle.</summary>
public static class AccessEntries
{
    /// <summary>The configured handle of <paramref name="email"/>, or <c>null</c> when it is no entry.</summary>
    public static string? EntryOf(UserRoleData roles, WhitelistData whitelist, string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var address = email.Trim();
        var role = roles.Roles.FirstOrDefault(r => string.Equals(r.Email?.Trim(), address, StringComparison.OrdinalIgnoreCase));
        if (role is not null) return role.Email.Trim();
        if (!whitelist.Enabled) return null;
        return whitelist.Emails.FirstOrDefault(e => string.Equals(e?.Trim(), address, StringComparison.OrdinalIgnoreCase))?.Trim();
    }

    /// <summary>Whether an explicit Admin exists — the same truth as the setup state ("a configured instance").</summary>
    public static bool HasExplicitAdmin(UserRoleData roles) => roles.Roles.Any(r => r.Role == AppRole.Admin);

    /// <summary>The fingerprint people see instead of a subject: the first 12 hex digits of SHA-256 over
    /// <c>issuer \n subject</c>. Display only, never compared for authority.</summary>
    public static string Fingerprint(string issuer, string subject) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\n" + subject)))[..12];
}

/// <summary>The file <c>identity-bindings.json</c> (contract 0003, data model).</summary>
public sealed class IdentityBindingFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("bindings")] public List<IdentityBindingRecord> Bindings { get; set; } = [];
    [JsonPropertyName("pending")] public List<PendingIdentityRecord> Pending { get; set; } = [];
}

public sealed class IdentityBindingRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("issuer")] public string Issuer { get; set; } = "";
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("boundUtc")] public DateTime BoundUtc { get; set; }
    [JsonPropertyName("boundBy")] public string BoundBy { get; set; } = "";
    [JsonPropertyName("lastSignInDay")] public string? LastSignInDay { get; set; }
}

public sealed class PendingIdentityRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("issuer")] public string Issuer { get; set; } = "";
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("emailVerifiedAsserted")] public bool EmailVerifiedAsserted { get; set; }
    [JsonPropertyName("requestedUtc")] public DateTime RequestedUtc { get; set; }
    [JsonPropertyName("expiresUtc")] public DateTime ExpiresUtc { get; set; }
}

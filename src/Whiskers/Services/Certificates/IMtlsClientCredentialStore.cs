using Whiskers.Services.Mutations;

namespace Whiskers.Services.Certificates;

/// <summary>
/// The per-host mTLS client credentials Whiskers keeps in its data directory (R1-44, contract 0007 with amendments
/// 1–8). <c>{MtlsDir}/hosts/{serverId}/</c> (0700) holds <c>identity</c> (the name stem plus the active, pending and
/// previous slot), <c>rollout.json</c> (the rollout journal) and one slot directory <c>g{n}-s{k}/</c> (0700) per issued
/// certificate with <c>client.key</c> (0600) and <c>client.crt</c>. The CA bundle is the existing
/// <c>{MtlsDir}/ca.crt</c>: no new trust anchor. The server record, not this store, is authoritative for the slot in use.
///
/// <para>A renewal or rotation never overwrites the live credential: the next certificate goes into a slot of its own, the
/// server record is switched to it (one atomic save), and the old slot is deleted only once it is no longer needed — after
/// the new connection is confirmed (renewal) or after the host refused the old credential (rotation).</para>
///
/// <para>The private key is generated in Whiskers' process and never leaves the data directory; only the CSR travels to
/// step-ca and only the signed certificate comes back.</para>
/// </summary>
public interface IMtlsClientCredentialStore
{
    /// <summary>The shared credential of the fleet before R1-44: <c>{MtlsDir}/client.crt</c> / <c>client.key</c>.</summary>
    MtlsClientCredentialPaths SharedPaths { get; }

    /// <summary>The fleet CA bundle <c>{MtlsDir}/ca.crt</c> every per-host credential is checked against.</summary>
    string CaPath { get; }

    /// <summary>The paths the next issue for <paramref name="serverId"/> and <paramref name="generation"/> would write
    /// (a new slot; nothing is created). Used for onboarding, renewal and rotation. There is deliberately no "active
    /// paths" lookup: connections always use the server record's paths, which are authoritative (amendment 6). Throws for
    /// a server id that cannot be a directory name.</summary>
    MtlsClientCredentialPaths ProposeNewSlot(string serverId, int generation);

    /// <summary>True when the server's client certificate and key are this store's files of one slot of exactly that
    /// server (canonical full paths).</summary>
    bool IsManaged(Models.ServerConfig server);

    /// <summary>True when the server presents the shared credential (canonical full paths of certificate and key).</summary>
    bool IsShared(Models.ServerConfig server);

    /// <summary>The active identity of <paramref name="serverId"/>, or null before the first issue.</summary>
    MtlsClientIdentity? IdentityOf(string serverId);

    /// <summary>The identity of the pending (issued, not yet active) certificate, or null.</summary>
    MtlsClientIdentity? PendingIdentityOf(string serverId);

    /// <summary>The pending credential, or null.</summary>
    MtlsClientCredentialPaths? PendingFor(string serverId);

    /// <summary>The credential the last activation replaced, kept until <see cref="DiscardPreviousAsync"/>, or null.</summary>
    MtlsClientCredentialPaths? PreviousFor(string serverId);

    /// <summary>Generates a key in-process, has step-ca sign a CSR for the server's identity of
    /// <paramref name="generation"/>, validates the result and writes it into a new slot that becomes the active one
    /// (first issue, or a host whose record does not use the store yet). The slug is fixed at the first issue.</summary>
    [MutatingOperation("certificate.issue_mtls")]
    Task<MtlsClientCredentialPaths> IssueAsync(Models.ServerConfig server, int generation, CancellationToken ct);

    /// <summary>As <see cref="IssueAsync"/>, but the new slot becomes the pending one; the active credential is untouched.</summary>
    [MutatingOperation("certificate.issue_mtls")]
    Task<MtlsClientCredentialPaths> IssuePendingAsync(Models.ServerConfig server, int generation, CancellationToken ct);

    /// <summary>Records the pending slot as the active one and the replaced slot as the previous one (files untouched).</summary>
    [MutatingOperation("mtls.client_renew")]
    Task ActivatePendingAsync(string serverId, CancellationToken ct);

    /// <summary>Deletes the pending slot (a switch that did not work).</summary>
    [MutatingOperation("mtls.client_renew")]
    Task DiscardPendingAsync(string serverId, CancellationToken ct);

    /// <summary>Deletes the previous slot once nothing needs it.</summary>
    [MutatingOperation("mtls.client_renew")]
    Task DiscardPreviousAsync(string serverId, CancellationToken ct);

    /// <summary>Removes a server's directory when the server is removed. The parameter is deliberately not named
    /// <c>serverId</c>: the record is already gone when this runs, so it could not be bound to a registered server.</summary>
    [MutatingOperation("server.remove")]
    Task RemoveAsync(string hostId, CancellationToken ct);

    /// <summary>The rollout journal of <paramref name="serverId"/>, or null.</summary>
    MtlsRolloutJournal? ReadJournal(string serverId);

    /// <summary>Writes the rollout journal (Whiskers' own record of the phase a host has reached).</summary>
    Task WriteJournalAsync(string serverId, MtlsRolloutJournal journal, CancellationToken ct);

    /// <summary>What the server page shows about the server's client credential.</summary>
    MtlsClientCredentialStatus StatusOf(Models.ServerConfig server);

    /// <summary>Every client key file the store knows (the shared one and each slot's), for the readiness checks.</summary>
    IReadOnlyList<string> KeyFiles();
}

/// <summary>Where a client credential lives. <see cref="Ca"/> is always the fleet CA bundle <c>{MtlsDir}/ca.crt</c>.</summary>
public sealed record MtlsClientCredentialPaths(string Cert, string Key, string Ca, int Generation);

/// <summary>The rollout journal of one host (<c>rollout.json</c>). <see cref="Phase"/> is the last phase that was
/// confirmed: 1 issued, 2 widened, 3 switched, 4 narrowed, 5 recorded. <see cref="NegativeCheckPassed"/> says the
/// previous credential was refused by the host (a TLS alert right after the new one was admitted).
/// <see cref="SharedCredentialAbsent"/>: there was no shared credential to try (fresh install or retired); the running
/// allow-list read from <c>docker inspect</c> (only the host's own name) is the proof instead (amendment 3, M-9).
/// <see cref="IsNarrowed"/> is what retiring and the readiness accept.</summary>
public sealed record MtlsRolloutJournal(
    string Kind,
    string CommonName,
    int Generation,
    int Phase,
    string? PreviousCommonName,
    bool NegativeCheckPassed,
    DateTime UpdatedUtc,
    bool SharedCredentialAbsent = false)
{
    public const string Migrate = "migrate";
    public const string Rotate = "rotate";
    public const string Onboard = "onboard";

    /// <summary>Phase 5 recorded with the shared (or previous) credential proven refused, or with no shared credential
    /// to try.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNarrowed => Phase >= 5 && (NegativeCheckPassed || SharedCredentialAbsent);
}

/// <summary>Which kind of client credential a server uses.</summary>
public enum MtlsClientCredentialKind
{
    /// <summary>Not an mTLS server, or no client certificate configured.</summary>
    None,
    /// <summary>The fleet-wide shared credential (before migration).</summary>
    Shared,
    /// <summary>Its own credential from this store.</summary>
    Own,
    /// <summary>Paths an operator set by hand: neither the shared <c>{MtlsDir}/client.*</c> nor the managed
    /// <c>{MtlsDir}/hosts/{id}/*</c>. Whiskers never touches them.</summary>
    OperatorSupplied,
}

public sealed record MtlsClientCredentialStatus(
    MtlsClientCredentialKind Kind,
    int Generation,
    DateTime? NotAfterUtc,
    int Phase,
    string? CommonName);

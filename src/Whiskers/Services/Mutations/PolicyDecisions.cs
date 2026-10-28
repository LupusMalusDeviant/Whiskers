using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Persistence;
using Whiskers.Utils;

namespace Whiskers.Services.Mutations;

/// <summary>
/// A policy exactly as it was evaluated (R1-12): the preset that governed the call and the content hash that names
/// this version. The policy is a private copy; nothing that holds a snapshot can change it.
/// </summary>
public sealed class PolicySnapshot
{
    private PolicySnapshot(string preset, string json, string versionHash)
    {
        Preset = preset;
        Json = json;
        VersionHash = versionHash;
    }

    /// <summary>The preset name: the active one, a trigger's pinned one, or <see cref="FailClosedPreset"/>.</summary>
    public string Preset { get; }

    /// <summary>The policy in canonical JSON (keys sorted at every level, no whitespace).</summary>
    public string Json { get; }

    /// <summary>SHA-256 of <see cref="Json"/>: equal content is the same version, any change is a new one.</summary>
    public string VersionHash { get; }

    /// <summary>The preset a fail-closed store reports (unreadable <c>guardrails.json</c>).</summary>
    public const string FailClosedPreset = Guardrails.GuardrailStore.FailClosedPreset;

    public static PolicySnapshot Of(string preset, GuardrailPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var json = PolicyCanonicalJson.Of(JsonSerializer.SerializeToElement(policy));
        return new PolicySnapshot(string.IsNullOrWhiteSpace(preset) ? "(unnamed)" : preset, json, PolicyCanonicalJson.Hash(json));
    }
}

/// <summary>The canonical form decisions are hashed in: object keys sorted ordinally at every level, no whitespace.
/// Two inputs that differ only in key order or formatting hash the same; any other difference does not.</summary>
public static class PolicyCanonicalJson
{
    public static string Of(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The normalized input hash of a call's arguments (INV-22).</summary>
    public static string InputHash(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
                else Write(writer, value);
            }
            writer.WriteEndObject();
        }
        return Hash(Encoding.UTF8.GetString(stream.ToArray()));
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>Which policy governs a call: the live active preset, or a run's pinned preset combined most
/// restrictively with the global kill switch (INV-23).</summary>
public static class EffectivePolicy
{
    public static (string Preset, GuardrailPolicy Policy) Of(PinnedPolicy? pinned, Guardrails.IGuardrailStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        // A fail-closed store refuses everything, pinned runs included: its state is not the operator's choice.
        var active = store.Active;
        if (store.LoadError is not null) return (PolicySnapshot.FailClosedPreset, GuardrailPolicy.FailClosed());
        if (pinned is null) return active;
        var policy = pinned.Policy.Clone();
        // The kill switch is global: switched on, it stops a pinned run too; a pinned preset can never switch it off.
        if (active.Policy.ReadOnlyMode) policy.ReadOnlyMode = true;
        return (pinned.Preset, policy);
    }
}

/// <summary>What the gateway records about one decision before anything runs.</summary>
public sealed record PolicyDecisionRecord(
    string Id,
    DateTime Timestamp,
    string OperationId,
    MutationRequest Request,
    Capability Capability,
    MutationPolicyDecision Decision,
    PolicySnapshot Policy);

/// <summary>
/// Persists policy decisions (INV-22). <see cref="RecordAsync"/> throws when the record cannot be written; the
/// gateway then refuses the mutation — no record, no mutation.
/// </summary>
public interface IPolicyDecisionStore
{
    Task RecordAsync(PolicyDecisionRecord record, CancellationToken ct);
}

/// <summary>Writes decisions and the policy versions they name to the database, in one transaction. A version that
/// is stored already is left as it is.</summary>
public sealed class DbPolicyDecisionStore : IPolicyDecisionStore
{
    private const int MaxReasonChars = 2000;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    public DbPolicyDecisionStore(IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _time = time ?? TimeProvider.System;
    }

    public async Task RecordAsync(PolicyDecisionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var request = record.Request;
        var target = request.Target;
        var reason = AuditLogMutationSink.WithoutPayloadValues(record.Decision.Reason, request.Arguments);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        if (!await db.PolicySnapshots.AsNoTracking().AnyAsync(s => s.VersionHash == record.Policy.VersionHash, ct))
            db.PolicySnapshots.Add(new PolicySnapshotEntity
            {
                VersionHash = record.Policy.VersionHash,
                Preset = record.Policy.Preset,
                PolicyJson = record.Policy.Json,
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
        db.PolicyDecisions.Add(new PolicyDecisionEntity
        {
            Id = record.Id,
            Timestamp = record.Timestamp,
            OperationId = record.OperationId,
            PrincipalId = request.Principal.Id,
            PrincipalKind = request.Principal.Kind.ToString(),
            OnBehalfOfId = request.Principal.OnBehalfOf?.Id,
            CredentialId = request.Principal.CredentialId,
            Surface = request.Surface.ToString(),
            CapabilityId = record.Capability.Id,
            RequiredLevel = record.Capability.RequiredLevel,
            TargetKind = target.Kind,
            TargetId = WithoutUrlCredentials(target.Id)!,
            TargetName = WithoutUrlCredentials(target.Name),
            TargetServerId = target.ServerId,
            // A related resource can be a repository URL with credentials in it (a Git app): never stored as such.
            TargetRelated = target.Related.Count == 0 ? null : JsonSerializer.Serialize(target.Related.Select(WithoutUrlCredentials)),
            InputHash = PolicyCanonicalJson.InputHash(request.Arguments),
            PolicyVersion = record.Policy.VersionHash,
            Preset = record.Policy.Preset,
            MatchedRules = JsonSerializer.Serialize(record.Decision.MatchedRules),
            Verdict = record.Decision.Verdict.ToString(),
            // A rule's reason can quote a resolved value, such as a repository URL with a token in it.
            Reason = reason is null ? null : Cap(SecretRedactor.Redact(WithoutUrlCredentials(reason)!)),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (db.ChangeTracker.Entries<PolicySnapshotEntity>().Any(e => e.State == EntityState.Added))
        {
            // Possibly another decision stored the same version meanwhile. Only then is the decision stored alone —
            // checked, not assumed: any other failure leaves the version unstored and is a failure (no record, no
            // mutation), never a decision that names a version nobody can read.
            if (!await SnapshotStoredAsync(record.Policy, ct)) throw;
            foreach (var entry in db.ChangeTracker.Entries<PolicySnapshotEntity>().ToList()) entry.State = EntityState.Detached;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Whether exactly this version is stored (same hash and the same policy), read in a fresh context.</summary>
    private async Task<bool> SnapshotStoredAsync(PolicySnapshot snapshot, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var stored = await db.PolicySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.VersionHash == snapshot.VersionHash, ct);
        return stored is not null && stored.PolicyJson == snapshot.Json;
    }

    private static string Cap(string s) => s.Length > MaxReasonChars ? s[..MaxReasonChars] + "…" : s;

    /// <summary>The text with the user info of every URL in it replaced by <c>***</c> — and nothing else changed, so a
    /// resource identity (<c>db-prod</c>) stays exactly what it is. The general redactor is for free text only: its
    /// command patterns would rewrite ordinary names.</summary>
    internal static string? WithoutUrlCredentials(string? value) => PolicyDecisions.WithoutUrlCredentials(value);
}

/// <summary>What decisions and audit rows may hold of a value that came from outside.</summary>
internal static class PolicyDecisions
{
    // scheme://anything@host → scheme://***@host: a user name alone can be a token (https://<token>@github.com).
    private static readonly System.Text.RegularExpressions.Regex UrlUserInfo =
        new(@"(?i)\b([a-z][a-z0-9+.-]*://)[^\s/@]+@", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The text with the user info of every URL in it replaced by <c>***</c>, nothing else changed.</summary>
    public static string? WithoutUrlCredentials(string? value) =>
        value is null ? null : UrlUserInfo.Replace(value, "$1***@");
}

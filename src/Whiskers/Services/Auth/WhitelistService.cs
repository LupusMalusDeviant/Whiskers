using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

public class WhitelistService : IWhitelistService, IInitializable
{
    private readonly JsonFileStore<WhitelistData> _store;
    private readonly IConfiguration _configuration;
    private readonly IRoleService _roles;
    private readonly ILogger<WhitelistService> _logger;
    private WhitelistData _cached = new();
    private readonly ReaderWriterLockSlim _lock = new();
    // R1-22: one writer at a time from capture to diff, so a save's recorded change is the change it made. R1-48 review
    // round 2: the boundary is shared with the roles and the setup wizard (AccessControlWriteGate).
    private readonly AccessControlWriteGate _gate;

    // R1-27: the identity bindings of addresses that stop being listed are pruned before the write.
    private readonly IIdentityBindingStore? _bindings;

    public WhitelistService(IConfiguration configuration, IRoleService roleService, ILogger<WhitelistService> logger, DataPathOptions? dataPaths = null,
        AccessControlWriteGate? writeGate = null, IIdentityBindingStore? bindings = null)
    {
        // An address removed by the last save must not come back from the previous generation (1.0 review).
        _store = new JsonFileStore<WhitelistData>((dataPaths ?? DataPathOptions.Default).WhitelistJson,
            restorePreviousGeneration: false);
        _configuration = configuration;
        _roles = roleService;
        _logger = logger;
        _gate = writeGate ?? new AccessControlWriteGate();
        _bindings = bindings;
    }

    /// <summary>The addresses a whitelist makes entries: its list while it is enabled, none otherwise.</summary>
    private static IEnumerable<string> Listed(WhitelistData data) =>
        data.Enabled ? data.Emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()) : [];

    public int Order => 10;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_store.Exists())
        {
            var data = await _store.LoadAsync();
            SetCache(data);
            _logger.LogInformation("Loaded whitelist: {Count} emails, enabled={Enabled}", data.Emails.Count, data.Enabled);
        }
        else
        {
            // Seed from config if AllowedEmails are set. Blank entries are dropped: compose maps an unset
            // GOOGLE_ADMIN_EMAIL to `AllowedEmails__0=`, and an enabled list holding only "" admits nobody — not the
            // OIDC user, not the local admin the setup wizard creates.
            var configEmails = (_configuration.GetSection("GoogleAuth:AllowedEmails").Get<List<string>>() ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (configEmails.Count > 0)
            {
                // Contract 0017 H1: the local Admin that WHISKERS_ADMIN_EMAIL seeds (RoleService, LocalAdminSeeder) is
                // listed too, or the enabled list would refuse its sign-in. WHISKERS_ADMIN_EMAIL alone seeds no whitelist.
                var localAdmin = _configuration["WHISKERS_ADMIN_EMAIL"]?.Trim();
                if (!string.IsNullOrEmpty(localAdmin) && !configEmails.Contains(localAdmin, StringComparer.OrdinalIgnoreCase))
                    configEmails.Add(localAdmin);
                var data = new WhitelistData { Enabled = true, Emails = configEmails };
                await _store.SaveAsync(data);
                SetCache(data);
                _logger.LogInformation("Seeded whitelist from config: {Count} emails", configEmails.Count);
            }
            else
            {
                SetCache(new WhitelistData());
                _logger.LogInformation("No whitelist configured, all Google users allowed");
            }
        }
    }

    public bool IsEmailAllowed(string? email)
    {
        if (string.IsNullOrEmpty(email))
            return false;

        bool enabled, inList;
        int count;
        _lock.EnterReadLock();
        try
        {
            enabled = _cached.Enabled;
            count = _cached.Emails.Count;
            inList = enabled && _cached.Emails.Contains(email, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Enabled = enforce the list. Enabled-but-empty = deny all (an admin who cleared the last entry must
        // not silently open the instance to every Google account).
        if (enabled)
            return count > 0 && inList;

        // Whitelist disabled (never configured). C5: previously an unconditional fail-open ("allow everyone").
        // Now fail-open ONLY while the instance is otherwise unconfigured (no role entries) — preserving the
        // legacy behaviour for existing deployments that rely on it. As soon as ANY role exists (e.g. the C5
        // admin bootstrap seeded one), a disabled whitelist is fail-CLOSED: only users with an explicit role
        // are admitted, so a fresh instance with a configured admin no longer lets every Google/OIDC account in.
        return !_roles.HasAnyRoles() || _roles.HasExplicitRole(email);
    }

    public WhitelistData GetWhitelist()
    {
        _lock.EnterReadLock();
        try
        {
            return new WhitelistData
            {
                Enabled = _cached.Enabled,
                Emails = new List<string>(_cached.Emails),
                AddedForAccounts = CopyRecord(_cached.AddedForAccounts)
            };
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public async Task SaveWhitelistAsync(WhitelistData data)
    {
        // Deep-copy before persisting/caching so the enforcement snapshot never aliases a caller-owned
        // list (e.g. the Settings page's live edit buffer). Otherwise later, unsaved UI edits would take
        // effect immediately and a concurrent mutation could throw during the auth-path re-check.
        var copy = new WhitelistData
        {
            Enabled = data.Enabled,
            Emails = new List<string>(data.Emails)
        };
        using (var lease = await _gate.EnterAsync())
        {
            var before = GetWhitelist();
            // Contract 0017 H2/H3: the record of local-account entries is the service's own. What a caller passes is
            // ignored; an address that stays listed keeps its record, one this save removes loses it (a later re-add by
            // hand is then a manual entry that H3 leaves).
            copy.AddedForAccounts = KeepRecord(before.AddedForAccounts, copy.Emails);
            // R1-27 (contract 0003, rule 5): an address that stops being listed loses its identity bindings first, under
            // the held gate; a failed prune throws and the whitelist stays as it is.
            // A save that lists a new address prunes first too (contract 0003, amendment c): whatever the bindings hold of an
            // address that is no entry now goes before it becomes one again.
            if (_bindings is not null
                && (Listed(before).Except(Listed(copy), StringComparer.OrdinalIgnoreCase).Any()
                    || Listed(copy).Except(Listed(before), StringComparer.OrdinalIgnoreCase).Any()))
                await _bindings.PruneOrphansAsync(lease, nextWhitelist: copy);
            await _store.SaveAsync(copy);
            SetCache(copy);
            Whiskers.Services.Mutations.ConfigChange.Record("whitelist", before, copy);   // R1-22
        }
        _logger.LogInformation("Whitelist updated: {Count} emails, enabled={Enabled}", copy.Emails.Count, copy.Enabled);
    }

    public async Task AddFirstAdminAsync(string email, AccessControlWriteGate.Lease lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        // R1-48 review round 2: the caller holds the boundary every role and whitelist writer takes — no role writer
        // can establish an Admin after this check, and no whitelist save can run before the wizard's role write.
        _gate.Demand(lease);
        // R1-48: closed for good once an administrator exists — from then on only SaveWhitelistAsync, through the gateway.
        if (_roles.GetRoleData().Roles.Any(r => r.Role == AppRole.Admin))
            throw new InvalidOperationException("An administrator already exists; the first-admin write is closed.");
        var current = GetWhitelist();
        if (current.Emails.Contains(email, StringComparer.OrdinalIgnoreCase)) return;
        // Only the address is added; Enabled stays as it is (enabling it would lock out unlisted OIDC users).
        var next = new WhitelistData { Enabled = current.Enabled, Emails = [.. current.Emails, email], AddedForAccounts = current.AddedForAccounts };
        // R1-27 (contract 0003, amendment c): an addition prunes first, under the held gate; a failed prune refuses it.
        if (_bindings is not null) await _bindings.PruneOrphansAsync(lease, nextWhitelist: next);
        await _store.SaveAsync(next);   // persisted first, published after
        _gate.Demand(lease);
        SetCache(next);
        _logger.LogInformation("Setup wizard added the first administrator to the whitelist");
    }

    public async Task<bool> AddLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        email = email.Trim();
        using var lease = await _gate.EnterAsync(ct);
        var before = GetWhitelist();
        // A disabled whitelist is not touched (byte-identical); a listed address stays a manual entry without a record.
        if (!before.Enabled || before.Emails.Any(e => SameAddress(e, email))) return false;
        var record = CopyRecord(before.AddedForAccounts) ?? new Dictionary<string, string>();
        RemoveRecord(record, email);
        record[email] = accountId;
        var next = new WhitelistData { Enabled = true, Emails = [.. before.Emails, email], AddedForAccounts = record };
        RequireDecision("account.create");
        await WriteAsync(lease, before, next, ct);
        _logger.LogInformation("A local account an Admin created was added to the enabled whitelist");
        return true;
    }

    public async Task<bool> RemoveLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default, WhitelistWriteReceipt? receipt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        email = email.Trim();
        using var lease = await _gate.EnterAsync(ct);
        var before = GetWhitelist();
        // Only the entry this account's creation added: no record (listed before, or removed / re-added by hand since)
        // or another account's record leaves the list as it is.
        if (!string.Equals(RecordOf(before.AddedForAccounts, email), accountId, StringComparison.Ordinal)) return false;
        var record = CopyRecord(before.AddedForAccounts)!;
        RemoveRecord(record, email);
        var next = new WhitelistData
        {
            Enabled = before.Enabled,
            Emails = before.Emails.Where(e => !SameAddress(e, email)).ToList(),
            AddedForAccounts = record.Count == 0 ? null : record
        };
        RequireDecision("account.delete", "account.create");   // account.create: the undo of a failed creation
        await WriteAsync(lease, before, next, ct, receipt);
        _logger.LogInformation("The whitelist entry of a deleted local account was removed");
        return true;
    }

    public async Task RestoreLocalAccountEntryAsync(string email, string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        email = email.Trim();
        using var lease = await _gate.EnterAsync(ct);
        var before = GetWhitelist();
        // Listed again by hand in between: that entry stays a manual one.
        if (before.Emails.Any(e => SameAddress(e, email))) return;
        var record = CopyRecord(before.AddedForAccounts) ?? new Dictionary<string, string>();
        RemoveRecord(record, email);
        record[email] = accountId;
        var next = new WhitelistData { Enabled = before.Enabled, Emails = [.. before.Emails, email], AddedForAccounts = record };
        RequireDecision("account.delete");
        await WriteAsync(lease, before, next, ct);
        _logger.LogInformation("The whitelist entry of a local account whose deletion failed was put back");
    }

    /// <summary>One write of the local-account entries, under the held gate: bindings pruned first when the listed
    /// addresses change (contract 0003, rule 5 and amendment c; a failed prune throws, nothing written), persisted, then
    /// published and recorded in the caller's gateway operation (R1-22).</summary>
    private async Task WriteAsync(AccessControlWriteGate.Lease lease, WhitelistData before, WhitelistData next, CancellationToken ct,
        WhitelistWriteReceipt? receipt = null)
    {
        _gate.Demand(lease);
        if (_bindings is not null
            && (Listed(before).Except(Listed(next), StringComparer.OrdinalIgnoreCase).Any()
                || Listed(next).Except(Listed(before), StringComparer.OrdinalIgnoreCase).Any()))
            await _bindings.PruneOrphansAsync(lease, nextWhitelist: next, ct: ct);
        await _store.SaveAsync(next);
        // Persisted: whatever fails after this, the caller knows the write happened (contract 0017 H3 undo).
        if (receipt is not null) receipt.Persisted = true;
        SetCache(next);
        Whiskers.Services.Mutations.ConfigChange.Record("whitelist", before, next);
    }

    /// <summary>INV-46: the local-account entry writes are part of a decided account operation, whose event records
    /// them. Checked under the gate when a write is due (a call that changes nothing needs no decision; a decision
    /// that closed while the call waited counts as none): outside such a decision nothing is written (fail closed).</summary>
    private static void RequireDecision(params string[] capabilityIds)
    {
        if (!capabilityIds.Any(Whiskers.Services.Mutations.MutationScope.Authorizes))
            throw new InvalidOperationException(
                $"A local-account whitelist write runs only inside a decided {string.Join(" or ", capabilityIds)}; nothing was written.");
    }

    private static bool SameAddress(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? RecordOf(Dictionary<string, string>? record, string email) =>
        record?.FirstOrDefault(r => SameAddress(r.Key, email)).Value;

    private static void RemoveRecord(Dictionary<string, string> record, string email)
    {
        foreach (var key in record.Keys.Where(k => SameAddress(k, email)).ToList()) record.Remove(key);
    }

    private static Dictionary<string, string>? CopyRecord(Dictionary<string, string>? record) =>
        record is null ? null : new Dictionary<string, string>(record);

    /// <summary>The record of the addresses in <paramref name="emails"/>; <c>null</c> when none is left.</summary>
    private static Dictionary<string, string>? KeepRecord(Dictionary<string, string>? record, IEnumerable<string> emails)
    {
        if (record is null) return null;
        var listed = emails.ToList();
        var kept = record.Where(r => listed.Any(e => SameAddress(e, r.Key))).ToDictionary(r => r.Key, r => r.Value);
        return kept.Count == 0 ? null : kept;
    }

    private void SetCache(WhitelistData data)
    {
        _lock.EnterWriteLock();
        try
        {
            _cached = data;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}

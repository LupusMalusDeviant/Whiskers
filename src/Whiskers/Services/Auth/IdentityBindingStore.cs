using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>
/// R1-27: <see cref="IIdentityBindingStore"/> on <c>identity-bindings.json</c>.
/// <list type="bullet">
/// <item><b>The file is the authority.</b> The live instance and the host CLI (a second process) share it. Every write
/// holds the <see cref="AccessControlWriteGate"/> (in the process) and an exclusive lock on
/// <c>identity-bindings.json.lock</c> (across processes), re-reads the file under both, applies its change to what it
/// read, persists (<see cref="DurableFile"/>: flushed, one rename, the previous generation kept as <c>.bak</c>) and only
/// then publishes. A reader re-reads the file whenever it changed (time and length; always within two seconds of a
/// write, below the file system's time resolution), so a confirmation by the CLI reaches the running instance without a
/// restart, and neither writer overwrites the other's change.</item>
/// <item><b>No automatic rollback.</b> A current file that is missing while its previous generation exists, cannot be
/// parsed, is incomplete (version, <c>bindings</c> or <c>pending</c> absent or <c>null</c>) or is a directory makes the
/// store unavailable; the <c>.bak</c> is never taken over by itself (it may hold a revoked binding). Only a store never
/// written (neither file) is empty. Recovery is the host operator's explicit act.</item>
/// <item><b>Unavailable until pruned.</b> A failed save, or a start-up prune that could not be saved, makes the store
/// unavailable — every federated admission and session refuses, readiness is Degraded — until a re-read and prune
/// succeed (the next sign-in attempt, the next write, or the retry every 30 s). The records a failed prune had to
/// remove are removed by the next write whatever the entries are then, so an address configured again meanwhile never
/// revives the old subject.</item>
/// </list>
/// <para>The roles and the whitelist are read through the service provider when they are needed: the role and whitelist
/// writers call <see cref="PruneOrphansAsync"/> before their own writes, so they depend on this store and not the other
/// way round. <see cref="IInitializable"/> Order 15; the start-up prune runs later (<see cref="IdentityBindingStartupPrune"/>,
/// Order 26), once the roles are loaded.</para>
/// </summary>
public sealed class IdentityBindingStore : IIdentityBindingStore, IInitializable
{
    /// <summary>How long a binding request waits for confirmation.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);

    /// <summary>At most this many binding requests are kept; the oldest go first.</summary>
    public const int MaxPending = 64;

    /// <summary>The actor of the host CLI's audit row.</summary>
    public const string CliActor = "cli (--confirm-identity-binding)";

    /// <summary>The suffix of the file whose exclusive lock serialises the writers of every process.</summary>
    public const string LockSuffix = ".lock";

    /// <summary>How long a writer waits for the other process's lock before it refuses (fail closed).</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Within this time of a write the file is re-read on every read (time stamps may be coarser).</summary>
    private static readonly TimeSpan RecentWrite = TimeSpan.FromSeconds(2);

    /// <summary>How often an unavailable store retries its re-read and prune.</summary>
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _path;
    private readonly IServiceProvider _services;
    private readonly IAuditLogService _audit;
    private readonly IConfiguration _configuration;
    private readonly AccessControlWriteGate _gate;
    private readonly TimeProvider _time;
    private readonly ILogger<IdentityBindingStore>? _logger;
    private readonly bool _authDisabled;

    // Published state, guarded by _state: what the file held when it was last read, and whether that read succeeded.
    private readonly object _state = new();
    private IdentityBindingFile _data = Empty();
    private FileStamp? _stamp;
    private bool _readOk;
    private bool _loggedUnreadable;
    // A save failed (or a start-up prune could not be saved): unavailable until a re-read and prune succeed.
    private volatile bool _degraded;
    // What a failed prune had to remove: removed by the next write whatever the entries are then.
    private readonly HashSet<string> _mustRemove = new(StringComparer.Ordinal);
    private int _recovering;

    public IdentityBindingStore(IServiceProvider services, IAuditLogService audit, IConfiguration configuration,
        AccessControlWriteGate? writeGate = null, DataPathOptions? dataPaths = null, TimeProvider? time = null,
        ILogger<IdentityBindingStore>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authDisabled = configuration.GetValue<bool>("Auth:Disabled");
        _gate = writeGate ?? new AccessControlWriteGate();
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _path = Path.GetFullPath((dataPaths ?? DataPathOptions.Default).IdentityBindingsJson);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>Tests only: replaces the save (one that fails on cue). Null in the app.</summary>
    internal Func<IdentityBindingFile, Task>? PersistForTests { get; set; }

    /// <summary>Tests only: runs before the sign-in binding enters the gate (a race made deterministic).</summary>
    internal Func<Task>? BeforeBindGateForTests { get; set; }

    /// <summary>Tests only: runs after the removal's last-Admin check, before it is saved.</summary>
    internal Func<Task>? BeforeRemoveCommitForTests { get; set; }

    public int Order => 15;

    public bool IsAvailable
    {
        get
        {
            Refresh();
            lock (_state) return _readOk && !_degraded;
        }
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        Refresh(force: true);
        lock (_state)
            if (_readOk)
                _logger?.LogInformation("Loaded {Bindings} identity binding(s) and {Pending} pending request(s)",
                    _data.Bindings.Count, _data.Pending.Count);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------ reads

    private IdentityBindingFile Current
    {
        get
        {
            Refresh();
            lock (_state)
                return _readOk && !_degraded
                    ? _data
                    : throw new IdentityBindingStoreUnavailableException("The identity bindings are unavailable.");
        }
    }

    public IdentityBindingInfo? Find(string issuer, string subject)
    {
        var data = Current;
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject)) return null;
        var b = data.Bindings.FirstOrDefault(x => x.Issuer == issuer && x.Subject == subject);
        return b is null ? null : Info(b);
    }

    public IReadOnlyList<IdentityBindingInfo> BindingsOf(string account)
    {
        var data = Current;
        return data.Bindings.Where(b => Same(b.Account, account)).Select(Info).ToList();
    }

    public IdentityBindingSnapshot Snapshot()
    {
        var data = Current;
        var now = Now;
        return new IdentityBindingSnapshot(data.Bindings.Select(Info).ToList(),
            data.Pending.Where(p => p.ExpiresUtc > now).OrderByDescending(p => p.RequestedUtc).Select(Info).ToList());
    }

    private static IdentityBindingInfo Info(IdentityBindingRecord b) => new(b.Id, b.Account, b.Provider, b.Issuer,
        AccessEntries.Fingerprint(b.Issuer, b.Subject), b.Method, b.BoundUtc, b.BoundBy, b.LastSignInDay);

    private static PendingIdentityInfo Info(PendingIdentityRecord p) => new(p.Id, p.Account, p.Provider, p.Issuer,
        AccessEntries.Fingerprint(p.Issuer, p.Subject), p.Reason, p.EmailVerifiedAsserted, p.RequestedUtc, p.ExpiresUtc);

    // ------------------------------------------------------------------------------------------------ recovery

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            return await RecoverAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("The identity bindings could not be re-read and pruned ({Error})", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Under the gate and the file lock: re-read, prune what is no longer an entry (and what a failed prune had
    /// to remove), save if anything went. True when the store is available afterwards.</summary>
    private async Task<bool> RecoverAsync(CancellationToken ct)
    {
        using var lease = await _gate.EnterAsync(ct);
        await using var fileLock = await LockAsync(ct);
        var (data, removed) = LoadForWrite();
        if (removed.Count > 0)
        {
            if (!await PersistAsync(data, lease))
            {
                lock (_state) _mustRemove.UnionWith(removed);
                return false;
            }
            _logger?.LogInformation("Pruned {Count} identity binding record(s) of addresses that are no longer configured", removed.Count);
        }
        else
        {
            lock (_state) _mustRemove.Clear();
            _degraded = false;
        }
        lock (_state) return _readOk && !_degraded;
    }

    // ------------------------------------------------------------------------------------------------ sign-in writes

    public async Task<IdentityBindingResult> BindAtSignInAsync(FederatedSignInClaims claims, string account,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (string.IsNullOrEmpty(claims.Issuer) || string.IsNullOrEmpty(claims.Subject) || string.IsNullOrWhiteSpace(account))
            return IdentityBindingResult.Refused(IdentityBindingErrors.Conflict);
        if (BeforeBindGateForTests is { } hook) await hook();

        using var lease = await _gate.EnterAsync(ct);
        await using var fileLock = await TryLockAsync(ct);
        if (fileLock is null) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        if (TryLoadForWrite() is not { } data) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);

        // Contract 0003, rule 1.7: everything the admission decided is decided again under the gate, on what is in force
        // now — a configured instance, an entry the whitelist still admits, a standard and verified address from a trusted
        // provider, an unbound subject and an account without a binding.
        var roles = Roles.GetRoleData();
        if (!AccessEntries.HasExplicitAdmin(roles)) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        if (AccessEntries.EntryOf(roles, Whitelist.GetWhitelist(), account) is not { } handle
            || !Whitelist.IsEmailAllowed(handle))
            return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        if (!claims.EmailIsStandardClaim || !IsTrue(claims.EmailVerified)
            || !FederatedAdmission.Trusts(_configuration, claims.Provider))
            return IdentityBindingResult.Refused(IdentityBindingErrors.Conflict);
        if (data.Bindings.Any(b => b.Issuer == claims.Issuer && b.Subject == claims.Subject)
            || data.Bindings.Any(b => Same(b.Account, handle)))
            return IdentityBindingResult.Refused(IdentityBindingErrors.Conflict);

        var binding = NewBinding(handle, claims.Provider, claims.Issuer, claims.Subject, IdentityBindingMethods.VerifiedEmail,
            "sign-in");
        // The record first: a binding the trail does not show is not written.
        if (!await _audit.TryLogAsync(handle, "web", AuthAuditLog.IdentityBoundAction, AuthAuditLog.Target, handle, handle,
                BoundDetails(binding), success: true))
        {
            _logger?.LogError("An identity binding was refused: its audit row could not be written");
            return IdentityBindingResult.Refused(IdentityBindingErrors.AuditUnavailable);
        }
        var next = Copy(data);
        next.Bindings.Add(binding);
        // A request of the same account and issuer is answered by this binding.
        next.Pending.RemoveAll(p => Same(p.Account, handle) && p.Issuer == binding.Issuer);
        if (!await PersistAsync(next, lease))
        {
            await _audit.TryLogAsync(handle, "web", AuthAuditLog.IdentityBoundAction, AuthAuditLog.Target, handle, handle,
                $"binding={binding.Id}; the binding could not be saved; nothing was changed", success: false);
            return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        }
        _logger?.LogInformation("Identity binding {Binding} was created at a verified {Provider} sign-in", binding.Id, binding.Provider);
        return IdentityBindingResult.Ok(binding.Id);
    }

    public async Task<IdentityBindingResult> RecordPendingAsync(FederatedSignInClaims claims, string account, string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (!PendingIdentityReasons.All.Contains(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (string.IsNullOrEmpty(claims.Issuer) || string.IsNullOrEmpty(claims.Subject))
            return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);

        using var lease = await _gate.EnterAsync(ct);
        await using var fileLock = await TryLockAsync(ct);
        if (fileLock is null) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        if (TryLoadForWrite() is not { } data) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        // Strangers leave no record: only an address that is a configured entry gets a request.
        if (EntryOf(account) is not { } handle) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        var now = Now;
        var request = new PendingIdentityRecord
        {
            Id = NewId("p_"), Account = handle, Provider = claims.Provider, Issuer = claims.Issuer, Subject = claims.Subject,
            Reason = reason, EmailVerifiedAsserted = IsTrue(claims.EmailVerified), RequestedUtc = now,
            ExpiresUtc = now + PendingLifetime,
        };
        var next = Copy(data);
        next.Pending.RemoveAll(p => p.ExpiresUtc <= now || (Same(p.Account, handle) && p.Issuer == request.Issuer));
        next.Pending.Add(request);
        if (next.Pending.Count > MaxPending)
            next.Pending = next.Pending.OrderByDescending(p => p.RequestedUtc).Take(MaxPending).ToList();
        if (!await PersistAsync(next, lease)) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);

        await _audit.TryLogAsync(handle, "web", AuthAuditLog.IdentityPendingAction, AuthAuditLog.Target, handle, handle,
            $"request={request.Id}; provider={request.Provider}; issuer={request.Issuer}; " +
            $"fingerprint={AccessEntries.Fingerprint(request.Issuer, request.Subject)}; reason={reason}; " +
            $"email_verified={(request.EmailVerifiedAsserted ? "true" : "false")}", success: false);
        _logger?.LogInformation("A {Provider} sign-in waits for confirmation ({Reason}, request {Request})",
            request.Provider, reason, request.Id);
        return IdentityBindingResult.Ok();
    }

    public async Task<IdentityBindingResult> RecordSignInAsync(string bindingId, CancellationToken ct = default)
    {
        var today = Today;
        IdentityBindingFile current;
        try { current = Current; }
        catch (IdentityBindingStoreUnavailableException) { return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable); }
        if (current.Bindings.FirstOrDefault(b => b.Id == bindingId) is { } seen && seen.LastSignInDay == today)
            return IdentityBindingResult.Ok(bindingId);

        using var lease = await _gate.EnterAsync(ct);
        await using var fileLock = await TryLockAsync(ct);
        if (fileLock is null) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        if (TryLoadForWrite() is not { } data) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        var next = Copy(data);
        var binding = next.Bindings.FirstOrDefault(b => b.Id == bindingId);
        if (binding is null) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        if (binding.LastSignInDay == today) return IdentityBindingResult.Ok(bindingId);
        binding.LastSignInDay = today;
        return await PersistAsync(next, lease)
            ? IdentityBindingResult.Ok(bindingId)
            : IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
    }

    // ------------------------------------------------------------------------------------------------ admin writes

    public async Task<IdentityBindingResult> ConfirmAsync(string pendingId, CancellationToken ct = default)
    {
        if (Authority() is { } refused) return IdentityBindingResult.Refused(refused);
        using var lease = await _gate.EnterAsync(ct);
        await using var fileLock = await TryLockAsync(ct);
        if (fileLock is null) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        if (TryLoadForWrite() is not { } data) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        var request = data.Pending.FirstOrDefault(p => p.Id == pendingId && p.ExpiresUtc > Now);
        if (request is null) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        var by = MutationScope.Current?.Principal?.Id ?? "user:unknown";
        return await BindPendingAsync(data, request, IdentityBindingMethods.Admin, by, lease, auditFirst: null);
    }

    public async Task<IdentityBindingResult> RemoveAsync(string id, CancellationToken ct = default)
    {
        if (Authority() is { } refused) return IdentityBindingResult.Refused(refused);
        // The one boundary of every removal of an Admin's way to sign in (with the local-account deletion): the
        // last-Admin check and the removal are one step for both (contract 0003, rule 6).
        await AdminSignIn.Gate.WaitAsync(ct);
        try
        {
            using var lease = await _gate.EnterAsync(ct);
            await using var fileLock = await TryLockAsync(ct);
            if (fileLock is null) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
            if (TryLoadForWrite() is not { } data) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
            var next = Copy(data);
            if (next.Pending.RemoveAll(p => p.Id == id) > 0)
                return await PersistAsync(next, lease) ? IdentityBindingResult.Ok() : IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);

            var binding = next.Bindings.FirstOrDefault(b => b.Id == id);
            if (binding is null) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
            next.Bindings.Remove(binding);
            // The last way the last Admin who can sign in has stays (contract 0003, rule 6).
            var roles = Roles.GetRoleData();
            if (roles.Roles.Any(r => r.Role == AppRole.Admin && Same(r.Email, binding.Account))
                && !await AnyAdminCanSignInAsync(roles, next, ct))
                return IdentityBindingResult.Refused(IdentityBindingErrors.LastAdmin);
            if (BeforeRemoveCommitForTests is { } hook) await hook();
            if (!await PersistAsync(next, lease)) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
            _logger?.LogInformation("Identity binding {Binding} was removed", binding.Id);
            return IdentityBindingResult.Ok(binding.Id);
        }
        finally
        {
            AdminSignIn.Gate.Release();
        }
    }

    public async Task<IdentityBindingResult> ConfirmFromHostAsync(string account, string? issuer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account)) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        using var lease = await _gate.EnterAsync(ct);
        await using var fileLock = await TryLockAsync(ct);
        if (fileLock is null) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        if (TryLoadForWrite() is not { } data) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        var now = Now;
        var waiting = data.Pending
            .Where(p => Same(p.Account, account.Trim()) && p.ExpiresUtc > now
                        && (string.IsNullOrEmpty(issuer) || p.Issuer == issuer))
            .OrderByDescending(p => p.RequestedUtc)
            .ToList();
        if (waiting.Count == 0) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        if (waiting.Select(p => p.Issuer).Distinct(StringComparer.Ordinal).Count() > 1)
            return new IdentityBindingResult(false, IdentityBindingErrors.Ambiguous) { Candidates = waiting.Select(Info).ToList() };

        return await BindPendingAsync(data, waiting[0], IdentityBindingMethods.HostCli, "cli", lease, auditFirst: CliActor);
    }

    /// <summary>Turns <paramref name="request"/> into a binding under the held gate and file lock, on
    /// <paramref name="data"/> as just read. With <paramref name="auditFirst"/> (the CLI) the <c>auth.identity_bound</c>
    /// row is written first by that actor; no row, no change.</summary>
    private async Task<IdentityBindingResult> BindPendingAsync(IdentityBindingFile data, PendingIdentityRecord request,
        string method, string boundBy, AccessControlWriteGate.Lease lease, string? auditFirst)
    {
        if (EntryOf(request.Account) is not { } handle) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        var holder = data.Bindings.FirstOrDefault(b => b.Issuer == request.Issuer && b.Subject == request.Subject);
        if (holder is not null && !Same(holder.Account, handle)) return IdentityBindingResult.Refused(IdentityBindingErrors.Conflict);

        var next = Copy(data);
        next.Pending.RemoveAll(p => p.Id == request.Id);
        string bindingId;
        if (holder is not null)
        {
            bindingId = holder.Id;   // already bound to this account: only the request goes
        }
        else
        {
            var binding = NewBinding(handle, request.Provider, request.Issuer, request.Subject, method, boundBy);
            bindingId = binding.Id;
            if (auditFirst is not null && !await _audit.TryLogAsync(auditFirst, "system", AuthAuditLog.IdentityBoundAction,
                    AuthAuditLog.Target, handle, handle, BoundDetails(binding), success: true))
            {
                _logger?.LogError("A host confirmation of an identity binding was refused: its audit row could not be written");
                return IdentityBindingResult.Refused(IdentityBindingErrors.AuditUnavailable);
            }
            // One binding per account and issuer: a confirmed new subject replaces the old one.
            next.Bindings.RemoveAll(b => Same(b.Account, handle) && b.Issuer == request.Issuer);
            next.Bindings.Add(binding);
        }
        if (!await PersistAsync(next, lease))
        {
            if (auditFirst is not null)
                await _audit.TryLogAsync(auditFirst, "system", AuthAuditLog.IdentityBoundAction, AuthAuditLog.Target, handle,
                    handle, $"binding={bindingId}; the binding could not be saved; nothing was changed", success: false);
            return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        }
        _logger?.LogInformation("Identity binding {Binding} was confirmed ({Method})", bindingId, method);
        return IdentityBindingResult.Ok(bindingId);
    }

    public async Task<int> PruneOrphansAsync(AccessControlWriteGate.Lease lease, UserRoleData? nextRoles = null,
        WhitelistData? nextWhitelist = null, CancellationToken ct = default)
    {
        _gate.Demand(lease);
        await using var fileLock = await LockAsync(ct);
        // What is no longer an entry now goes as well as what will not be one after the caller's write: a record whose
        // address is configured again by this very write must not survive from before.
        var (data, orphansNow) = LoadForWrite();
        var roles = nextRoles ?? Roles.GetRoleData();
        var whitelist = nextWhitelist ?? Whitelist.GetWhitelist();
        bool Orphan(string account) => AccessEntries.EntryOf(roles, whitelist, account) is null;
        var removed = orphansNow.Count + data.Bindings.RemoveAll(b => Orphan(b.Account)) + data.Pending.RemoveAll(p => Orphan(p.Account));
        if (removed == 0) return 0;
        if (!await PersistAsync(data, lease))
        {
            // What is no longer an entry now must go with the next write that succeeds, whatever the entries are then;
            // what only the refused write would have removed stays (that write does not happen).
            lock (_state) _mustRemove.UnionWith(orphansNow);
            throw new IdentityBindingStoreUnavailableException("The identity bindings of removed addresses could not be pruned.");
        }
        _logger?.LogInformation("Pruned {Count} identity binding record(s) of addresses that are no longer configured", removed);
        return removed;
    }

    // ------------------------------------------------------------------------------------------------ file

    private readonly record struct FileStamp(bool Exists, bool Directory, bool PreviousExists, long Ticks, long Length);

    private FileStamp StampNow()
    {
        if (System.IO.Directory.Exists(_path)) return new FileStamp(false, true, false, 0, 0);
        var info = new FileInfo(_path);
        var previous = File.Exists(_path + DurableFile.BackupSuffix) || System.IO.Directory.Exists(_path + DurableFile.BackupSuffix);
        return info.Exists
            ? new FileStamp(true, false, previous, info.LastWriteTimeUtc.Ticks, info.Length)
            : new FileStamp(false, false, previous, 0, 0);
    }

    /// <summary>Re-reads the file when it changed since it was last read (or was written very recently).</summary>
    private void Refresh(bool force = false)
    {
        FileStamp stamp;
        try { stamp = StampNow(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkUnreadable(ex);
            return;
        }
        lock (_state)
        {
            // File times are wall-clock times: compared with the wall clock, not the injected time.
            var recent = stamp.Exists && DateTime.UtcNow - new DateTime(stamp.Ticks, DateTimeKind.Utc) < RecentWrite;
            if (!force && _stamp == stamp && !recent) return;
            try
            {
                _data = Read(stamp);
                _stamp = stamp;
                if (!_readOk) _loggedUnreadable = false;
                _readOk = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _stamp = stamp;
                MarkUnreadableLocked(ex);
            }
        }
    }

    private void MarkUnreadable(Exception ex)
    {
        lock (_state) MarkUnreadableLocked(ex);
    }

    private void MarkUnreadableLocked(Exception ex)
    {
        _readOk = false;
        if (_loggedUnreadable) return;
        _loggedUnreadable = true;
        // Fail closed: every federated admission and federated session refuses; local sign-in is unaffected. The type
        // only: a message may carry a path or content.
        _logger?.LogError("The identity bindings could not be read ({Error}); every Google/OIDC sign-in and session is " +
                          "refused until the file is repaired", ex.GetType().Name);
    }

    /// <summary>The file as it is: empty only when neither it nor its previous generation exists. Never the previous
    /// generation in its place.</summary>
    private IdentityBindingFile Read(FileStamp stamp)
    {
        if (stamp.Directory) throw new IOException("The identity bindings path is a directory.");
        if (!stamp.Exists)
        {
            if (stamp.PreviousExists)
                throw new FileNotFoundException("The identity bindings file is missing while its previous generation exists.");
            return Empty();
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Parse(File.ReadAllText(_path));
            }
            catch (IOException) when (attempt < 3 && File.Exists(_path))
            {
                Thread.Sleep(20);   // a rename by the other writer in progress (Windows)
            }
        }
    }

    /// <summary>Re-reads under the held gate and file lock and returns what the next write starts from — with what is no
    /// longer an entry, and what a failed prune had to remove, already taken out. Throws when the file cannot be read.</summary>
    private (IdentityBindingFile Data, List<string> Removed) LoadForWrite()
    {
        Refresh(force: true);
        IdentityBindingFile data;
        HashSet<string> mustRemove;
        lock (_state)
        {
            if (!_readOk) throw new IdentityBindingStoreUnavailableException("The identity bindings are unavailable.");
            data = Copy(_data);
            mustRemove = [.. _mustRemove];
        }
        var roles = Roles.GetRoleData();
        var whitelist = Whitelist.GetWhitelist();
        bool Gone(string id, string account) => mustRemove.Contains(id) || AccessEntries.EntryOf(roles, whitelist, account) is null;
        var removed = data.Bindings.Where(b => Gone(b.Id, b.Account)).Select(b => b.Id)
            .Concat(data.Pending.Where(p => Gone(p.Id, p.Account)).Select(p => p.Id)).ToList();
        data.Bindings.RemoveAll(b => removed.Contains(b.Id));
        data.Pending.RemoveAll(p => removed.Contains(p.Id));
        return (data, removed);
    }

    private IdentityBindingFile? TryLoadForWrite()
    {
        try { return LoadForWrite().Data; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not IdentityBindingStoreUnavailableException)
                _logger?.LogError("The identity bindings could not be prepared for a write ({Error})", ex.GetType().Name);
            return null;
        }
    }

    private static IdentityBindingFile Parse(string json)
    {
        // The schema's members are required (R1-27 review): a document without its version or one of its lists is not an
        // empty store — it may be one whose history was lost.
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The identity bindings are no object.");
            if (Member(root, "version") is not { ValueKind: JsonValueKind.Number } version || !version.TryGetInt32(out var v) || v != 1)
                throw new JsonException("unknown or missing version");
            if (Member(root, "bindings") is not { ValueKind: JsonValueKind.Array }) throw new JsonException("bindings missing");
            if (Member(root, "pending") is not { ValueKind: JsonValueKind.Array }) throw new JsonException("pending missing");
        }
        var file = JsonSerializer.Deserialize<IdentityBindingFile>(json, JsonOptions) ?? throw new JsonException("null");
        return Problem(file) is { } problem ? throw new JsonException(problem) : file;

        static JsonElement? Member(JsonElement root, string name)
        {
            foreach (var p in root.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }
    }

    /// <summary>The exclusive lock every writer of every process holds around its re-read, change and save.</summary>
    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(_path + LockSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (_time.GetUtcNow() < deadline)
            {
                await Task.Delay(25, ct);
            }
        }
    }

    private async Task<FileStream?> TryLockAsync(CancellationToken ct)
    {
        try { return await LockAsync(ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError("The identity bindings lock could not be taken ({Error}); the write is refused", ex.GetType().Name);
            return null;
        }
    }

    private async Task<bool> PersistAsync(IdentityBindingFile next, AccessControlWriteGate.Lease lease)
    {
        try
        {
            var save = PersistForTests?.Invoke(next)
                       ?? DurableFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(next, JsonOptions), keepPrevious: true);
            await save;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unavailable until a re-read and prune succeed (R1-27 review): what is on disk is not known to be what is in
            // force here.
            _degraded = true;
            _logger?.LogError("The identity bindings could not be saved ({Error}); every Google/OIDC sign-in and session is " +
                              "refused until they are read and pruned again", ex.GetType().Name);
            ScheduleRecovery();
            return false;
        }
        _gate.Demand(lease);   // still held: nobody else wrote while the save ran
        lock (_state)
        {
            _data = Copy(next);    // persisted first, published after
            try { _stamp = StampNow(); } catch (IOException) { _stamp = null; }
            _readOk = true;
            _mustRemove.Clear();
        }
        _degraded = false;
        return true;
    }

    /// <summary>Retries the re-read and prune every 30 s while the store is unavailable after a failed save.</summary>
    private void ScheduleRecovery()
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return;
        // A failed save may happen inside a decision (Confirm / Remove): the retry must not carry its scope.
        using var noFlow = ExecutionContext.SuppressFlow();
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 2880 && _degraded; attempt++)
                {
                    await Task.Delay(RecoveryInterval, _time);
                    try
                    {
                        if (await RecoverAsync(CancellationToken.None)) break;
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception) { /* the next attempt */ }
                }
            }
            finally { Volatile.Write(ref _recovering, 0); }
        });
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private IRoleService Roles => _services.GetRequiredService<IRoleService>();
    private IWhitelistService Whitelist => _services.GetRequiredService<IWhitelistService>();

    private string? EntryOf(string account) => AccessEntries.EntryOf(Roles.GetRoleData(), Whitelist.GetWhitelist(), account);

    /// <summary>The panel's writes: refused with authentication disabled, outside a decision without a principal, and
    /// for anyone the gateway did not decide as a human Admin (defence in depth behind the gateway).</summary>
    private string? Authority()
    {
        if (_authDisabled) return IdentityBindingErrors.AuthDisabled;
        if (MutationScope.Current?.Principal is not { } principal) return IdentityBindingErrors.SessionInvalid;
        return principal.Kind == MutationPrincipalKind.Human
               && McpPermissionLevels.Normalize(principal.PermissionLevel) == McpPermissionLevels.Admin
            ? null
            : IdentityBindingErrors.NotAuthorized;
    }

    /// <summary>Whether any explicit Admin can still sign in with <paramref name="bindings"/>: a local account with a
    /// password or an active binding (contract 0003, rule 6).</summary>
    private async Task<bool> AnyAdminCanSignInAsync(UserRoleData roles, IdentityBindingFile bindings, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetService<WhiskersIdentityDbContext>();
        var normalizer = scope.ServiceProvider.GetService<ILookupNormalizer>();
        foreach (var admin in roles.Roles.Where(r => r.Role == AppRole.Admin).Select(r => r.Email))
        {
            if (bindings.Bindings.Any(b => Same(b.Account, admin))) return true;
            if (await AdminSignIn.HasLocalPasswordAsync(db, normalizer, admin, ct)) return true;
        }
        return false;
    }

    private IdentityBindingRecord NewBinding(string account, string provider, string issuer, string subject, string method,
        string boundBy) => new()
    {
        Id = NewId("b_"), Account = account, Provider = provider, Issuer = issuer, Subject = subject, Method = method,
        BoundUtc = Now, BoundBy = boundBy, LastSignInDay = method == IdentityBindingMethods.VerifiedEmail ? Today : null,
    };

    private static string BoundDetails(IdentityBindingRecord b) =>
        $"binding={b.Id}; method={b.Method}; provider={b.Provider}; issuer={b.Issuer}; " +
        $"fingerprint={AccessEntries.Fingerprint(b.Issuer, b.Subject)}";

    private static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private DateTime Now => _time.GetUtcNow().UtcDateTime;
    private string Today => Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Contract 0003, rule 1.6: exactly <c>true</c>, case-insensitive — nothing trimmed, nothing else.</summary>
    internal static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static IdentityBindingFile Empty() => new() { Version = 1, Bindings = [], Pending = [] };

    private static IdentityBindingFile Copy(IdentityBindingFile src) => new()
    {
        Version = 1,
        Bindings = src.Bindings.Select(b => new IdentityBindingRecord
        {
            Id = b.Id, Account = b.Account, Provider = b.Provider, Issuer = b.Issuer, Subject = b.Subject, Method = b.Method,
            BoundUtc = b.BoundUtc, BoundBy = b.BoundBy, LastSignInDay = b.LastSignInDay,
        }).ToList(),
        Pending = src.Pending.Select(p => new PendingIdentityRecord
        {
            Id = p.Id, Account = p.Account, Provider = p.Provider, Issuer = p.Issuer, Subject = p.Subject, Reason = p.Reason,
            EmailVerifiedAsserted = p.EmailVerifiedAsserted, RequestedUtc = p.RequestedUtc, ExpiresUtc = p.ExpiresUtc,
        }).ToList(),
    };

    /// <summary>What makes a parsed file unusable: it is then handled like one that cannot be parsed.</summary>
    private static string? Problem(IdentityBindingFile file)
    {
        if (file.Version != 1) return "unknown version";
        if (file.Bindings is null || file.Pending is null) return "a list is missing";
        if (file.Bindings.Any(b => b is null || string.IsNullOrEmpty(b.Id) || string.IsNullOrWhiteSpace(b.Account)
                                   || string.IsNullOrEmpty(b.Issuer) || string.IsNullOrEmpty(b.Subject)))
            return "a binding is incomplete";
        if (file.Pending.Any(p => p is null || string.IsNullOrEmpty(p.Id) || string.IsNullOrWhiteSpace(p.Account)
                                  || string.IsNullOrEmpty(p.Issuer) || string.IsNullOrEmpty(p.Subject)))
            return "a pending request is incomplete";
        if (file.Bindings.GroupBy(b => (b.Issuer, b.Subject)).Any(g => g.Count() > 1)) return "a subject is bound twice";
        if (file.Bindings.GroupBy(b => (b.Account.Trim().ToUpperInvariant(), b.Issuer)).Any(g => g.Count() > 1))
            return "an account has two bindings of one issuer";
        return null;
    }
}

/// <summary>"An Admin who can sign in" (contract 0003, rule 6; R1-49): a local account with a password, or an active
/// identity binding. Shared by the binding store's last-Admin rule and <see cref="LocalAccountService"/>.</summary>
public static class AdminSignIn
{
    /// <summary>The one boundary of the last-usable-Admin rule (R1-27 review): every removal of an Admin's way to sign in —
    /// a binding removed in Settings, a local account deleted, reset or added by an Admin — checks the rule and commits
    /// while holding it, so two removals that each leave the other's way in place cannot both pass.</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Whether <paramref name="email"/> has a local account with a password. No account store: no.</summary>
    public static async Task<bool> HasLocalPasswordAsync(WhiskersIdentityDbContext? db, ILookupNormalizer? normalizer,
        string email, CancellationToken ct)
    {
        if (db is null) return false;
        var normalized = normalizer?.NormalizeEmail(email) ?? email.Trim().ToUpperInvariant();
        return await db.Users.AsNoTracking().AnyAsync(u => u.NormalizedEmail == normalized && u.PasswordHash != null, ct);
    }

    /// <summary>Whether <paramref name="email"/> has an active identity binding. An unavailable store: no (fail closed).</summary>
    public static bool HasBinding(IIdentityBindingStore? bindings, string email)
    {
        if (bindings is null) return false;
        try { return bindings.BindingsOf(email).Count > 0; }
        catch (IdentityBindingStoreUnavailableException) { return false; }
    }
}

/// <summary>The start-up prune of <see cref="IIdentityBindingStore"/> (contract 0003, rule 5): once the roles and the
/// whitelist are loaded, bindings and requests of addresses that are no longer entries go. If that cannot be saved the
/// store stays unavailable — every federated admission and session refuses — until a later re-read and prune succeed;
/// the start goes on.</summary>
public sealed class IdentityBindingStartupPrune(IIdentityBindingStore store, AccessControlWriteGate gate,
    ILogger<IdentityBindingStartupPrune>? logger = null) : IInitializable
{
    public int Order => 26;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!store.IsAvailable) return;
        try
        {
            using var lease = await gate.EnterAsync(ct);
            await store.PruneOrphansAsync(lease, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The store marked itself unavailable when the save failed (and remembers what has to go).
            logger?.LogError("The start-up prune of the identity bindings failed ({Error}); Google/OIDC sign-ins stay refused " +
                             "until it succeeds", ex.GetType().Name);
        }
    }
}

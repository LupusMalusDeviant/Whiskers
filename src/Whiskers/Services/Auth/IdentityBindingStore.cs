using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Auth;

/// <summary>
/// R1-27: <see cref="IIdentityBindingStore"/> on <c>identity-bindings.json</c> (<see cref="JsonFileStore{T}"/>: durable,
/// <c>.bak</c> generation, R1-35). The published state is an immutable copy; readers never lock. Every write holds the
/// <see cref="AccessControlWriteGate"/> shared with the role and whitelist stores, persists first and publishes after.
/// <para>The roles and the whitelist are read through the service provider when they are needed: the role and whitelist
/// writers call <see cref="PruneOrphansAsync"/> before their own writes, so they depend on this store and not the other
/// way round. <see cref="IInitializable"/> Order 15 (after the whitelist, before the roles and the setup state); the
/// start-up prune runs later (<see cref="IdentityBindingStartupPrune"/>, Order 26), once the roles are loaded.</para>
/// </summary>
public sealed class IdentityBindingStore : IIdentityBindingStore, IInitializable
{
    /// <summary>How long a binding request waits for confirmation.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);

    /// <summary>At most this many binding requests are kept; the oldest go first.</summary>
    public const int MaxPending = 64;

    /// <summary>The actor of the host CLI's audit row.</summary>
    public const string CliActor = "cli (--confirm-identity-binding)";

    private readonly JsonFileStore<IdentityBindingFile> _file;
    private readonly IServiceProvider _services;
    private readonly IAuditLogService _audit;
    private readonly AccessControlWriteGate _gate;
    private readonly TimeProvider _time;
    private readonly ILogger<IdentityBindingStore>? _logger;
    private readonly bool _authDisabled;

    private volatile IdentityBindingFile _data = new();
    private volatile bool _available = true;

    public IdentityBindingStore(IServiceProvider services, IAuditLogService audit, IConfiguration configuration,
        AccessControlWriteGate? writeGate = null, DataPathOptions? dataPaths = null, TimeProvider? time = null,
        ILogger<IdentityBindingStore>? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentNullException.ThrowIfNull(configuration);
        _authDisabled = configuration.GetValue<bool>("Auth:Disabled");
        _gate = writeGate ?? new AccessControlWriteGate();
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _file = new JsonFileStore<IdentityBindingFile>((dataPaths ?? DataPathOptions.Default).IdentityBindingsJson,
            problem: Problem);
    }

    /// <summary>Tests only: replaces the save (one that fails on cue). Null in the app.</summary>
    internal Func<IdentityBindingFile, Task>? PersistForTests { get; set; }

    public int Order => 15;

    public bool IsAvailable => _available;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var loaded = await _file.LoadAsync();
            _data = Copy(loaded);
            _available = true;
            _logger?.LogInformation("Loaded {Bindings} identity binding(s) and {Pending} pending request(s)",
                loaded.Bindings.Count, loaded.Pending.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: every federated admission and federated session refuses; local sign-in is unaffected and the
            // start goes on. The type only: a message may carry a path or content.
            _available = false;
            _logger?.LogError("The identity bindings could not be read ({Error}); every Google/OIDC sign-in and session is " +
                              "refused until the file is repaired", ex.GetType().Name);
        }
    }

    // ------------------------------------------------------------------------------------------------ reads

    private IdentityBindingFile Current =>
        _available ? _data : throw new IdentityBindingStoreUnavailableException("The identity bindings are unavailable.");

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

    // ------------------------------------------------------------------------------------------------ sign-in writes

    public async Task<IdentityBindingResult> BindAtSignInAsync(FederatedSignInClaims claims, string account,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (!_available) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        if (string.IsNullOrEmpty(claims.Issuer) || string.IsNullOrEmpty(claims.Subject) || string.IsNullOrWhiteSpace(account))
            return IdentityBindingResult.Refused(IdentityBindingErrors.Conflict);

        using var lease = await _gate.EnterAsync(ct);
        var data = Current;
        // Re-checked under the gate (contract 0003, rule 1.7): the subject is still unbound, the account has no binding
        // yet (another subject or issuer would have to be confirmed) and it is still a configured entry.
        if (data.Bindings.Any(b => b.Issuer == claims.Issuer && b.Subject == claims.Subject)
            || data.Bindings.Any(b => Same(b.Account, account)))
            return IdentityBindingResult.Refused(IdentityBindingErrors.Conflict);
        if (EntryOf(account) is not { } handle) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);

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
        if (!_available) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        if (string.IsNullOrEmpty(claims.Issuer) || string.IsNullOrEmpty(claims.Subject))
            return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);

        using var lease = await _gate.EnterAsync(ct);
        var data = Current;
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
        if (!_available) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        var today = Today;
        if (Current.Bindings.FirstOrDefault(b => b.Id == bindingId) is { } seen && seen.LastSignInDay == today)
            return IdentityBindingResult.Ok(bindingId);

        using var lease = await _gate.EnterAsync(ct);
        var next = Copy(Current);
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
        if (!_available) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        using var lease = await _gate.EnterAsync(ct);
        var request = Current.Pending.FirstOrDefault(p => p.Id == pendingId && p.ExpiresUtc > Now);
        if (request is null) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        var by = MutationScope.Current?.Principal?.Id ?? "user:unknown";
        return await BindPendingAsync(request, IdentityBindingMethods.Admin, by, lease, auditFirst: null);
    }

    public async Task<IdentityBindingResult> RemoveAsync(string id, CancellationToken ct = default)
    {
        if (Authority() is { } refused) return IdentityBindingResult.Refused(refused);
        if (!_available) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        using var lease = await _gate.EnterAsync(ct);
        var data = Current;
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
        if (!await PersistAsync(next, lease)) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        _logger?.LogInformation("Identity binding {Binding} was removed", binding.Id);
        return IdentityBindingResult.Ok(binding.Id);
    }

    public async Task<IdentityBindingResult> ConfirmFromHostAsync(string account, string? issuer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account)) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        if (!_available) return IdentityBindingResult.Refused(IdentityBindingErrors.StoreUnavailable);
        using var lease = await _gate.EnterAsync(ct);
        var now = Now;
        var waiting = Current.Pending
            .Where(p => Same(p.Account, account.Trim()) && p.ExpiresUtc > now
                        && (string.IsNullOrEmpty(issuer) || p.Issuer == issuer))
            .OrderByDescending(p => p.RequestedUtc)
            .ToList();
        if (waiting.Count == 0) return IdentityBindingResult.Refused(IdentityBindingErrors.NotFound);
        if (waiting.Select(p => p.Issuer).Distinct(StringComparer.Ordinal).Count() > 1)
            return new IdentityBindingResult(false, IdentityBindingErrors.Ambiguous) { Candidates = waiting.Select(Info).ToList() };

        var request = waiting[0];
        return await BindPendingAsync(request, IdentityBindingMethods.HostCli, "cli", lease, auditFirst: CliActor);
    }

    /// <summary>Turns <paramref name="request"/> into a binding under the held gate. With <paramref name="auditFirst"/>
    /// (the CLI) the <c>auth.identity_bound</c> row is written first by that actor; no row, no change.</summary>
    private async Task<IdentityBindingResult> BindPendingAsync(PendingIdentityRecord request, string method, string boundBy,
        AccessControlWriteGate.Lease lease, string? auditFirst)
    {
        var data = Current;
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
        var data = Current;   // unavailable → throws: the caller's write is refused
        var roles = nextRoles ?? Roles.GetRoleData();
        var whitelist = nextWhitelist ?? Whitelist.GetWhitelist();
        bool Orphan(string account) => AccessEntries.EntryOf(roles, whitelist, account) is null;
        var next = Copy(data);
        var removed = next.Bindings.RemoveAll(b => Orphan(b.Account)) + next.Pending.RemoveAll(p => Orphan(p.Account));
        if (removed == 0) return 0;
        if (!await PersistAsync(next, lease))
            throw new IdentityBindingStoreUnavailableException("The identity bindings of removed addresses could not be pruned.");
        _logger?.LogInformation("Pruned {Count} identity binding record(s) of addresses that are no longer configured", removed);
        return removed;
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

    private async Task<bool> PersistAsync(IdentityBindingFile next, AccessControlWriteGate.Lease lease)
    {
        try
        {
            await (PersistForTests?.Invoke(next) ?? _file.SaveAsync(next));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError("The identity bindings could not be saved ({Error}); nothing was changed", ex.GetType().Name);
            return false;
        }
        _gate.Demand(lease);   // still held: nobody else wrote while the save ran
        _data = Copy(next);    // persisted first, published after
        return true;
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

    internal static bool IsTrue(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>What makes a parsed file unusable: it is then handled like one that cannot be parsed (R1-40).</summary>
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
/// whitelist are loaded, bindings and requests of addresses that are no longer entries go. A failure is logged; the
/// start goes on (an unavailable store refuses every federated sign-in anyway).</summary>
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
            logger?.LogError("The start-up prune of the identity bindings failed ({Error})", ex.GetType().Name);
        }
    }
}

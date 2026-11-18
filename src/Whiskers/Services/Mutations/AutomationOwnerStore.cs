using System.Text.Json;
using Whiskers.Configuration;

namespace Whiskers.Services.Mutations;

/// <summary>What <see cref="IAutomationOwnerStore.SuspendAsync"/> did.</summary>
public enum SuspendResult
{
    /// <summary>Suspended and saved.</summary>
    Suspended,
    /// <summary>Suspended in memory only: the file could not be written (holds until a restart).</summary>
    NotSaved,
    /// <summary>Not suspended: the feature has another owner now (taken over meanwhile) or is already blocked.</summary>
    Superseded,
}

/// <summary>The owner an admin confirmed for a fleet-wide automation feature. <see cref="Blocked"/> marks a
/// feature that must not run until an admin takes it over: its owner was lost with an unreadable file, or the owner
/// lost their authority and the feature was suspended (<see cref="SuspendedReason"/>, PC-D6).</summary>
public sealed record AutomationOwnerEntry(string OwnerId, DateTime ConfirmedAt, bool Blocked = false,
    string? SuspendedReason = null);

/// <summary>
/// Owners of the fleet-wide automation features that have no item of their own to store one on (PC-D6):
/// <see cref="AutoPatch"/>, <see cref="AutoUpdate"/> and the <see cref="VpnBootstrap">VPN bring-up</see>. An admin takes
/// a feature over in Settings; from then on
/// every cycle runs on that admin's authority, re-checked per run. Scheduled tasks keep their owner on the task.
/// </summary>
public interface IAutomationOwnerStore
{
    public const string AutoPatch = "autopatch";
    public const string AutoUpdate = "autoupdate";

    /// <summary>The in-app VPN bring-up at start (R1-43, S-07): it starts the VPN daemon and joins the mesh on the
    /// authority of the admin who took it over; without one it does not run.</summary>
    public const string VpnBootstrap = "vpnbootstrap";

    /// <summary>The features this store keeps an owner for.</summary>
    public static readonly IReadOnlyList<string> Features = [AutoPatch, AutoUpdate, VpnBootstrap];

    /// <summary>The feature's owner; <c>null</c> when it has none or is <see cref="IsBlocked">blocked</see>.</summary>
    AutomationOwnerEntry? Get(string feature);

    /// <summary><c>true</c> while the feature must not run at all: the owner file could not be read (missing is
    /// fine; a directory, an access error or invalid content is not), or a previous such failure was not yet
    /// resolved for this feature. "No owner" would put AutoUpdate into the grace period, so a broken file would raise
    /// its authority. Only taking that feature over clears it; taking over another feature does not.</summary>
    bool IsBlocked(string feature);

    /// <summary>Records <paramref name="ownerId"/> as the feature's owner and clears its block. Returns the owner it
    /// replaced (also a suspended one; <c>null</c>: none), read under the same lock as the write, so a takeover is
    /// audited with the assignment it actually replaced. Throws when the file cannot be written.</summary>
    [MutatingOperation("automation.owner_set")]
    Task<string?> SetAsync([MutationTarget] string feature, string ownerId, CancellationToken ct = default);

    /// <summary>Suspends the feature because its owner lost their authority (PC-D6): it is blocked, keeps the owner
    /// for the record and stays refused across restarts until an admin takes it over. Only ever lowers authority, so
    /// it needs no gateway decision. Never fails open: when the file cannot be written the feature is still blocked
    /// in memory (until the next successful write keeps it, or a restart) and <c>false</c> is returned, so the caller
    /// can tell the admins. Only the assignment of <paramref name="expectedOwnerId"/> (<c>null</c>: no owner yet) is
    /// suspended: when an admin took
    /// the feature over in the meantime, nothing changes and <see cref="SuspendResult.Superseded"/> is returned.</summary>
    Task<SuspendResult> SuspendAsync(string feature, string? expectedOwnerId, string reason, CancellationToken ct = default);

    /// <summary>The entry of a blocked feature (owner and reason), for the UI; <c>null</c> otherwise.</summary>
    AutomationOwnerEntry? GetBlocked(string feature) => null;

    /// <summary>Contract 0020: entry := (<paramref name="toOwnerId"/>, blocked, the handover text) only while the owner
    /// is <paramref name="fromOwnerId"/>. Called only from <c>AutomationHandover</c>.</summary>
    [MutatingOperation("automation.handover")]
    Task<HandoverWrite> HandOverAsync(string feature, string fromOwnerId, string toOwnerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot hand a feature over.");

    /// <summary>Contract 0020 (D11 c): blocked with the hold text only while the owner is <paramref name="ownerId"/>; the
    /// owner stays. Inside <c>account.create</c> only.</summary>
    [MutatingOperation("automation.hold")]
    Task<HandoverWrite> HoldAsync(string feature, string ownerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot hold a feature.");

    /// <summary>Contract 0020 (D7): <b>Resume as {owner}</b> — clears the block only while <paramref name="expectedOwnerId"/>
    /// is recorded; the owner is kept (a resume never re-owns). False when another owner is recorded or it is not
    /// blocked.</summary>
    [MutatingOperation("automation.owner_set")]
    Task<bool> ResumeAsync([MutationTarget] string feature, string expectedOwnerId, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} cannot resume a feature.");
}

/// <summary>A small JSON file in the data directory (<c>automation-owners.json</c>), written atomically.</summary>
public sealed class AutomationOwnerStore : IAutomationOwnerStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<AutomationOwnerStore> _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private Dictionary<string, AutomationOwnerEntry> _entries;
    private bool _loadFailed;

    // Contract 0020: the first-run ledger of handed-over features.
    private readonly IHandoverLedger? _handovers;

    /// <summary>Contract 0020: the block text of a handover and of an <c>account.create</c> hold.</summary>
    public const string HandoverReason = "handed over: blocked until an Admin resumes it";
    public const string HoldReason = "held when a local account was created for its owner's address: blocked until an Admin resumes it";

    public AutomationOwnerStore(DataPathOptions paths, ILogger<AutomationOwnerStore> logger, IHandoverLedger? handovers = null)
    {
        _handovers = handovers;
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.AutomationOwnersJson;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _entries = Load();
    }

    public bool IsBlocked(string feature) =>
        Volatile.Read(ref _entries).GetValueOrDefault(feature)?.Blocked == true;

    public AutomationOwnerEntry? Get(string feature) =>
        Volatile.Read(ref _entries).GetValueOrDefault(feature) is { Blocked: false } entry ? entry : null;

    public AutomationOwnerEntry? GetBlocked(string feature) =>
        Volatile.Read(ref _entries).GetValueOrDefault(feature) is { Blocked: true } entry ? entry : null;

    public async Task<string?> SetAsync(string feature, string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var replaced = await WriteAsync(feature, _ => new AutomationOwnerEntry(ownerId, DateTime.UtcNow), ct);
        // Contract 0020: a takeover as today ends a handover's first-run note.
        HandoverNotes.ForgetQuietly(_handovers, new AutomationItemRef(AutomationKind.FleetFeature, feature), _logger);
        return replaced?.OwnerId is { Length: > 0 } previous ? previous : null;
    }

    public Task<HandoverWrite> HandOverAsync(string feature, string fromOwnerId, string toOwnerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromOwnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toOwnerId);
        return BlockAsync(feature, fromOwnerId, toOwnerId, HandoverReason, ct);
    }

    public Task<HandoverWrite> HoldAsync(string feature, string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return BlockAsync(feature, ownerId, ownerId, HoldReason, ct);
    }

    /// <summary>Contract 0020: owner <paramref name="from"/> → <paramref name="to"/> (the same for a hold), blocked with
    /// the reason, only while <paramref name="from"/> is recorded — under the write lock, with the versioned diff.</summary>
    private async Task<HandoverWrite> BlockAsync(string feature, string from, string to, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        HandoverWrite? result = null;
        await WriteIfAsync(feature, current =>
        {
            if (current is null || string.IsNullOrWhiteSpace(current.OwnerId))
            {
                result = new(HandoverItemStatus.SkippedGone, feature, false);
                return null;
            }
            if (!string.Equals(current.OwnerId, from, StringComparison.Ordinal))
            {
                result = new(HandoverItemStatus.SkippedOwnerChanged, feature, !current.Blocked);
                return null;
            }
            result = new(HandoverItemStatus.HandedOver, feature, !current.Blocked);
            return new AutomationOwnerEntry(to, DateTime.UtcNow, Blocked: true, SuspendedReason: reason);
        }, ct);
        return result!;
    }

    public async Task<bool> ResumeAsync(string feature, string expectedOwnerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerId);
        var resumed = false;
        await WriteIfAsync(feature, current =>
        {
            if (current is not { Blocked: true } || !string.Equals(current.OwnerId, expectedOwnerId, StringComparison.Ordinal))
                return null;
            resumed = true;
            // The owner is kept: a resume never re-owns (D7).
            return new AutomationOwnerEntry(expectedOwnerId, DateTime.UtcNow);
        }, ct);
        if (resumed)
            HandoverNotes.RecordResumeInScope(_handovers, new AutomationItemRef(AutomationKind.FleetFeature, feature), _logger);
        return resumed;
    }

    /// <summary>Replaces the feature's entry with what <paramref name="next"/> returns for the current one, unless it
    /// returns <c>null</c> (nothing written). Throws when the file cannot be written.</summary>
    private async Task WriteIfAsync(string feature, Func<AutomationOwnerEntry?, AutomationOwnerEntry?> next, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        await _write.WaitAsync(ct);
        try
        {
            var current = _entries.GetValueOrDefault(feature);
            if (next(current) is not { } entry) return;
            var updated = new Dictionary<string, AutomationOwnerEntry>(_entries, StringComparer.Ordinal) { [feature] = entry };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_loadFailed) KeepUnreadableFile();
            await Whiskers.Services.Persistence.DurableFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(updated, Json), ct: ct);
            Volatile.Write(ref _entries, updated);
            _loadFailed = false;
            ConfigChange.Record($"automation-owner:{feature}", current, entry);   // R1-22
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task<SuspendResult> SuspendAsync(string feature, string? expectedOwnerId, string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _write.WaitAsync(ct);
        try
        {
            // Checked under the write lock: a takeover that completed after the run read its owner wins. With no
            // expected owner, only a feature that still has none is suspended.
            var current = _entries.GetValueOrDefault(feature);
            var expected = string.IsNullOrWhiteSpace(expectedOwnerId) ? null : expectedOwnerId;
            if (current is { Blocked: true }
                || !string.Equals(current?.OwnerId is { Length: > 0 } id ? id : null, expected, StringComparison.Ordinal))
                return SuspendResult.Superseded;
            var next = new Dictionary<string, AutomationOwnerEntry>(_entries, StringComparer.Ordinal)
            {
                [feature] = new(current?.OwnerId ?? "", DateTime.UtcNow, Blocked: true, SuspendedReason: reason),
            };
            try
            {
                Persist(next, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Fail closed: block it in memory anyway. The next write of any feature carries the block to disk.
                Volatile.Write(ref _entries, next);
                _logger.LogError(ex, "The suspension of {Feature} could not be saved to {Path}; it is blocked until " +
                                     "Whiskers restarts", feature, _path);
                return SuspendResult.NotSaved;
            }
            Volatile.Write(ref _entries, next);
            _loadFailed = false;
            return SuspendResult.Suspended;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Writes <paramref name="next"/> atomically. Call under <see cref="_write"/>.</summary>
    private void Persist(Dictionary<string, AutomationOwnerEntry> next, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (_loadFailed) KeepUnreadableFile();
        // The other features' blocks are written too, so they survive a restart until each is taken over. Flushed (R1-35).
        Whiskers.Services.Persistence.DurableFile.WriteAllText(_path, JsonSerializer.Serialize(next, Json));
    }

    /// <summary>Replaces the feature's entry and returns the one it replaced.</summary>
    private async Task<AutomationOwnerEntry?> WriteAsync(string feature,
        Func<AutomationOwnerEntry?, AutomationOwnerEntry> entryOf, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        await _write.WaitAsync(ct);
        try
        {
            var replaced = _entries.GetValueOrDefault(feature);
            var next = new Dictionary<string, AutomationOwnerEntry>(_entries, StringComparer.Ordinal)
            {
                [feature] = entryOf(replaced),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_loadFailed) KeepUnreadableFile();
            // The other features' blocks are written too, so they survive a restart until each is taken over. Flushed (R1-35).
            await Whiskers.Services.Persistence.DurableFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(next, Json), ct: ct);
            Volatile.Write(ref _entries, next);
            _loadFailed = false;
            // The whole entry (owner, block and its reason) as a versioned diff in the gateway's event (R1-22): a
            // takeover that lifts a suspension shows it, also when the owner stays the same.
            ConfigChange.Record($"automation-owner:{feature}", replaced, next[feature]);
            return replaced;
        }
        finally
        {
            _write.Release();
        }
    }

    private Dictionary<string, AutomationOwnerEntry> Load()
    {
        try
        {
            // Only a file that is really not there means "no owners yet". A directory at the path, an access error or
            // invalid content blocks every feature.
            if (Directory.Exists(_path))
                throw new IOException("The owner file path is a directory.");
            string text;
            try { text = File.ReadAllText(_path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(StringComparer.Ordinal);
            }
            var loaded = JsonSerializer.Deserialize<Dictionary<string, AutomationOwnerEntry>>(text);
            if (loaded is null || loaded.Values.Any(e => e is null || (!e.Blocked && string.IsNullOrWhiteSpace(e.OwnerId))))
                throw new JsonException("The owner file holds an empty or invalid entry.");
            // Owners recorded before the principal id scheme: a bare email is migrated explicitly (logged); an
            // ambiguous value is kept as it is and stays suspended.
            var entries = new Dictionary<string, AutomationOwnerEntry>(StringComparer.Ordinal);
            foreach (var (feature, entry) in loaded)
            {
                var migrated = AutomationOwners.MigrateLegacy(entry.OwnerId) ?? entry.OwnerId;
                if (!string.Equals(migrated, entry.OwnerId, StringComparison.Ordinal))
                    _logger.LogInformation("Automation owner of {Feature} migrated to the principal id scheme", feature);
                entries[feature] = entry with { OwnerId = migrated };
            }
            return entries;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // Fail closed: AutoPatch and AutoUpdate stay blocked until an admin takes each of them over again.
            _logger.LogError(ex, "Automation owners could not be read from {Path}; AutoPatch, AutoUpdate and the VPN " +
                                 "bring-up will not run until an admin takes each of them over in Settings", _path);
            _loadFailed = true;
            return IAutomationOwnerStore.Features.ToDictionary(f => f,
                _ => new AutomationOwnerEntry("", DateTime.UtcNow, Blocked: true), StringComparer.Ordinal);
        }
    }

    /// <summary>Keeps a copy of the file that could not be read before the first write replaces it (best effort).</summary>
    private void KeepUnreadableFile()
    {
        try
        {
            if (File.Exists(_path))
                File.Copy(_path, $"{_path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The unreadable owner file {Path} could not be kept", _path);
        }
    }
}

using System.Text.Json;
using Whiskers.Configuration;

namespace Whiskers.Services.Mutations;

/// <summary>The owner an admin confirmed for a fleet-wide automation feature. <see cref="Blocked"/> marks a
/// feature whose owner was lost with an unreadable file: it stays refused until an admin takes it over.</summary>
public sealed record AutomationOwnerEntry(string OwnerId, DateTime ConfirmedAt, bool Blocked = false);

/// <summary>
/// Owners of the fleet-wide automation features that have no item of their own to store one on (PC-D6):
/// <see cref="AutoPatch"/> and <see cref="AutoUpdate"/>. An admin takes a feature over in Settings; from then on
/// every cycle runs on that admin's authority, re-checked per run. Scheduled tasks keep their owner on the task.
/// </summary>
public interface IAutomationOwnerStore
{
    public const string AutoPatch = "autopatch";
    public const string AutoUpdate = "autoupdate";

    /// <summary>The features this store keeps an owner for.</summary>
    public static readonly IReadOnlyList<string> Features = [AutoPatch, AutoUpdate];

    /// <summary>The feature's owner; <c>null</c> when it has none or is <see cref="IsBlocked">blocked</see>.</summary>
    AutomationOwnerEntry? Get(string feature);

    /// <summary><c>true</c> while the feature must not run at all: the owner file could not be read (missing is
    /// fine; a directory, an access error or invalid content is not), or a previous such failure was not yet
    /// resolved for this feature. "No owner" would put AutoUpdate into the grace period, so a broken file would raise
    /// its authority. Only taking that feature over clears it; taking over another feature does not.</summary>
    bool IsBlocked(string feature);

    /// <summary>Records <paramref name="ownerId"/> as the feature's owner and clears its block. Throws when the file
    /// cannot be written.</summary>
    [MutatingOperation("automation.owner_set")]
    Task SetAsync(string feature, string ownerId, CancellationToken ct = default);
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

    public AutomationOwnerStore(DataPathOptions paths, ILogger<AutomationOwnerStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.AutomationOwnersJson;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _entries = Load();
    }

    public bool IsBlocked(string feature) =>
        Volatile.Read(ref _entries).GetValueOrDefault(feature)?.Blocked == true;

    public AutomationOwnerEntry? Get(string feature) =>
        Volatile.Read(ref _entries).GetValueOrDefault(feature) is { Blocked: false } entry ? entry : null;

    public async Task SetAsync(string feature, string ownerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await _write.WaitAsync(ct);
        try
        {
            var next = new Dictionary<string, AutomationOwnerEntry>(_entries, StringComparer.Ordinal)
            {
                [feature] = new AutomationOwnerEntry(ownerId, DateTime.UtcNow),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_loadFailed) KeepUnreadableFile();
            var temp = _path + ".tmp";
            // The other features' blocks are written too, so they survive a restart until each is taken over.
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(next, Json), ct);
            File.Move(temp, _path, overwrite: true);
            Volatile.Write(ref _entries, next);
            _loadFailed = false;
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
            _logger.LogError(ex, "Automation owners could not be read from {Path}; AutoPatch and AutoUpdate will not run " +
                                 "until an admin takes each of them over in Settings", _path);
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

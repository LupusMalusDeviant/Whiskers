using System.Text.Json;
using Whiskers.Configuration;

namespace Whiskers.Services.Mutations;

/// <summary>The owner an admin confirmed for a fleet-wide automation feature.</summary>
public sealed record AutomationOwnerEntry(string OwnerId, DateTime ConfirmedAt);

/// <summary>
/// Owners of the fleet-wide automation features that have no item of their own to store one on (PC-D6):
/// <see cref="AutoPatch"/> and <see cref="AutoUpdate"/>. An admin takes a feature over in Settings; from then on
/// every cycle runs on that admin's authority, re-checked per run. Scheduled tasks keep their owner on the task.
/// </summary>
public interface IAutomationOwnerStore
{
    public const string AutoPatch = "autopatch";
    public const string AutoUpdate = "autoupdate";

    AutomationOwnerEntry? Get(string feature);

    /// <summary>Records <paramref name="ownerId"/> as the feature's owner. Throws when the file cannot be written.</summary>
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

    public AutomationOwnerStore(DataPathOptions paths, ILogger<AutomationOwnerStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.AutomationOwnersJson;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _entries = Load();
    }

    public AutomationOwnerEntry? Get(string feature) =>
        Volatile.Read(ref _entries).GetValueOrDefault(feature);

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
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(next, Json), ct);
            File.Move(temp, _path, overwrite: true);
            Volatile.Write(ref _entries, next);
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
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, AutomationOwnerEntry>>(File.ReadAllText(_path));
            return loaded is null
                ? new(StringComparer.Ordinal)
                : new Dictionary<string, AutomationOwnerEntry>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable means "no owner": AutoPatch then stays off and AutoUpdate falls back to the grace rule.
            _logger.LogError(ex, "Automation owners could not be read from {Path}; treating every feature as unowned", _path);
            return new(StringComparer.Ordinal);
        }
    }
}

using System.Text.Json;
using Whiskers.Configuration;
using Whiskers.Models;

namespace Whiskers.Services.Mutations;

/// <summary>
/// The durable fallback for audit events that could not be inserted (R1-19): a refusal has no intent row before it, so
/// when its insert fails the complete, already redacted event is kept as one file under <c>audit-outbox/</c>, named by
/// its <see cref="AuditLogEntity.EventId"/>, and inserted later by <see cref="AuditRecovery"/> — also after a restart.
/// The event id makes the insert idempotent: a file whose event is already in the table is just removed.
/// </summary>
public sealed class AuditOutbox
{
    private readonly string _dir;
    private int _lost;

    public AuditOutbox(DataPathOptions? dataPaths = null) =>
        _dir = (dataPaths ?? DataPathOptions.Default).AuditOutboxDir;

    /// <summary>Events that could neither be inserted nor kept in the outbox (the disk failed too): lost, and the
    /// audit health stays Degraded for the life of the process.</summary>
    public int Lost => Volatile.Read(ref _lost);

    /// <summary>Keeps the event; false (and counted as lost) when the file cannot be written.</summary>
    public bool TryKeep(AuditLogEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrEmpty(row.EventId);
        try
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, row.EventId + ".json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(row));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _lost);
            return false;
        }
    }

    /// <summary>The kept events, oldest first. A file that cannot be read is left where it is and still counts.</summary>
    public IReadOnlyList<(string Path, AuditLogEntity? Row)> Load()
    {
        if (!Directory.Exists(_dir)) return [];
        return Directory.EnumerateFiles(_dir, "*.json")
            .OrderBy(File.GetLastWriteTimeUtc)
            .Select(path =>
            {
                try { return (path, JsonSerializer.Deserialize<AuditLogEntity>(File.ReadAllText(path))); }
                catch (Exception) { return (path, (AuditLogEntity?)null); }
            })
            .ToList();
    }

    public int Count => Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "*.json").Count() : 0;

    public void Remove(string path) => File.Delete(path);
}

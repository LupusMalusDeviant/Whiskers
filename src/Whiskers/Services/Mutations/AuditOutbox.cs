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

    /// <summary>An event that could not even be built (nothing to keep): counted as lost.</summary>
    public void MarkLost() => Interlocked.Increment(ref _lost);

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
        // Only a directory that is really not there is empty; any other failure (permissions, I/O) reaches the
        // caller's pass, and Count keeps it visible.
        List<string> files;
        try { files = Directory.EnumerateFiles(_dir, "*.json").ToList(); }
        catch (DirectoryNotFoundException) { return []; }
        return files
            .OrderBy(File.GetLastWriteTimeUtc)
            .Select(path =>
            {
                try { return (path, JsonSerializer.Deserialize<AuditLogEntity>(File.ReadAllText(path))); }
                catch (Exception) { return (path, (AuditLogEntity?)null); }
            })
            .ToList();
    }

    /// <summary>How many events wait in the outbox; 1 when the directory cannot be read (unknown is not empty).</summary>
    public int Count
    {
        get
        {
            // Directory.Exists says "no" for a directory it may not read: only a missing directory counts as empty.
            try { return Directory.EnumerateFiles(_dir, "*.json").Count(); }
            catch (DirectoryNotFoundException) { return 0; }
            catch (Exception) { return 1; }
        }
    }

    public void Remove(string path) => File.Delete(path);
}

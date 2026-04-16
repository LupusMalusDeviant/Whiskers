using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ServerWatch.Configuration;
using ServerWatch.Models;

namespace ServerWatch.Services.HealthMonitor;

public class InMemoryHealthStore : IHealthStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<HealthRecord>> _records = new();
    private readonly ConcurrentDictionary<string, HealthRecord> _latest = new();
    private readonly int _retentionHours;

    public InMemoryHealthStore(IOptions<HealthMonitorSettings> settings)
    {
        _retentionHours = settings.Value.HistoryRetentionHours;
    }

    public void AddRecord(HealthRecord record)
    {
        var queue = _records.GetOrAdd(record.ContainerId, _ => new ConcurrentQueue<HealthRecord>());
        queue.Enqueue(record);
        _latest[record.ContainerId] = record;
        Prune(queue);
    }

    public IList<HealthRecord> GetHistory(string containerId, TimeSpan? window = null)
    {
        if (!_records.TryGetValue(containerId, out var queue))
            return Array.Empty<HealthRecord>();

        var cutoff = DateTime.UtcNow - (window ?? TimeSpan.FromHours(_retentionHours));
        return queue.Where(r => r.Timestamp >= cutoff).OrderByDescending(r => r.Timestamp).ToList();
    }

    public IDictionary<string, HealthRecord> GetAllLatest()
    {
        return new Dictionary<string, HealthRecord>(_latest);
    }

    public IList<HealthRecord> GetAllRecords(TimeSpan? window = null)
    {
        var cutoff = DateTime.UtcNow - (window ?? TimeSpan.FromHours(_retentionHours));
        return _records.Values
            .SelectMany(q => q)
            .Where(r => r.Timestamp >= cutoff)
            .OrderByDescending(r => r.Timestamp)
            .ToList();
    }

    private void Prune(ConcurrentQueue<HealthRecord> queue)
    {
        var cutoff = DateTime.UtcNow.AddHours(-_retentionHours);
        while (queue.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            queue.TryDequeue(out _);
        }
    }
}

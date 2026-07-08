using Whiskers.Models;

namespace Whiskers.Services.HealthMonitor;

public interface IHealthStore
{
    void AddRecord(HealthRecord record);
    IList<HealthRecord> GetHistory(string containerId, TimeSpan? window = null);
    IDictionary<string, HealthRecord> GetAllLatest();
    IList<HealthRecord> GetAllRecords(TimeSpan? window = null);
}

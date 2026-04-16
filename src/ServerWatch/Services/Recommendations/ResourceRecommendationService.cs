using Microsoft.EntityFrameworkCore;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Recommendations;

public class ResourceRecommendation
{
    public string ContainerId { get; set; } = "";
    public string ContainerName { get; set; } = "";
    public double AvgCpuPercent { get; set; }
    public double PeakCpuPercent { get; set; }
    public long AvgMemoryBytes { get; set; }
    public long PeakMemoryBytes { get; set; }
    public long CurrentMemoryLimit { get; set; }
    public long RecommendedMemoryBytes { get; set; }
    public string Verdict { get; set; } = "";  // "optimal", "over-provisioned", "under-provisioned", "no-limit"
    public string Hint { get; set; } = "";
}

public class ResourceRecommendationService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ResourceRecommendationService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<List<ResourceRecommendation>> GetRecommendationsAsync(string? serverId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var since = DateTime.UtcNow.AddDays(-7);
        var metrics = await db.ContainerMetrics
            .Where(m => m.Timestamp > since && (serverId == null || m.ServerId == serverId))
            .GroupBy(m => new { m.ContainerId, m.ContainerName })
            .Select(g => new
            {
                g.Key.ContainerId,
                g.Key.ContainerName,
                AvgCpu = g.Average(m => m.CpuPercent),
                PeakCpu = g.Max(m => m.CpuPercent),
                AvgMem = (long)g.Average(m => m.MemoryUsageBytes),
                PeakMem = g.Max(m => m.MemoryUsageBytes),
                MemLimit = g.Max(m => m.MemoryLimitBytes)
            })
            .ToListAsync();

        return metrics.Select(m =>
        {
            var rec = new ResourceRecommendation
            {
                ContainerId = m.ContainerId,
                ContainerName = m.ContainerName,
                AvgCpuPercent = m.AvgCpu,
                PeakCpuPercent = m.PeakCpu,
                AvgMemoryBytes = m.AvgMem,
                PeakMemoryBytes = m.PeakMem,
                CurrentMemoryLimit = m.MemLimit
            };

            // Recommendation: 2x peak usage, rounded up to nearest 128MB
            var recommended = (long)(m.PeakMem * 2.0);
            recommended = ((recommended / (128 * 1024 * 1024)) + 1) * (128 * 1024 * 1024);
            rec.RecommendedMemoryBytes = Math.Max(recommended, 128 * 1024 * 1024); // Min 128MB

            if (m.MemLimit <= 0)
            {
                rec.Verdict = "no-limit";
                rec.Hint = $"Kein Memory-Limit gesetzt. Empfehlung: {FormatBytes(rec.RecommendedMemoryBytes)}";
            }
            else if (m.PeakMem > m.MemLimit * 0.85)
            {
                rec.Verdict = "under-provisioned";
                rec.Hint = $"Peak-Nutzung ({FormatBytes(m.PeakMem)}) nah am Limit ({FormatBytes(m.MemLimit)}). Empfehlung: {FormatBytes(rec.RecommendedMemoryBytes)}";
            }
            else if (m.AvgMem < m.MemLimit * 0.2 && m.MemLimit > 512 * 1024 * 1024)
            {
                rec.Verdict = "over-provisioned";
                rec.Hint = $"Durchschnitt ({FormatBytes(m.AvgMem)}) weit unter Limit ({FormatBytes(m.MemLimit)}). Empfehlung: {FormatBytes(rec.RecommendedMemoryBytes)}";
            }
            else
            {
                rec.Verdict = "optimal";
                rec.Hint = "Ressourcen-Nutzung im optimalen Bereich.";
            }

            return rec;
        }).OrderBy(r => r.Verdict == "optimal" ? 2 : r.Verdict == "no-limit" ? 1 : 0).ToList();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1073741824 => $"{bytes / 1073741824.0:F1} GB",
        >= 1048576 => $"{bytes / 1048576.0:F0} MB",
        _ => $"{bytes / 1024.0:F0} KB"
    };
}

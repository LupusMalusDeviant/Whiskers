using Microsoft.EntityFrameworkCore;
using ServerWatch.Models.Cve;
using ServerWatch.Services.Persistence;

namespace ServerWatch.Services.Cve;

/// <summary>Tracks when each vulnerability instance was FIRST seen, persisted in SQLite so the
/// "open for N days" age survives restarts and scan cycles. Keyed by the finding's IdentityKey.</summary>
public interface ICveAgeStore
{
    /// <summary>Records first-seen = now for any identity key not seen before (idempotent insert).</summary>
    Task RecordSeenAsync(IEnumerable<(string IdentityKey, string CveId)> current, CancellationToken ct = default);

    /// <summary>IdentityKey → first-seen timestamp, for computing age.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetFirstSeenAsync(CancellationToken ct = default);
}

public sealed class CveAgeStore : ICveAgeStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CveAgeStore>? _logger;

    public CveAgeStore(IServiceScopeFactory scopeFactory, ILogger<CveAgeStore>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecordSeenAsync(IEnumerable<(string IdentityKey, string CveId)> current, CancellationToken ct = default)
    {
        var items = current
            .Where(c => !string.IsNullOrEmpty(c.IdentityKey))
            .GroupBy(c => c.IdentityKey)
            .Select(g => g.First())
            .ToList();
        if (items.Count == 0) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

            var keys = items.Select(i => i.IdentityKey).ToHashSet();
            var existing = await db.CveFirstSeen
                .Where(e => keys.Contains(e.IdentityKey))
                .Select(e => e.IdentityKey)
                .ToListAsync(ct);
            var existingSet = existing.ToHashSet();

            var now = DateTime.UtcNow;
            var fresh = items
                .Where(i => !existingSet.Contains(i.IdentityKey))
                .Select(i => new CveFirstSeenEntity { IdentityKey = i.IdentityKey, CveId = i.CveId, FirstSeenUtc = now })
                .ToList();
            if (fresh.Count == 0) return;

            db.CveFirstSeen.AddRange(fresh);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to record CVE first-seen timestamps");
        }
    }

    public async Task<IReadOnlyDictionary<string, DateTime>> GetFirstSeenAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            return await db.CveFirstSeen.ToDictionaryAsync(e => e.IdentityKey, e => e.FirstSeenUtc, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read CVE first-seen timestamps");
            return new Dictionary<string, DateTime>();
        }
    }
}

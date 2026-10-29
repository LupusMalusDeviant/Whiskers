using Microsoft.EntityFrameworkCore;
using Whiskers.Models;
using Whiskers.Services.Persistence;

namespace Whiskers.Services.Webhooks;

/// <summary>How the endpoint answers a delivery (R1-10). Every invalid request gets the same answer.</summary>
public enum WebhookReply
{
    /// <summary>Queued: <see cref="WebhookAcceptance.RunId"/> names the run.</summary>
    Accepted,
    /// <summary>This delivery was accepted before: its run and that run's state, nothing runs again.</summary>
    Duplicate,
    /// <summary>Not a valid delivery (unknown or disabled webhook, bad signature, stale, too large) — no detail given.</summary>
    Rejected,
    /// <summary>Too many requests this minute.</summary>
    RateLimited,
    /// <summary>The queue is full; nothing was recorded, the provider may deliver it again.</summary>
    Busy,
}

/// <summary>The answer to a delivery. <see cref="State"/> is the run's state: queued, running, succeeded, failed,
/// refused, or interrupted (it was running when Whiskers stopped; whether it took effect is unknown).</summary>
public sealed record WebhookAcceptance(WebhookReply Reply, string? RunId = null, string? State = null);

/// <summary>
/// The accepted deliveries of all webhooks (table <c>WebhookDeliveries</c>, R1-10, INV-42). A delivery key is
/// recorded once: a concurrent or later delivery with the same key — also after a restart — finds the run of the
/// first. A delivery that was still queued when Whiskers stopped never ran: the next delivery with its key takes it
/// over. One that was running stays "interrupted" and does not run again.
/// </summary>
internal sealed class WebhookDeliveryStore
{
    internal const string Queued = "Queued", Running = "Running", Succeeded = "Succeeded", Failed = "Failed", Refused = "Refused";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    /// <summary>This process: a Queued or Running row of another one was cut off by a restart.</summary>
    internal string ProcessInstance { get; } = Guid.NewGuid().ToString("N");

    public WebhookDeliveryStore(IServiceScopeFactory scopes, TimeProvider time)
    {
        _scopes = scopes;
        _time = time;
    }

    /// <summary>Records the delivery. <c>Fresh</c> when this call must run it (recorded now, or taken over from a run
    /// a restart dropped before it started); otherwise the row of the run it repeats.</summary>
    public async Task<(bool Fresh, WebhookDeliveryEntity Row)> RecordAsync(string webhookId, string deliveryKey, CancellationToken ct)
    {
        // This call's token: the process and the call — a retried insert whose commit answer was lost knows its row.
        var token = $"{ProcessInstance}/{Guid.NewGuid():N}";
        var now = _time.GetUtcNow().UtcDateTime;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var row = new WebhookDeliveryEntity
        {
            WebhookId = webhookId,
            DeliveryKey = deliveryKey,
            RunId = Guid.NewGuid().ToString("N"),
            State = Queued,
            Instance = token,
            ReceivedAt = now,
        };
        try
        {
            db.WebhookDeliveries.Add(row);
            await db.SaveChangesAsync(ct);
            return (true, row);
        }
        catch (DbUpdateException)
        {
            // The key is known (or the write failed — then the read below fails too and the delivery is refused).
            db.ChangeTracker.Clear();
        }

        var existing = await db.WebhookDeliveries.AsNoTracking()
                           .SingleOrDefaultAsync(d => d.WebhookId == webhookId && d.DeliveryKey == deliveryKey, ct)
                       ?? throw new InvalidOperationException("The delivery could not be recorded.");
        if (existing.Instance == token) return (true, existing);
        if (existing.State != Queued || Mine(existing)) return (false, existing);

        // Queued by a process that stopped before it ran it: exactly one later delivery takes it over.
        var taken = await db.WebhookDeliveries
            .Where(d => d.Id == existing.Id && d.State == Queued && d.Instance == existing.Instance)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Instance, token).SetProperty(d => d.ReceivedAt, now), ct);
        existing.Instance = token;
        return (taken == 1, existing);
    }

    /// <summary>Starts the run: only a run this process queued, and only once.</summary>
    public async Task<bool> StartAsync(string runId)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.WebhookDeliveries
            .Where(d => d.RunId == runId && d.State == Queued && d.Instance.StartsWith(ProcessInstance + "/"))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, Running)) == 1;
    }

    /// <summary>Records how the run ended. A failed write leaves it Running: after a restart it reads as interrupted.</summary>
    public async Task FinishAsync(string runId, string state)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        await db.WebhookDeliveries
            .Where(d => d.RunId == runId && d.State == Running)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, state).SetProperty(d => d.FinishedAt, now));
    }

    /// <summary>The delivery recorded under this key, or <c>null</c>; records nothing.</summary>
    public async Task<WebhookDeliveryEntity?> FindAsync(string webhookId, string deliveryKey, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().WebhookDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(d => d.WebhookId == webhookId && d.DeliveryKey == deliveryKey, ct);
    }

    /// <summary>Removes finished deliveries received before <paramref name="before"/>. A queued or running one stays.</summary>
    public async Task PruneAsync(DateTime before)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        await db.WebhookDeliveries
            .Where(d => d.ReceivedAt < before && d.State != Queued && d.State != Running)
            .ExecuteDeleteAsync();
    }

    /// <summary>The run's state for the caller; a run another process was executing is "interrupted".</summary>
    public string Describe(WebhookDeliveryEntity row) => row.State switch
    {
        Running when !Mine(row) => "interrupted",
        _ => row.State.ToLowerInvariant(),
    };

    private bool Mine(WebhookDeliveryEntity row) =>
        row.Instance.StartsWith(ProcessInstance + "/", StringComparison.Ordinal);
}

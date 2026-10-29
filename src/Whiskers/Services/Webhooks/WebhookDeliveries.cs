using System.Collections.Concurrent;
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
/// first. Each row names the claim (<see cref="WebhookDeliveryEntity.Instance"/>) that queued it; a claim is live
/// while this process holds its run. A queued row whose claim is not live never ran and nothing will run it — its
/// process stopped, its worker gave it up, or the answer to its insert was lost — so the next delivery with its key
/// takes it over. One that was running stays "interrupted" and does not run again.
/// </summary>
internal sealed class WebhookDeliveryStore
{
    internal const string Queued = "Queued", Running = "Running", Succeeded = "Succeeded", Failed = "Failed", Refused = "Refused";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    /// <summary>This process, as the prefix of its claims (for the log; liveness is <see cref="_live"/>).</summary>
    internal string ProcessInstance { get; } = Guid.NewGuid().ToString("N");

    /// <summary>The claims whose runs this process holds: recorded and queued, running, or being recorded.</summary>
    private readonly ConcurrentDictionary<string, byte> _live = new(StringComparer.Ordinal);

    public WebhookDeliveryStore(IServiceScopeFactory scopes, TimeProvider time)
    {
        _scopes = scopes;
        _time = time;
    }

    /// <summary>Records the delivery. <c>Fresh</c> when this call must run it (recorded now, or taken over from a run
    /// nothing holds); then the row's claim is live until <see cref="Release"/>. Otherwise the row of the run it
    /// repeats. A call that fails leaves no live claim: a row it may have written is taken over by the next delivery.</summary>
    public async Task<(bool Fresh, WebhookDeliveryEntity Row)> RecordAsync(string webhookId, string deliveryKey, CancellationToken ct)
    {
        // This call's claim, live before the row exists: a concurrent duplicate never sees it unclaimed. A retried
        // insert whose commit answer was lost knows its row by it.
        var token = $"{ProcessInstance}/{Guid.NewGuid():N}";
        _live[token] = 0;
        var fresh = false;
        try
        {
            var recorded = await RecordAsync(webhookId, deliveryKey, token, ct);
            fresh = recorded.Fresh;
            return recorded;
        }
        finally
        {
            if (!fresh) Release(token);
        }
    }

    private async Task<(bool Fresh, WebhookDeliveryEntity Row)> RecordAsync(string webhookId, string deliveryKey, string token,
        CancellationToken ct)
    {
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
        if (!Orphaned(existing)) return (false, existing);

        // Queued, but nothing holds it: exactly one later delivery takes it over.
        var taken = await db.WebhookDeliveries
            .Where(d => d.Id == existing.Id && d.State == Queued && d.Instance == existing.Instance)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Instance, token).SetProperty(d => d.ReceivedAt, now), ct);
        existing.Instance = token;
        return (taken == 1, existing);
    }

    /// <summary>Starts the run: only under the claim that queued it (not one a later delivery took over), and only once.</summary>
    public async Task<bool> StartAsync(string runId, string claim, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.WebhookDeliveries
            .Where(d => d.RunId == runId && d.State == Queued && d.Instance == claim)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, Running), ct) == 1;
    }

    /// <summary>Records how the run ended. A failed write leaves it Running: once its claim is released it reads as
    /// interrupted.</summary>
    public async Task FinishAsync(string runId, string state, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        await db.WebhookDeliveries
            .Where(d => d.RunId == runId && d.State == Running)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, state).SetProperty(d => d.FinishedAt, now), ct);
    }

    /// <summary>The run's claim is no longer held: a queued row of it is taken over by the next delivery.</summary>
    public void Release(string claim) => _live.TryRemove(claim, out _);

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

    /// <summary>The run's state for the caller; a run nothing holds any more (its process stopped, or its worker gave
    /// it up) is "interrupted".</summary>
    public string Describe(WebhookDeliveryEntity row) => row.State switch
    {
        Running when !Live(row) => "interrupted",
        _ => row.State.ToLowerInvariant(),
    };

    /// <summary>Queued, but nothing holds it: nothing runs it until a delivery with its key takes it over.</summary>
    public bool Orphaned(WebhookDeliveryEntity row) => row.State == Queued && !Live(row);

    private bool Live(WebhookDeliveryEntity row) => _live.ContainsKey(row.Instance);
}

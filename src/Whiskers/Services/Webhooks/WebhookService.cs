using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Localization;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Whiskers.Models;
using Whiskers.Services.Docker;
using Whiskers.Services.GitDeploy;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;
using Whiskers.Services.Server;
using Whiskers.Utils;

namespace Whiskers.Services.Webhooks;

public class WebhookService : IWebhookService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDockerService _docker;
    private readonly IHostCommandExecutor _executor;
    private readonly IComposePlanner _planner;
    private readonly ILogger<WebhookService> _logger;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _time;
    private readonly WebhookDeliveryStore _deliveries;
    private readonly CancellationToken _stopping;
    private readonly IStringLocalizer<SharedResource>? _text;

    // R1-10 (INV-42): accepted deliveries wait here for one of a bounded number of workers; runs on the same resource
    // never overlap. A queue slot is reserved before a delivery is recorded, so an accepted delivery always runs. The
    // workers start with the first delivery.
    private readonly Channel<QueuedRun> _queue = Channel.CreateUnbounded<QueuedRun>();
    private readonly SemaphoreSlim _slots;
    // Runs whose action outlived the run timeout: they keep their target, not a worker. Bounded too.
    private readonly SemaphoreSlim _overdue;
    private readonly ResourceLocks _locks;
    // R1-22: one configuration writer at a time (create, rotate, enable, takeover, delete, automatic switch-off), so
    // each recorded change starts where the one before ended.
    private readonly SemaphoreSlim _config = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RunEnd>> _waiting = new(StringComparer.Ordinal);
    private readonly RateWindow _rates;
    private int _workersStarted;
    private long _lastPrune;

    /// <summary>How many resources this service's runs lock or await now.</summary>
    internal int LockedResources => _locks.Count;

    private sealed record QueuedRun(string WebhookId, string RunId, string Claim, string? SourceIp);

    /// <summary>The time a run's own bookkeeping (its state and log row) gets once its deadline has passed.</summary>
    private static readonly TimeSpan BookkeepingTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Request-rate buckets per webhook id: the id as sent, valid or not, so the limit says nothing about
    /// whether a webhook exists — and the table stays bounded whatever ids are sent.</summary>
    private const int RateBuckets = 1024;

    public WebhookService(IServiceScopeFactory scopeFactory, IDockerService docker,
        IHostCommandExecutor executor, IComposePlanner planner, ILogger<WebhookService> logger,
        IOptions<WebhookOptions>? options = null, TimeProvider? time = null, IHostApplicationLifetime? lifetime = null,
        IStringLocalizer<SharedResource>? text = null, ResourceLocks? locks = null)
    {
        _locks = locks ?? new ResourceLocks();
        _scopeFactory = scopeFactory;
        _docker = docker;
        _executor = executor;
        _planner = planner;
        _logger = logger;
        _options = options?.Value ?? new WebhookOptions();
        _time = time ?? TimeProvider.System;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
        _text = text;
        _deliveries = new WebhookDeliveryStore(scopeFactory, _time);
        _slots = new SemaphoreSlim(Math.Max(1, _options.QueueCapacity));
        _overdue = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRuns));
        _rates = new RateWindow(_time);
    }

    public async Task<List<WebhookEntity>> GetWebhooksAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.Webhooks.OrderBy(w => w.Name).ToListAsync();
    }

    public async Task<WebhookEntity?> GetWebhookAsync(string webhookId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        return await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.WebhookId == webhookId);
    }

    public async Task<bool> SetOwnerAsync(string webhookId, string ownerId, string? expectedOwnerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await _config.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var before = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.WebhookId == webhookId);
            // Compare-and-set in one statement: a takeover by someone else in between wins.
            var changed = await db.Webhooks
                .Where(w => w.WebhookId == webhookId && w.Owner == expectedOwnerId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Owner, ownerId));
            if (changed != 1) return false;
            // The versioned, redacted change is part of the gateway's event (R1-22), on the whole webhook like its other
            // lifecycle steps; read under the configuration lock, so no other change lies between before and after.
            if (before is not null)
            {
                var from = AuditView(before);
                before.Owner = ownerId;
                Whiskers.Services.Mutations.ConfigChange.Record($"webhook:{webhookId}", from, AuditView(before));
            }
            return true;
        }
        finally { _config.Release(); }
    }

    /// <summary>What the audit compares and versions for a webhook (R1-22): its configuration, not its trigger
    /// counters, which change with every delivery.</summary>
    private static object? AuditView(WebhookEntity? webhook)
    {
        if (webhook is null) return null;
        var node = System.Text.Json.JsonSerializer.SerializeToNode(webhook)!.AsObject();
        node.Remove(nameof(WebhookEntity.LastTriggered));
        node.Remove(nameof(WebhookEntity.TriggerCount));
        return System.Text.Json.JsonSerializer.SerializeToElement(node);
    }

    public async Task<WebhookEntity> CreateWebhookAsync(WebhookEntity webhook)
    {
        // Secrets are mandatory (HOCH-12 part 2): without one the trigger URL alone would be enough
        // to fire host actions. Generate server-side so no caller can create a secret-less webhook.
        if (string.IsNullOrWhiteSpace(webhook.Secret))
            webhook.Secret = GenerateSecret();
        // R1-10: only a provider with a known signature rule — any other could never be delivered to.
        if (!WebhookSignatures.Providers.Contains(webhook.Provider))
            throw new ArgumentException($"Unknown signature scheme '{webhook.Provider}'.", nameof(webhook));

        await _config.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.Webhooks.Add(webhook);
            await db.SaveChangesAsync();
            Whiskers.Services.Mutations.ConfigChange.Record($"webhook:{webhook.WebhookId}", null, AuditView(webhook));   // R1-22
            return webhook;
        }
        finally { _config.Release(); }
    }

    public async Task<string> RegenerateSecretAsync(string webhookId)
    {
        await _config.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId)
                ?? throw new InvalidOperationException("Webhook not found");
            var before = AuditView(webhook);
            webhook.Secret = GenerateSecret();
            await db.SaveChangesAsync();
            Whiskers.Services.Mutations.ConfigChange.Record($"webhook:{webhookId}", before, AuditView(webhook));   // R1-22
            return webhook.Secret;
        }
        finally { _config.Release(); }
    }

    public async Task SetEnabledAsync(string webhookId, bool enabled)
    {
        await _config.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId)
                ?? throw new InvalidOperationException("Webhook not found");
            // A legacy webhook without a secret must not come back to life unauthenticated —
            // force a secret regeneration first.
            if (enabled && string.IsNullOrEmpty(webhook.Secret))
                throw new InvalidOperationException("Webhook has no secret — regenerate its secret before enabling it.");
            var before = AuditView(webhook);
            webhook.Enabled = enabled;
            await db.SaveChangesAsync();
            Whiskers.Services.Mutations.ConfigChange.Record($"webhook:{webhookId}", before, AuditView(webhook));   // R1-22
        }
        finally { _config.Release(); }
    }

    public async Task DeleteWebhookAsync(string webhookId)
    {
        await _config.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId);
            if (webhook != null)
            {
                var before = AuditView(webhook);
                db.Webhooks.Remove(webhook);
                await db.SaveChangesAsync();
                Whiskers.Services.Mutations.ConfigChange.Record($"webhook:{webhookId}", before, null);   // R1-22
            }
        }
        finally { _config.Release(); }
    }

    public WebhookReply? Admit(string webhookId)
    {
        // Before the body is read and before anything is looked up: an unauthenticated caller spends at most this much,
        // and the per-id limit treats an unknown id like a known one.
        if (!_rates.TryTake("*", _options.RequestsPerMinute)) return WebhookReply.RateLimited;
        var bucket = (uint)StringComparer.Ordinal.GetHashCode(webhookId ?? "") % RateBuckets;
        return _rates.TryTake("id:" + bucket, _options.RequestsPerMinutePerWebhook) ? null : WebhookReply.RateLimited;
    }

    public async Task<WebhookAcceptance> AcceptAsync(string webhookId, WebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(webhookId) || webhookId.Length > 64 || request.Body.Length > _options.MaxBodyBytes)
            return Reject(webhookId, "the request is malformed or too large");

        WebhookEntity? webhook;
        using (var scope = _scopeFactory.CreateScope())
            webhook = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Webhooks.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WebhookId == webhookId, ct);
        if (webhook is null) return Reject(webhookId, "unknown webhook");
        if (!webhook.Enabled) return Reject(webhookId, "the webhook is disabled");

        // Signature and freshness by the webhook's provider; the key identifies the delivery (signed, never metadata).
        var (key, problem) = WebhookSignatures.Verify(webhook, request, _time.GetUtcNow(), _options.TimestampTolerance);
        if (key is null) return Reject(webhookId, problem!);
        return await QueueAsync(webhook, key, request.SourceIp, null, ct);
    }

    /// <summary>Records the delivery and queues its run, or answers with the run it repeats. A queue slot is taken
    /// first: a delivery is recorded only when it can run, so every answer that names a queued run is kept.</summary>
    private async Task<WebhookAcceptance> QueueAsync(WebhookEntity webhook, string key, string? sourceIp,
        TaskCompletionSource<RunEnd>? completion, CancellationToken ct)
    {
        if (!_slots.Wait(0))
        {
            // Full. A delivery seen before still gets its run; a new one is not recorded — the provider may retry.
            // One a stopped process left queued runs only if a delivery takes it over, which needs a slot: busy too.
            var known = await _deliveries.FindAsync(webhook.WebhookId, key, ct);
            if (known is not null && !_deliveries.Orphaned(known) && !_deliveries.Admitting(known))
                return Repeat(webhook, known);
            _logger.LogWarning("Webhook {Name}: the delivery queue is full; refused as busy", webhook.Name);
            return new WebhookAcceptance(WebhookReply.Busy);
        }

        bool fresh;
        WebhookDeliveryEntity row;
        try
        {
            (fresh, row) = await _deliveries.RecordAsync(webhook.WebhookId, key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _slots.Release();
            // Nothing recorded (or not knowably): nothing runs, and the provider may deliver it again.
            _logger.LogError(ex, "Webhook {Name}: the delivery could not be recorded; refused as busy", webhook.Name);
            return new WebhookAcceptance(WebhookReply.Busy);
        }
        catch
        {
            _slots.Release();
            throw;
        }
        if (!fresh)
        {
            _slots.Release();
            // Still being recorded by a concurrent delivery that may yet fail, or queued with nothing holding it (a
            // concurrent takeover that won and then failed): not "queued" — busy, and the next delivery takes it over.
            if (_deliveries.Admitting(row) || _deliveries.Orphaned(row))
            {
                _logger.LogInformation("Webhook {Name}: a repeat of a delivery still being recorded; refused as busy", webhook.Name);
                return new WebhookAcceptance(WebhookReply.Busy);
            }
            return Repeat(webhook, row);
        }

        if (completion is not null) _waiting[row.RunId] = completion;
        EnsureWorkers();
        // Unbounded channel, bounded by the slot taken above: the write cannot fail.
        _queue.Writer.TryWrite(new QueuedRun(webhook.WebhookId, row.RunId, row.Instance, sourceIp));
        _deliveries.Enqueued(row.Instance);
        _logger.LogInformation("Webhook {Name}: delivery accepted as run {RunId}", webhook.Name, row.RunId);
        return new WebhookAcceptance(WebhookReply.Accepted, row.RunId, "queued");
    }

    private WebhookAcceptance Repeat(WebhookEntity webhook, WebhookDeliveryEntity row)
    {
        _logger.LogInformation("Webhook {Name}: delivery repeats run {RunId}; nothing runs again", webhook.Name, row.RunId);
        return new WebhookAcceptance(WebhookReply.Duplicate, row.RunId, _deliveries.Describe(row));
    }

    private WebhookAcceptance Reject(string? webhookId, string problem)
    {
        // The reason stays in the log; the caller learns nothing about which check failed.
        _logger.LogWarning("Webhook delivery for {WebhookId} rejected: {Problem}",
            webhookId is { Length: <= 64 } ? webhookId : "(invalid)", problem);
        return new WebhookAcceptance(WebhookReply.Rejected);
    }

    private void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref _workersStarted, 1) == 1) return;
        // Not the caller's execution context: a worker serves every later delivery, so nothing of the first caller —
        // a mutation scope, a culture, a user — may travel with it.
        using (ExecutionContext.SuppressFlow())
            for (var i = 0; i < Math.Max(1, _options.MaxConcurrentRuns); i++)
                _ = Task.Run(WorkAsync);
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var run in _queue.Reader.ReadAllAsync(_stopping))
            {
                _slots.Release();
                try
                {
                    await ExecuteRunAsync(run);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Webhook run {RunId} failed outside its action", run.RunId);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Shutting down: a queued run of this process is taken over by its next delivery after the restart.
        }
    }

    /// <summary>
    /// Runs one accepted delivery: once, never at the same time as another run on the same resource, within
    /// <see cref="WebhookOptions.RunTimeout"/>. The webhook is read again once the run has its target, so a webhook
    /// switched off, removed or taken over while the delivery waited is refused, or runs on its owner now.
    /// </summary>
    private async Task ExecuteRunAsync(QueuedRun run)
    {
        _waiting.TryRemove(run.RunId, out var completion);
        RunMessage? message = new("Webhook_Run_NotRun", "The delivery did not run.", []);
        (bool Success, string Output) result = (false, message.English);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        deadline.CancelAfter(_options.RunTimeout);
        var ct = deadline.Token;
        ResourceLocks.Lease? gate = null;
        var handedOver = false;
        try
        {
            var queued = await ReadWebhookAsync(run.WebhookId, ct);
            var target = queued is null ? new RunTarget("missing:" + run.WebhookId) : await TargetAsync(queued, ct);
            while (true)
            {
                try
                {
                    gate = await _locks.AcquireAsync(target.Key, ct);
                }
                catch (OperationCanceledException)
                {
                    message = new("Webhook_Run_NoTurn", "Failed: the delivery did not get its turn on its target in time.", []);
                    result = (false, message.English);
                    var noTurn = result;
                    using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
                    await Bounded(async t =>
                    {
                        if (await _deliveries.StartAsync(run.RunId, run.Claim, t))
                            await FinishAsync(run, queued, noTurn, WebhookDeliveryStore.Failed);
                        return true;
                    }, bookkeeping.Token);
                    return;
                }
                // What the run acts on is resolved again while its lock is held — a run before may have rebuilt the
                // container under a new id, or changed the compose files — and the action gets exactly that. A
                // target whose lock is another now (another server) waits for that lock instead.
                if (queued is null || !target.Resolved) break;
                var now = await TargetAsync(queued, ct);
                if (now.Key == target.Key)
                {
                    target = now;
                    break;
                }
                gate.Dispose();
                gate = null;
                target = now;
            }

            // The work this run starts holds its lock: a Git deployment it starts does not wait for its own server.
            _locks.Holding(target.Key);
            if (!await StartAsync(run, ct)) return;
            var webhook = await ReadWebhookAsync(run.WebhookId, ct);
            if (webhook is null || !webhook.Enabled || queued is null || !SameTarget(queued, webhook))
            {
                // Removed, switched off or pointed elsewhere while it waited: it does not run.
                message = new("Webhook_Run_Changed",
                    "Refused: the webhook was removed, switched off or changed before the delivery ran.", []);
                result = await FinishAsync(run, webhook, (false, message.English), WebhookDeliveryStore.Refused);
                return;
            }

            _logger.LogInformation("Webhook triggered: {Name} ({Action} on {Target}), run {RunId}",
                webhook.Name, webhook.Action, webhook.TargetId, run.RunId);
            // Started on the thread pool: the owner's authority and the gateway may do synchronous work (an
            // account lookup in SQLite) before they return a task — the deadline must already be watching then.
            var work = Task.Run(() => RunAsOwnerAsync(webhook, run.RunId, target, ct), CancellationToken.None);
            if (!await Finished(work, ct))
            {
                if (!_overdue.Wait(0))
                {
                    // No overdue slot free: the action keeps this worker as well as its target until it ends. Its
                    // target is never released while it runs.
                    _logger.LogWarning("Webhook run {RunId} exceeded its time limit; it keeps its worker until it ends", run.RunId);
                    try
                    {
                        await work;
                    }
                    catch
                    {
                        // Its failure is its outcome, recorded below.
                    }
                }
                else
                {
                    // The action outlived the run timeout and does not stop: it keeps its target until it ends — no other
                    // run may overlap it — but no longer a worker.
                    handedOver = true;
                    var held = gate;
                    message = new("Webhook_Run_Overdue",
                        "Run {0} exceeded its time limit and is still running; its outcome will be in the log.", [run.RunId]);
                    result = (false, message.English);
                    _ = work.ContinueWith(async done =>
                    {
                        try
                        {
                            await FinishAsync(run, webhook, Outcome(done), StateOf(done));
                        }
                        finally
                        {
                            held.Dispose();
                            _deliveries.Release(run.Claim);
                            _overdue.Release();
                        }
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                    return;
                }
            }
            message = work.IsCanceled ? new RunMessage("Webhook_Run_Cancelled", Cancelled, []) : null;
            result = await FinishAsync(run, webhook, Outcome(work), StateOf(work));
        }
        catch (Exception ex) when (!handedOver)
        {
            // Given up: running, it failed; queued, the next delivery takes it over once the claim is released
            // below. Recorded beside the worker — the database may be what failed — and fenced by the claim.
            _logger.LogError(ex, "Webhook run {RunId} failed outside its action", run.RunId);
            _ = Task.Run(() => AbandonAsync(run));
        }
        finally
        {
            if (!handedOver)
            {
                gate?.Dispose();
                _deliveries.Release(run.Claim);
            }
            completion?.TrySetResult(new RunEnd(result.Success, result.Output, message));
            // Not on the worker: pruning has its own time and never holds a run's worker (SQLite runs its commands
            // synchronously, so it is started on the thread pool, not just left unawaited).
            _ = Task.Run(PruneIfDueAsync);
        }
    }

    /// <summary>How a run ended, for a caller waiting on it: <see cref="Message"/> is set when the worker itself
    /// wrote the output, so the page's Test can show it in the person's language.</summary>
    private sealed record RunEnd(bool Success, string Output, RunMessage? Message);

    /// <summary>A worker's own message: English for the log, its resource key for the page.</summary>
    private sealed record RunMessage(string Key, string Format, object[] Args)
    {
        public string English => string.Format(CultureInfo.InvariantCulture, Format, Args);
    }

    /// <summary>What a run locks and acts on: <see cref="Key"/> names the resource; for a container action
    /// <see cref="Container"/>, for a compose deployment <see cref="Plan"/>, for a Git delivery its app's
    /// <see cref="Server"/> at <see cref="Revision"/>, is what the key was made from (or <see cref="Error"/> says why
    /// there is none).</summary>
    private sealed record RunTarget(string Key, ContainerInfo? Container = null, ComposePlan? Plan = null,
        string? Error = null, string? Server = null, int? Revision = null)
    {
        /// <summary>Resolved from what the name points to now — which can change (a rebuild, an edited compose file,
        /// an app moved to another server), so it is checked again under its lock.</summary>
        public bool Resolved => Container is not null || Plan is not null || Server is not null;
    }

    /// <summary>Whether the action ended before the run's deadline.</summary>
    private async Task<bool> Finished(Task work, CancellationToken deadline)
    {
        try
        {
            await work.WaitAsync(deadline);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !work.IsCompleted)
        {
            return false;
        }
        catch
        {
            // The action's own failure: it finished.
        }
        return true;
    }

    private const string Cancelled = "The action was cancelled.";

    private static (bool Success, string Output) Outcome(Task<(bool Success, string Output, bool Refused)> work) =>
        work.IsCompletedSuccessfully
            ? (work.Result.Success, work.Result.Output)
            : (false, work.Exception?.GetBaseException().Message ?? Cancelled);

    /// <summary>Starts the run within its deadline. The database may not answer in time (SQLite runs its commands
    /// synchronously and does not cancel them): the worker goes on, and a start that still lands later is recorded
    /// as failed — the run never runs.</summary>
    private async Task<bool> StartAsync(QueuedRun run, CancellationToken ct)
    {
        var start = Task.Run(() => _deliveries.StartAsync(run.RunId, run.Claim, ct), CancellationToken.None);
        try
        {
            return await start.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !start.IsCompleted)
        {
            _ = start.ContinueWith(t => t.IsCompletedSuccessfully && t.Result ? AbandonAsync(run) : Task.CompletedTask,
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            throw;
        }
    }

    /// <summary>
    /// Database work of a run, on the thread pool and waited for at most until <paramref name="ct"/>: SQLite runs its
    /// commands synchronously and does not cancel them, so awaiting them directly could hold a worker past its
    /// deadline. Work given up goes on in the background; its failure is logged. Claims fence it: what it writes
    /// applies only to the run it was started for.
    /// </summary>
    private Task<T> Bounded<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var task = Task.Run(() => work(ct), CancellationToken.None);
        _ = task.ContinueWith(t => _logger.LogWarning(t.Exception, "Webhook database work given up failed"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return task.WaitAsync(ct);
    }

    /// <summary>A run its worker gave up: running, it failed (a queued one is taken over by its next delivery).</summary>
    private async Task AbandonAsync(QueuedRun run)
    {
        try
        {
            using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
            await Bounded(async t =>
            {
                await _deliveries.FinishAsync(run.RunId, run.Claim, WebhookDeliveryStore.Failed, t);
                return true;
            }, bookkeeping.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook run {RunId}: its failure could not be recorded", run.RunId);
        }
    }

    private static string StateOf(Task<(bool Success, string Output, bool Refused)> work) =>
        !work.IsCompletedSuccessfully ? WebhookDeliveryStore.Failed
        : work.Result.Refused ? WebhookDeliveryStore.Refused
        : work.Result.Success ? WebhookDeliveryStore.Succeeded
        : WebhookDeliveryStore.Failed;

    /// <summary>Records how the run ended: the delivery's state, the webhook's log and counters — within its own
    /// time, also once the run's deadline has passed.</summary>
    private async Task<(bool, string)> FinishAsync(QueuedRun run, WebhookEntity? webhook, (bool Success, string Output) result,
        string state)
    {
        using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
        try
        {
            return await Bounded(ct => RecordOutcomeAsync(run, webhook, result, state, ct), bookkeeping.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Webhook run {RunId}: its outcome could not be recorded in time", run.RunId);
            return result;
        }
    }

    private async Task<(bool, string)> RecordOutcomeAsync(QueuedRun run, WebhookEntity? webhook,
        (bool Success, string Output) result, string state, CancellationToken ct)
    {
        try
        {
            await _deliveries.FinishAsync(run.RunId, run.Claim, state, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook run {RunId}: its outcome could not be recorded", run.RunId);
        }
        if (webhook is null) return result;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            await db.Webhooks.Where(w => w.WebhookId == webhook.WebhookId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(w => w.LastTriggered, _time.GetUtcNow().UtcDateTime)
                    .SetProperty(w => w.TriggerCount, w => w.TriggerCount + 1), ct);
            db.WebhookLogs.Add(new WebhookLogEntity
            {
                WebhookId = webhook.WebhookId,
                WebhookName = webhook.Name,
                Success = result.Success,
                Output = result.Success ? result.Output : null,
                Error = result.Success ? null : result.Output,
                SourceIp = run.SourceIp,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook run {RunId}: its log entry could not be written", run.RunId);
        }
        return result;
    }

    private Task<WebhookEntity?> ReadWebhookAsync(string webhookId, CancellationToken ct) => Bounded(async t =>
    {
        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Webhooks.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WebhookId == webhookId, t);
    }, ct);

    private static bool SameTarget(WebhookEntity queued, WebhookEntity now) =>
        queued.Action == now.Action && queued.TargetId == now.TargetId && queued.ServerId == now.ServerId;

    /// <summary>
    /// The resource a run locks. A container action and a compose deployment hold their server (one at a time per
    /// server): a rebuild starts the replacement under a new id and renames the old container before it ends, and a
    /// deployment's files can lie anywhere (a symlinked directory, a compose file elsewhere, a Git checkout), so no
    /// name, id or path is one resource throughout — the server is. A Git delivery holds its app's server too, before
    /// its webhook is read again (the deployment it starts then does not wait for it again). A server is locked by its
    /// id, whichever alias is stored. A container or compose action acts on exactly the container or plan resolved
    /// under the lock; one that cannot be resolved is refused.
    /// </summary>
    private async Task<RunTarget> TargetAsync(WebhookEntity webhook, CancellationToken ct)
    {
        var server = _locks.ServerKey(WebhookTargets.ServerOf(webhook));
        switch (webhook.Action)
        {
            case "git-deploy":
            {
                // An app that cannot be read is held by its id: its deployment refuses it.
                var (app, _) = await LookupAsync(webhook, async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var git = scope.ServiceProvider.GetService<IGitDeployService>();
                    return (git is null ? null : await git.GetAppAsync(webhook.TargetId), (string?)null);
                }, ct);
                return new RunTarget(app is null ? "git:" + webhook.TargetId : _locks.ServerKey(app.ServerId),
                    Server: app?.ServerId, Revision: app?.Revision);
            }
            case "deploy":
            {
                var (plan, error) = await LookupAsync(webhook, () => PlanAsync(webhook, ct), ct);
                return new RunTarget(server, Plan: plan, Error: plan is null ? error : null);
            }
            case "restart" or "rebuild":
            {
                var (container, error) = await LookupAsync(webhook, () => WebhookTargets.ContainerAsync(_docker, webhook, ct), ct);
                return container is not null
                    ? new RunTarget(server, container)
                    : new RunTarget($"container:{server}|{webhook.TargetId}", Error: error);
            }
            default:
                return new RunTarget($"other:{server}|{webhook.TargetId}");
        }
    }

    /// <summary>A POSIX directory without <c>.</c>, <c>..</c>, repeated or trailing slashes.</summary>
    internal static string NormalizeDirectory(string? path)
    {
        var parts = new List<string>();
        foreach (var part in (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>Removes the keys of old finished deliveries, at most once an hour.</summary>
    private async Task PruneIfDueAsync()
    {
        var now = _time.GetUtcNow();
        var last = Interlocked.Read(ref _lastPrune);
        if (now.ToUnixTimeSeconds() - last < 3600 || Interlocked.CompareExchange(ref _lastPrune, now.ToUnixTimeSeconds(), last) != last)
            return;
        try
        {
            using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
            await _deliveries.PruneAsync((now - _options.DeliveryRetention).UtcDateTime, bookkeeping.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Old webhook deliveries could not be pruned");
        }
    }

    /// <summary>The webhook as an automation actor on its owner. The owner is only ever set by a person on the
    /// Webhooks page (create or take over), so that person is also its provenance: the PC-D1 rules apply, not an
    /// agent's.</summary>
    internal static AutomationActor ActorOf(WebhookEntity webhook)
    {
        var owner = string.IsNullOrWhiteSpace(webhook.Owner) ? null : webhook.Owner;
        return new AutomationActor("webhook:" + webhook.WebhookId, $"Webhook '{webhook.Name}'", owner, owner);
    }

    /// <summary>
    /// Runs the webhook's action through the Mutation Gateway on its owner's authority, re-checked now (R1-09, INV-04):
    /// the action's capability on its target, and for a compose deploy every container of the plan. The target and
    /// plan come from <see cref="WebhookTargets"/>, the same as for the Webhooks page's Test. Every delivery is decided
    /// — also one without an owner, or whose target or plan cannot be resolved — so it is audited, and a refusal
    /// because the owner lost the authority (or there is none) switches the webhook off and tells an admin; any other
    /// refusal (kill switch, protected resource, an unknown target, the Git app's own owner) is reported to an admin
    /// and leaves it on. The action itself runs only for a webhook with an owner.
    /// </summary>
    private async Task<(bool Success, string Output, bool Refused)> RunAsOwnerAsync(WebhookEntity webhook, string runId,
        RunTarget target, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var automation = scope.ServiceProvider.GetService<IAutomationMutationRunner>();
        if (automation is null) return (false, "Automated actions are not available.", true);

        // The run's operation (R1-18): its decision, run, verification and audit carry the run id. Nested decisions (a
        // deployment's containers and mounts) get ids of their own and the link to it.
        var actor = ActorOf(webhook);
        var runActor = actor with { OperationId = runId };
        var sid = WebhookTargets.ServerOf(webhook);
        string capability;
        MutationOutcome<(bool, string)> outcome;
        switch (webhook.Action)
        {
            case "restart":
            {
                capability = "container.restart";
                // The container the run locked. An unresolved one is decided by its stored name: the resolver refuses
                // it (audited).
                var (container, error) = (target.Container, target.Error);
                outcome = await automation.RunAsync(runActor, "container.restart",
                    new { containerId = container?.Id ?? webhook.TargetId, name = container?.Name ?? webhook.TargetId, serverId = sid },
                    async _ =>
                    {
                        EnsureOwned(webhook);
                        if (container is null) throw Unresolved(error ?? $"Container '{webhook.TargetId}' not found");
                        await _docker.RestartContainerAsync(container.Id, sid);
                        return (true, $"Container {container.Name} restarted.");
                    }, ct);
                break;
            }
            case "rebuild":
            {
                capability = "container.update";
                var (container, error) = (target.Container, target.Error);
                outcome = await automation.RunAsync(runActor, "container.update",
                    new { containerId = container?.Id ?? webhook.TargetId, name = container?.Name ?? webhook.TargetId, serverId = sid },
                    async _ =>
                    {
                        EnsureOwned(webhook);
                        if (container is null) throw Unresolved(error ?? $"Container '{webhook.TargetId}' not found");
                        var newId = await _docker.RecreateContainerAsync(container.Id, sid);
                        return (true, $"Container {container.Name} rebuilt (new ID: {newId[..Math.Min(12, newId.Length)]}).");
                    }, ct);
                break;
            }
            case "deploy":
            {
                capability = "compose.deploy";
                // The plan the run locked. One that cannot be made is still decided on the project directory; then
                // nothing runs.
                var (plan, error) = (target.Plan, target.Error);
                var gate = automation.GateFor(actor, "compose.deploy");
                outcome = await automation.RunAsync(runActor, "compose.deploy",
                    new { projectName = plan?.ProjectNameForAudit, projectDir = webhook.TargetId, serverId = sid },
                    async token =>
                    {
                        EnsureOwned(webhook);
                        if (plan is null) throw Unresolved(error!);
                        (bool, string) result = (false, "The deployment did not run.");
                        await gate(plan, async () => result = await DeployCompose(webhook, plan.ProjectName, token));
                        return result;
                    }, ct);
                break;
            }
            case "git-deploy":
            {
                // F5: push-triggered git redeploy; TargetId = the GitDeployApp id. The webhook's owner decides firing
                // it; the deployment then runs on the app owner's authority (R1-08), so both must hold it. A refusal
                // of the app's owner is a refusal of this delivery too.
                capability = "gitdeploy.deploy";
                var gitDeploy = scope.ServiceProvider.GetRequiredService<IGitDeployService>();
                var (app, error) = await LookupAsync(webhook, async () =>
                    (await gitDeploy.GetAppAsync(webhook.TargetId), (string?)"Git deploy app not found."), ct);
                outcome = await automation.RunAsync(runActor, "gitdeploy.deploy",
                    new { appId = app?.Id ?? webhook.TargetId, name = app?.Name, serverId = app?.ServerId, revision = app?.Revision },
                    async token =>
                    {
                        EnsureOwned(webhook);
                        // An app this run found no server for (missing when the run took its lock, maybe saved since)
                        // is refused: the run holds no server it could deploy on.
                        if (app is null || target.Revision is null) throw Unresolved(error ?? "Git deploy app not found.");
                        // Only the app at the revision whose server this run holds: an app saved since (moved to
                        // another server) is refused, its next delivery takes the new server.
                        var (success, output, refused) = await GitDeployOwners.RunAsOwnerAsync(gitDeploy, automation,
                            app.Id, token, revision: target.Revision);
                        if (refused)
                            throw new MutationRefusedException($"Git deployment '{app.Name}': {output}", MutationStatus.Denied, "");
                        return (success, output);
                    }, ct);
                break;
            }
            default:
                return (false, $"Unknown action: {webhook.Action}", true);
        }

        var refused = outcome.Error as MutationRefusedException;
        // The gateway reports a cancelled action (the run's deadline, shutdown) as failed with the cancellation: it is
        // the run's cancellation, not an action's message.
        if (refused is null && outcome.Error is OperationCanceledException cancelled)
            throw new OperationCanceledException(cancelled.Message, cancelled, cancelled.CancellationToken);
        if (refused is null && outcome.Status is MutationStatus.Executed or MutationStatus.Failed)
            return outcome.Value.Item2 is not null
                ? (outcome.Value.Item1, outcome.Value.Item2, false)
                : (false, outcome.Error?.Message ?? outcome.Reason ?? "The action failed.", false);

        var reason = refused?.Message ?? outcome.Reason ?? $"The action was {outcome.Status}.";
        // A webhook without an owner never runs (EnsureOwned), also during the grace period other automation may
        // have: it is switched off like one whose owner lost the authority.
        var lost = string.IsNullOrWhiteSpace(webhook.Owner)
            ? "it has no owner"
            : automation.LostAuthority(actor, capability);
        if (lost is not null && await SuspendAsync(webhook.WebhookId, webhook.Owner))
        {
            await NotifyAsync(scope, "automation_suspended", webhook,
                $"Webhook \"{webhook.Name}\" was switched off: {lost}. It stays off until an admin takes it over on " +
                "the Webhooks page and switches it on again.");
            return (false, $"Suspended: {lost}. {reason}", true);
        }
        await NotifyAsync(scope, "automation_refused", webhook,
            $"Webhook \"{webhook.Name}\" was called, but its action did not run. {reason}");
        return (false, $"Refused: {reason}", true);
    }

    /// <summary>What the action acts on, looked up before the decision. A lookup that throws (a removed server, an
    /// unreachable host) leaves it unresolved — the delivery is still decided, refused and reported — and its detail
    /// stays in the log.</summary>
    private async Task<(T? Value, string? Error)> LookupAsync<T>(WebhookEntity webhook, Func<Task<(T? Value, string? Error)>> lookup,
        CancellationToken ct)
        where T : class
    {
        try
        {
            // A lookup only reads: one that outlives the run's deadline is left behind, and the target is unresolved.
            var (value, error) = await lookup().WaitAsync(ct);
            return (value, value is null ? error : null);
        }
        catch (Exception ex)
        {
            // Also a timeout (a cancellation): a delivery has no caller that could cancel it, so it is still decided.
            _logger.LogWarning(ex, "Webhook {Name}: its target could not be looked up", webhook.Name);
            return (null, $"The target of '{webhook.Name}' could not be looked up.");
        }
    }

    /// <summary>The webhook's compose plan (<see cref="WebhookTargets.ComposePlanAsync"/>). The planner's message can
    /// quote values from the project's <c>.env</c> — a project name built from a password — so it never reaches a
    /// refusal, the audit, a notification, the webhook log or this log; the page's Test shows it to the person.</summary>
    private async Task<(ComposePlan? Plan, string? Error)> PlanAsync(WebhookEntity webhook, CancellationToken ct)
    {
        var (plan, _) = await WebhookTargets.ComposePlanAsync(_planner, webhook, ct);
        if (plan is not null) return (plan, null);
        _logger.LogWarning("Webhook {Name}: the compose deployment in {Directory} could not be planned",
            webhook.Name, webhook.TargetId);
        return (null, $"The compose deployment in '{webhook.TargetId}' could not be planned; the Test button on the " +
                      "Webhooks page shows why.");
    }

    /// <summary>An action whose target or plan cannot be resolved is refused, not a failed run: an admin hears of it.</summary>
    private static MutationRefusedException Unresolved(string reason) => new(reason, MutationStatus.Denied, "");

    /// <summary>A webhook without an owner never runs its action — also not when owners are optional for other
    /// automation (<see cref="AutomationOptions.RequireOwner"/> off): only an admin taking it over enables it.</summary>
    private static void EnsureOwned(WebhookEntity webhook)
    {
        if (string.IsNullOrWhiteSpace(webhook.Owner))
            throw new MutationRefusedException(
                $"'{webhook.Name}' has no owner: an admin must take it over on the Webhooks page first.",
                MutationStatus.Denied, "");
    }

    /// <summary>Switches the webhook off if it still has the owner the call ran on; a takeover meanwhile wins.</summary>
    private async Task<bool> SuspendAsync(string webhookId, string? owner)
    {
        await _config.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            return await db.Webhooks.Where(w => w.WebhookId == webhookId && w.Owner == owner)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Enabled, false)) == 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Switching off webhook {WebhookId} failed", webhookId);
            return false;
        }
        finally { _config.Release(); }
    }

    private async Task NotifyAsync(IServiceScope scope, string eventType, WebhookEntity webhook, string message)
    {
        _logger.LogWarning("Webhook {Name}: {Message}", webhook.Name, message);
        if (scope.ServiceProvider.GetService<INotificationService>() is not { } notify) return;
        try
        {
            await notify.SendAsync(new NotificationEvent
            {
                EventType = eventType,
                ContainerName = webhook.Name,
                ImageInfo = message,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification for webhook {Name} failed", webhook.Name);
        }
    }

    private async Task<(bool, string)> DeployCompose(WebhookEntity webhook, string lockedProject, CancellationToken ct)
    {
        var sid = WebhookTargets.ServerOf(webhook);
        // The plan (R1-07) made again right before writing: the compose files of the directory as compose finds them,
        // the project and every existing container `up` touches — then exactly that runs (explicit -p / -f). The owner
        // decided every container of the plan made before (R1-09); one that turns up only now stops it.
        // Either failing is a refusal (nothing ran), reported to an admin like the first plan's — also a planning
        // pass that throws (a Docker timeout), whose detail stays in the log.
        var (plan, error) = await LookupAsync(webhook, () => PlanAsync(webhook, ct), ct);
        if (plan is null) throw Unresolved(error!);
        // The run holds the lock of the project it planned; another project now (an edited compose file or .env) is
        // not what it holds. The name is not quoted: it can come from the .env.
        if (!string.Equals(plan.ProjectName, lockedProject, StringComparison.Ordinal))
            throw Unresolved($"The compose deployment in '{webhook.TargetId}' now plans another project than the one the run holds; nothing ran.");
        try
        {
            plan.EnsureDecided(MutationScope.Current
                ?? throw new ComposePlanException("The deployment was not decided."));
        }
        catch (ComposePlanException ex)
        {
            throw Unresolved(ex.Message);   // names only containers
        }
        var result = await _executor.ExecuteAsync(sid, plan.DeployScript(pull: true), TimeSpan.FromMinutes(5), ct);
        return (result.Success, result.Success ? result.Output : result.Error);
    }

    /// <summary>Signs a synthetic test delivery with the stored secret, as the webhook's provider would, and sends it
    /// through the exact path of an external caller — signature, deduplication, queue, the target's turn — then waits
    /// for its run.</summary>
    public async Task<(bool Success, string Output)> TriggerSignedTestAsync(string webhookId)
    {
        WebhookEntity? webhook;
        using (var scope = _scopeFactory.CreateScope())
            webhook = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Webhooks.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WebhookId == webhookId);
        if (webhook == null) return (false, "Webhook not found");
        if (string.IsNullOrEmpty(webhook.Secret))
            return (false, "Webhook has no secret configured — regenerate its secret first.");
        if (!webhook.Enabled) return (false, "Webhook is disabled");

        var request = WebhookSignatures.SignedTest(webhook, _time.GetUtcNow(), "ui-test");
        var (key, problem) = WebhookSignatures.Verify(webhook, request, _time.GetUtcNow(), _options.TimestampTolerance);
        if (key is null) return (false, Text("Webhook_Test_Invalid", "The test delivery was not valid: {0}", problem!));
        var completion = new TaskCompletionSource<RunEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = await QueueAsync(webhook, key, request.SourceIp, completion, CancellationToken.None);
        if (accepted.Reply != WebhookReply.Accepted)
            return (false, accepted.Reply == WebhookReply.Busy
                ? Text("Webhook_Test_Busy", "The delivery queue is full; try again shortly.")
                : Text("Webhook_Test_NotAccepted", "The test delivery was not accepted ({0}).", accepted.Reply.ToString()));
        try
        {
            var end = await completion.Task.WaitAsync(_options.TestRunTimeout);
            return (end.Success, end.Message is { } m ? Text(m.Key, m.Format, m.Args) : end.Output);
        }
        catch (TimeoutException)
        {
            return (false, Text("Webhook_Test_StillRunning", "Run {0} is still queued or running; its result will appear in the log.",
                accepted.RunId!));
        }
    }

    /// <summary>A message for the person on the Webhooks page, in their language when a localizer is available.</summary>
    private string Text(string key, string english, params object[] args)
    {
        var localized = _text?[key, args];
        return localized is { ResourceNotFound: false } ? localized.Value : string.Format(CultureInfo.InvariantCulture, english, args);
    }

    /// <summary>256-bit cryptographically random HMAC secret, lowercase hex (64 chars).</summary>
    internal static string GenerateSecret() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Requests per key in the current minute (fixed window). The table holds only the current window, and
    /// keys are known webhook ids (and "*"), so it cannot be grown by unknown ids.</summary>
    private sealed class RateWindow(TimeProvider time)
    {
        private readonly object _lock = new();
        private long _minute = -1;
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public bool TryTake(string key, int limit)
        {
            var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
            lock (_lock)
            {
                if (minute != _minute)
                {
                    _minute = minute;
                    _counts.Clear();
                }
                var count = _counts.GetValueOrDefault(key);
                if (count >= limit) return false;
                _counts[key] = count + 1;
                return true;
            }
        }
    }
}

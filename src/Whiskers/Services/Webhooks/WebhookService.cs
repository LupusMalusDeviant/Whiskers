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
    private readonly IComposeRunner? _composeRunner;
    private readonly ILogger<WebhookService> _logger;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _time;
    private readonly WebhookDeliveryStore _deliveries;
    private readonly CancellationToken _stopping;
    private readonly IStringLocalizer<SharedResource>? _text;
    // R1-31 (INV-52): the signing secrets are stored encrypted with a key derived from the vault's.
    private readonly Whiskers.Services.Vault.IVaultService? _vault;

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


    /// <summary>Request-rate buckets per webhook id: the id as sent, valid or not, so the limit says nothing about
    /// whether a webhook exists — and the table stays bounded whatever ids are sent.</summary>
    private const int RateBuckets = 1024;

    public WebhookService(IServiceScopeFactory scopeFactory, IDockerService docker,
        IHostCommandExecutor executor, IComposePlanner planner, ILogger<WebhookService> logger,
        IOptions<WebhookOptions>? options = null, TimeProvider? time = null, IHostApplicationLifetime? lifetime = null,
        IStringLocalizer<SharedResource>? text = null, ResourceLocks? locks = null, Whiskers.Services.Vault.IVaultService? vault = null,
        IComposeRunner? composeRunner = null)
    {
        _vault = vault;
        // Built only when the parts it needs are there; a run without them is refused (fail closed).
        _composeRunner = composeRunner ?? (executor is null || docker is null ? null : new ComposeRunner(executor, docker));
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
        var secret = webhook.Secret;
        webhook.Secret = ProtectSecret(webhook.WebhookId, secret);   // before anything is written

        await _config.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            db.Webhooks.Add(webhook);
            await db.SaveChangesAsync();
            Whiskers.Services.Mutations.ConfigChange.Record($"webhook:{webhook.WebhookId}", null, AuditView(webhook));   // R1-22
            // The caller shows the secret once; the database keeps it encrypted.
            webhook.Secret = secret;
            return webhook;
        }
        finally { _config.Release(); }
    }

    /// <summary>The secret as stored (R1-31): encrypted, bound to the webhook. Without a usable vault the save is
    /// refused before anything is written — a signing secret is never stored in plaintext.</summary>
    private string ProtectSecret(string webhookId, string secret)
        => _vault?.ProtectField(Whiskers.Services.Vault.ProtectedFields.WebhookSecret(webhookId), secret)
           ?? throw new InvalidOperationException(Text("Webhook_Error_SecretNeedsVault",
               "A webhook's signing secret is stored in the vault, and the vault is not usable (VAULT_KEY missing or wrong)."));

    /// <summary>A webhook as loaded, with its secret readable for signing: an encrypted one opened through the vault, a
    /// plaintext one from before R1-31 as it is. Empty when it cannot be opened — the delivery is then refused like one
    /// to a webhook without a secret.</summary>
    private WebhookEntity WithReadableSecret(WebhookEntity webhook, out bool unreadable)
    {
        var plain = Whiskers.Services.Vault.ProtectedFields.Read(_vault,
            Whiskers.Services.Vault.ProtectedFields.WebhookSecret(webhook.WebhookId), webhook.Secret);
        unreadable = plain is null;
        webhook.Secret = plain ?? "";
        return webhook;
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
            var secret = GenerateSecret();
            webhook.Secret = ProtectSecret(webhookId, secret);
            await db.SaveChangesAsync();
            Whiskers.Services.Mutations.ConfigChange.Record($"webhook:{webhookId}", before, AuditView(webhook));   // R1-22
            return secret;
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
        WithReadableSecret(webhook, out var unreadable);
        if (unreadable)
            return Reject(webhookId, "the webhook's secret cannot be read (the vault is not usable, or the key is another)");

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
        WebhookEntity? queued = null;
        // Whether the delivery's event is written by now (by the gateway, or as not run).
        var decided = false;
        var abandoning = false;
        try
        {
            queued = await ReadWebhookAsync(run.WebhookId, ct);
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
                    // INV-45: the delivery ends before its decision; its action did not run. It is recorded and settled
                    // beside the worker, once (GiveUpAsync) — also for a webhook removed before the worker read it:
                    // then by its id, unowned. Owned by this branch from here on: nothing else records it.
                    decided = true;
                    abandoning = true;
                    var noTurnWebhook = queued;
                    _ = Task.Run(() => GiveUpAsync(run, noTurnWebhook, message.English, record: true));
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
            // Started: from here on the delivery ends — run, refused or failed — and is on record (INV-45).
            var webhook = await ReadWebhookAsync(run.WebhookId, ct);
            if (webhook is null || !webhook.Enabled || queued is null || !SameTarget(queued, webhook))
            {
                // Removed, switched off or pointed elsewhere while it waited: it does not run.
                message = new("Webhook_Run_Changed",
                    "Refused: the webhook was removed, switched off or changed before the delivery ran.", []);
                // INV-45: the accepted delivery is refused before its decision — on record as the delivery it was
                // (by its id, unowned, when the webhook was gone before the worker read it).
                decided = true;
                await RecordNotRunAsync(queued ?? webhook, run.WebhookId, run.RunId, message.English);
                result = await FinishAsync(run, webhook, (false, message.English), WebhookDeliveryStore.Refused);
                return;
            }

            _logger.LogInformation("Webhook triggered: {Name} ({Action} on {Target}), run {RunId}",
                webhook.Name, webhook.Action, webhook.TargetId, run.RunId);
            // Started on the thread pool: the owner's authority and the gateway may do synchronous work (an
            // account lookup in SQLite) before they return a task — the deadline must already be watching then.
            // From here the gateway (or RunAsOwnerAsync's own refusal) records the delivery.
            decided = true;
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
            // Given up: it failed, and it is final. Recorded beside the worker — the database may be what failed — and
            // fenced by the claim. An accepted run nothing recorded yet (it failed before or at its start, or after it
            // and before its decision) is on record once, as a delivery that did not run (INV-45).
            _logger.LogError(ex, "Webhook run {RunId} failed outside its action", run.RunId);
            var unrecorded = !decided;
            // The claim stays held until the run is settled: no later delivery takes over a run being given up.
            abandoning = true;
            var givenUp = queued;
            _ = Task.Run(() => GiveUpAsync(run, givenUp, GivenUp, unrecorded));
        }
        finally
        {
            if (!handedOver)
            {
                gate?.Dispose();
                if (!abandoning) _deliveries.Release(run.Claim);
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
    /// synchronously and does not cancel them): the worker gives the run up (<see cref="GiveUpAsync"/>), which
    /// settles it whether this start lands before, after or never — the run never runs.</summary>
    private async Task<bool> StartAsync(QueuedRun run, CancellationToken ct)
    {
        var start = Task.Run(() => _deliveries.StartAsync(run.RunId, run.Claim, ct), CancellationToken.None);
        return await start.WaitAsync(ct);
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

    /// <summary>The reason of an accepted delivery its worker gave up before its decision (INV-45).</summary>
    internal const string GivenUp = "Failed: the delivery was given up before its decision (see the log); its action did not run.";

    /// <summary>The reason of an accepted delivery that a stop, a crash or a database outage left unsettled, found
    /// at the next start (INV-45).</summary>
    internal const string NotSettled =
        "Failed: the delivery was accepted but not settled before Whiskers stopped (or while its database was unavailable); its action did not run.";

    /// <summary>How long one bookkeeping step of a run (its state, its log row, a settlement) may take; internal so
    /// tests can stall it past this.</summary>
    internal TimeSpan BookkeepingTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a settlement that failed waits before it tries again.</summary>
    internal TimeSpan SettleRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, byte> _givingUp = new(StringComparer.Ordinal);

    /// <summary>
    /// A run its worker gave up — no turn on its target, a failure before its decision, or (<paramref name="record"/>
    /// false) a failure after the gateway recorded it. INV-45 (review round 3):
    /// <list type="number">
    /// <item>With <paramref name="record"/>, the delivery is recorded first, once: the gateway's Denied event under its
    /// run id, which survives a database outage in the audit outbox.</item>
    /// <item>It is then settled — failed, final — in one claim-fenced update, tried again until it lands or Whiskers
    /// stops; the claim is held all that time, so no repeat takes it over. A delivery the provider was told is accepted
    /// is not left queued for a redelivery that may never come; it needs a new delivery (a new delivery id, or for
    /// GitHub a new push).</item>
    /// <item>A stop before it is settled leaves it to <see cref="ReconcileAsync"/> at the next start, which finds its
    /// event and only settles it.</item>
    /// </list>
    /// A recorded one is also in the webhook's log and sends an <c>automation_refused</c> notification.
    /// </summary>
    private async Task GiveUpAsync(QueuedRun run, WebhookEntity? queued, string reason, bool record)
    {
        if (!_givingUp.TryAdd(run.RunId, 0)) return;   // once per run, whatever calls it
        try
        {
            WebhookEntity? webhook = queued;
            if (record)
            {
                // The webhook as the worker read it, or — the read is what failed — as it reads now.
                webhook ??= await ReadForRecordAsync(run.WebhookId);
                await RecordNotRunAsync(webhook, run.WebhookId, run.RunId, reason);
            }

            while (true)
            {
                try
                {
                    using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
                    await Bounded(t => _deliveries.SettleAsync(run.RunId, run.Claim, t), bookkeeping.Token);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Webhook run {RunId} could not be settled; trying again", run.RunId);
                    try
                    {
                        await Task.Delay(SettleRetryDelay, _stopping);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stopping: the next start settles it (ReconcileAsync); its event is already on record.
                        return;
                    }
                }
            }

            if (record && webhook is not null)
            {
                using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
                try
                {
                    await Bounded(t => RecordOutcomeAsync(run, webhook, (false, reason), WebhookDeliveryStore.Failed, t),
                        bookkeeping.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Webhook run {RunId}: its log entry could not be written", run.RunId);
                }
                using var scope = _scopeFactory.CreateScope();
                await NotifyAsync(scope, NotificationEventTypes.AutomationRefused, webhook,
                    $"Webhook \"{webhook.Name}\" was called, but its delivery was given up and its action did not run. " +
                    $"{reason} It needs a new delivery (a new delivery id, or for GitHub a new push).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Webhook run {RunId}: giving it up failed", run.RunId);
        }
        finally
        {
            _givingUp.TryRemove(run.RunId, out _);
            // Settled, or Whiskers stops: the claim, which the worker kept for this, is let go. Work still in flight
            // stays fenced by it in the database.
            _deliveries.Release(run.Claim);
        }
    }

    private async Task<WebhookEntity?> ReadForRecordAsync(string webhookId)
    {
        try
        {
            using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
            return await ReadWebhookAsync(webhookId, bookkeeping.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook {WebhookId} could not be read for the record of its delivery", webhookId);
            return null;
        }
    }

    /// <summary>
    /// INV-45 (review round 3): settles the deliveries another process accepted and left queued or running — it stopped
    /// or crashed with runs still waiting, or could not settle one (its database was unavailable). Each is taken over
    /// for this (fenced, so a repeat that took it over first keeps it), then: without an event under its run id (in
    /// the audit log or the audit outbox) it is recorded once as not run and settled failed; with one, a queued row is
    /// settled failed and a running one — cut off while it ran, its outcome on record or unknown — stays as it is
    /// ("interrupted"). Run once at start (the Webhooks module); one pass at a time. A delivery whose acceptance
    /// answer was lost (answered <c>Busy</c>) cannot be told apart from an accepted one here and is settled the same
    /// way. Returns how many it recorded.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        await _reconcile.WaitAsync(ct);
        try
        {
            var recorded = 0;
            var since = (_time.GetUtcNow() - _options.DeliveryRetention).UtcDateTime;
            foreach (var row in await _deliveries.UnsettledOfOthersAsync(since, ct))
            {
                var claim = await _deliveries.AdoptAsync(row, ct);
                if (claim is null) continue;
                try
                {
                    var onRecord = await HasEventAsync(row.RunId, ct);
                    WebhookEntity? webhook = null;
                    if (!onRecord)
                    {
                        webhook = await ReadForRecordAsync(row.WebhookId);
                        await RecordNotRunAsync(webhook, row.WebhookId, row.RunId, NotSettled);
                        recorded++;
                    }
                    if (row.State == WebhookDeliveryStore.Queued || !onRecord)
                        await _deliveries.SettleAsync(row.RunId, claim, ct);
                    if (webhook is not null)
                    {
                        // Shown where a given-up delivery is: the webhook's log, and the admins are told.
                        await RecordOutcomeAsync(new QueuedRun(row.WebhookId, row.RunId, claim, null), webhook,
                            (false, NotSettled), WebhookDeliveryStore.Failed, ct);
                        using var scope = _scopeFactory.CreateScope();
                        await NotifyAsync(scope, NotificationEventTypes.AutomationRefused, webhook,
                            $"Webhook \"{webhook.Name}\" was called, but its delivery was not run before Whiskers stopped. " +
                            "It needs a new delivery (a new delivery id, or for GitHub a new push).");
                    }
                }
                finally
                {
                    _deliveries.Release(claim);
                }
            }
            if (recorded > 0)
                _logger.LogWarning("{Count} accepted webhook deliveries left unsettled before this start were recorded as not run",
                    recorded);
            return recorded;
        }
        finally
        {
            _reconcile.Release();
        }
    }

    private readonly SemaphoreSlim _reconcile = new(1, 1);

    /// <summary>Whether the delivery's run id has an audit event: in the log, or kept in the outbox to be written.</summary>
    private async Task<bool> HasEventAsync(string runId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        if (await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AuditLog.AsNoTracking()
                .AnyAsync(a => a.OperationId == runId, ct))
            return true;
        return scope.ServiceProvider.GetService<Whiskers.Services.Mutations.AuditOutbox>() is { } outbox
               && outbox.Load().Any(kept => kept.Row?.OperationId == runId);
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

    /// <summary>The webhook as an automation actor on its owner. The owner is a person: whoever creates or takes it
    /// over on the Webhooks page, or the person who approved `create_git_deploy_webhook` (contract 0001), never the
    /// calling key. That person is also its provenance: the PC-D1 rules apply, not an agent's.</summary>
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
                        ComposeImpact.ReportProject(plan);
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
            {
                // INV-45: a stored action none of the four is refused before any decision — on record all the same,
                // under its raw name, which the gateway refuses as no capability.
                var unknown = $"Unknown action: {webhook.Action}";
                await RecordNotRunAsync(webhook, webhook.WebhookId, runId, unknown);
                return (false, unknown, true);
            }
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
        if (lost is not null && await SuspendAsync(webhook.WebhookId, webhook.Owner, outcome.OperationId))
        {
            await NotifyAsync(scope, NotificationEventTypes.AutomationSuspended, webhook,
                $"Webhook \"{webhook.Name}\" was switched off: {lost}. It stays off until an admin takes it over on " +
                "the Webhooks page and switches it on again.");
            return (false, $"Suspended: {lost}. {reason}", true);
        }
        await NotifyAsync(scope, NotificationEventTypes.AutomationRefused, webhook,
            $"Webhook \"{webhook.Name}\" was called, but its action did not run. {reason}");
        return (false, $"Refused: {reason}", true);
    }

    /// <summary>The capability a webhook action is decided as; <c>null</c> for an action that is none of the four.</summary>
    internal static string? CapabilityOf(string? action) => action switch
    {
        "restart" => "container.restart",
        "rebuild" => "container.update",
        "deploy" => "compose.deploy",
        "git-deploy" => "gitdeploy.deploy",
        _ => null,
    };

    /// <summary>The capability a delivery is recorded under when its webhook was removed before the worker read it:
    /// what it would have done is unknown, so it names no capability (the gateway refuses it as such).</summary>
    internal const string RemovedWebhook = "(removed webhook)";

    /// <summary>
    /// INV-45: an accepted delivery that ends before its decision — its webhook changed, switched off or removed while
    /// it waited, no turn on its target in time, an action none of the four, a run its worker gave up before or after its
    /// start — is
    /// recorded as the gateway's Denied event of the delivery's operation (its run id), on the webhook owner's
    /// authority as for a run, so every accepted delivery is exactly one event. Its action did not run. A webhook that
    /// was gone before the worker read it (<paramref name="webhook"/> null) is recorded by its id, unowned, under
    /// <see cref="RemovedWebhook"/>. Written within its own bookkeeping time, never the run's deadline: a run that
    /// ends at its deadline is still on record.
    /// <para>Not accepted, so no attempt and no event: a delivery refused at acceptance — unknown id, switched off, an
    /// unreadable secret, a signature that does not verify, a stale timestamp outside the tolerance — or answered
    /// <c>Busy</c> (the queue full, the delivery not recordable, a repeat still being recorded): nothing was recorded or
    /// queued, and the provider may deliver it again. A <c>Duplicate</c> names the run it repeats, whose event is
    /// the one.</para>
    /// </summary>
    private async Task RecordNotRunAsync(WebhookEntity? webhook, string webhookId, string runId, string reason)
    {
        using var bookkeeping = new CancellationTokenSource(BookkeepingTimeout);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IAutomationMutationRunner>() is not { } automation) return;
            if (webhook is null)
            {
                await automation.RefuseAsync(
                    new AutomationActor("webhook:" + webhookId, $"Webhook {webhookId} (removed)", null) { OperationId = runId },
                    RemovedWebhook, new { webhookId }, reason, bookkeeping.Token);
                return;
            }
            var sid = WebhookTargets.ServerOf(webhook);
            object arguments = webhook.Action switch
            {
                "deploy" => new { projectDir = webhook.TargetId, serverId = sid },
                "git-deploy" => new { appId = webhook.TargetId, serverId = sid },
                _ => new { containerId = webhook.TargetId, name = webhook.TargetId, serverId = sid },
            };
            await automation.RefuseAsync(ActorOf(webhook) with { OperationId = runId },
                CapabilityOf(webhook.Action) ?? (string.IsNullOrWhiteSpace(webhook.Action) ? "(no action)" : webhook.Action),
                arguments, reason, bookkeeping.Token);
        }
        catch (Exception ex)
        {
            // The delivery stays refused; the audit trail's own failure is reported by the gateway.
            _logger.LogWarning(ex, "Webhook {WebhookId}: the delivery that did not run could not be recorded", webhookId);
        }
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
    private async Task<bool> SuspendAsync(string webhookId, string? owner, string operationId)
    {
        await _config.WaitAsync();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var before = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(w => w.WebhookId == webhookId);
            // Only a webhook that is still on: one an Admin switched off meanwhile needs no switch-off (and no row).
            if (await db.Webhooks.Where(w => w.WebhookId == webhookId && w.Owner == owner && w.Enabled)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.Enabled, false)) != 1)
                return false;
            if (before is not null) await RecordSuspensionAsync(scope, before, operationId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Switching off webhook {WebhookId} failed", webhookId);
            return false;
        }
        finally { _config.Release(); }
    }

    /// <summary>The automatic switch-off is a configuration change made after the run's own event was written: it gets
    /// a row of its own with the webhook's version before and after, so the section's versions stay one chain (R1-22).
    /// Written under the configuration lock, like every other change of the webhook.</summary>
    private async Task RecordSuspensionAsync(IServiceScope scope, WebhookEntity before, string operationId)
    {
        if (scope.ServiceProvider.GetService<Whiskers.Services.AuditLog.IAuditLogService>() is not { } audit) return;
        var from = Whiskers.Services.Mutations.ConfigChange.Describe(AuditView(before));
        var wasEnabled = before.Enabled;
        before.Enabled = false;
        var to = Whiskers.Services.Mutations.ConfigChange.Describe(AuditView(before));
        before.Enabled = wasEnabled;
        var changes = string.Join("; ", Whiskers.Services.Mutations.ConfigChange.Diff(from, to)
            .Select(c => $"{c.Path}: {c.Before} → {c.After}"));
        await audit.TryLogAsync("system", "system", SuspendAction, "webhook", before.WebhookId, before.Name,
            $"operation={operationId}; {Whiskers.Services.Mutations.ConfigChange.VersionAction} {from.Version} → {to.Version}" +
            (changes.Length > 0 ? $"; {changes}" : ""), success: true);
    }

    /// <summary>The audit action of an automatic switch-off (the owner lost the authority, or there is none).</summary>
    public const string SuspendAction = "webhook.suspended";

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
        // The host primitive: it runs only inside the compose.deploy decision of this run (R1-48).
        // Contract 0006: write, pull, build, inspect the images, up — then the re-check of what the run created.
        var runner = _composeRunner ?? throw Unresolved("The compose runner is not available; nothing ran.");
        var result = await runner.RunAsync(plan,
            (command, timeout, token) => _executor.ExecuteMutationAsync(sid, command, timeout, token),
            new ComposeRunOptions(true, TimeSpan.FromMinutes(5)), ct);
        if (result.Success) return (true, result.Output);
        return (false, result.Refusal ?? (result.Violations.Count > 0 ? string.Join("; ", result.Violations) : result.Error));
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
        WithReadableSecret(webhook, out var unreadable);
        if (unreadable)
            return (false, Text("Webhook_Test_SecretUnreadable",
                "The webhook's secret cannot be read: the vault is not usable (VAULT_KEY missing or wrong)."));
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

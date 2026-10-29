using System.Collections.Concurrent;
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

    // R1-10 (INV-42): accepted deliveries wait here (bounded) for one of a bounded number of workers; runs on the same
    // target never overlap. The workers start with the first delivery.
    private readonly Channel<QueuedRun> _queue;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _targets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool, string)>> _waiting = new(StringComparer.Ordinal);
    private readonly RateWindow _rates;
    private int _workersStarted;
    private long _lastPrune;

    private sealed record QueuedRun(string WebhookId, string RunId, string? SourceIp);

    public WebhookService(IServiceScopeFactory scopeFactory, IDockerService docker,
        IHostCommandExecutor executor, IComposePlanner planner, ILogger<WebhookService> logger,
        IOptions<WebhookOptions>? options = null, TimeProvider? time = null, IHostApplicationLifetime? lifetime = null)
    {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _executor = executor;
        _planner = planner;
        _logger = logger;
        _options = options?.Value ?? new WebhookOptions();
        _time = time ?? TimeProvider.System;
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
        _deliveries = new WebhookDeliveryStore(scopeFactory, _time);
        _queue = Channel.CreateBounded<QueuedRun>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
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
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        // Compare-and-set in one statement: a takeover by someone else in between wins.
        var changed = await db.Webhooks
            .Where(w => w.WebhookId == webhookId && w.Owner == expectedOwnerId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Owner, ownerId));
        return changed == 1;
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

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        db.Webhooks.Add(webhook);
        await db.SaveChangesAsync();
        return webhook;
    }

    public async Task<string> RegenerateSecretAsync(string webhookId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId)
            ?? throw new InvalidOperationException("Webhook not found");
        webhook.Secret = GenerateSecret();
        await db.SaveChangesAsync();
        return webhook.Secret;
    }

    public async Task SetEnabledAsync(string webhookId, bool enabled)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId)
            ?? throw new InvalidOperationException("Webhook not found");
        // A legacy webhook without a secret must not come back to life unauthenticated —
        // force a secret regeneration first.
        if (enabled && string.IsNullOrEmpty(webhook.Secret))
            throw new InvalidOperationException("Webhook has no secret — regenerate its secret before enabling it.");
        webhook.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    public async Task DeleteWebhookAsync(string webhookId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId);
        if (webhook != null)
        {
            db.Webhooks.Remove(webhook);
            await db.SaveChangesAsync();
        }
    }

    public async Task<WebhookAcceptance> AcceptAsync(string webhookId, WebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Bounded before anything is looked up: an unauthenticated caller spends at most this much.
        if (!_rates.TryTake("*", _options.RequestsPerMinute)) return new WebhookAcceptance(WebhookReply.RateLimited);
        if (string.IsNullOrWhiteSpace(webhookId) || webhookId.Length > 64 || request.Body.Length > _options.MaxBodyBytes)
            return Reject(webhookId, "the request is malformed or too large");

        WebhookEntity? webhook;
        using (var scope = _scopeFactory.CreateScope())
            webhook = await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Webhooks.AsNoTracking()
                .FirstOrDefaultAsync(w => w.WebhookId == webhookId, ct);
        if (webhook is null) return Reject(webhookId, "unknown webhook");
        if (!_rates.TryTake(webhook.WebhookId, _options.RequestsPerMinutePerWebhook))
            return new WebhookAcceptance(WebhookReply.RateLimited);
        if (!webhook.Enabled) return Reject(webhookId, "the webhook is disabled");

        // Signature and freshness by the webhook's provider; the key identifies the delivery (signed, never metadata).
        var (key, problem) = WebhookSignatures.Verify(webhook, request, _time.GetUtcNow(), _options.TimestampTolerance);
        if (key is null) return Reject(webhookId, problem!);
        return await QueueAsync(webhook, key, request.SourceIp, null, ct);
    }

    /// <summary>Records the delivery and queues its run, or answers with the run it repeats.</summary>
    private async Task<WebhookAcceptance> QueueAsync(WebhookEntity webhook, string key, string? sourceIp,
        TaskCompletionSource<(bool, string)>? completion, CancellationToken ct)
    {
        var (fresh, row) = await _deliveries.RecordAsync(webhook.WebhookId, key, ct);
        if (!fresh)
        {
            _logger.LogInformation("Webhook {Name}: delivery repeats run {RunId}; nothing runs again", webhook.Name, row.RunId);
            return new WebhookAcceptance(WebhookReply.Duplicate, row.RunId, _deliveries.Describe(row));
        }

        if (completion is not null) _waiting[row.RunId] = completion;
        EnsureWorkers();
        if (!_queue.Writer.TryWrite(new QueuedRun(webhook.WebhookId, row.RunId, sourceIp)))
        {
            _waiting.TryRemove(row.RunId, out _);
            // Nothing ran and nothing stays recorded: the provider may deliver it again.
            try { await _deliveries.ForgetAsync(row.RunId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Webhook {Name}: queued delivery could not be removed", webhook.Name); }
            _logger.LogWarning("Webhook {Name}: the delivery queue is full; refused as busy", webhook.Name);
            return new WebhookAcceptance(WebhookReply.Busy);
        }
        _logger.LogInformation("Webhook {Name}: delivery accepted as run {RunId}", webhook.Name, row.RunId);
        return new WebhookAcceptance(WebhookReply.Accepted, row.RunId, "queued");
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
        for (var i = 0; i < Math.Max(1, _options.MaxConcurrentRuns); i++)
            _ = Task.Run(WorkAsync);
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var run in _queue.Reader.ReadAllAsync(_stopping))
            {
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

    /// <summary>Runs one accepted delivery: once, and never at the same time as another run on the same target.</summary>
    private async Task ExecuteRunAsync(QueuedRun run)
    {
        _waiting.TryRemove(run.RunId, out var completion);
        (bool Success, string Output) result = (false, "The delivery did not run.");
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == run.WebhookId);
            var target = webhook is null ? run.WebhookId : TargetKey(webhook);
            var gate = _targets.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                if (!await _deliveries.StartAsync(run.RunId)) return;
                string state;
                if (webhook is null || !webhook.Enabled)
                {
                    // Removed or switched off while it waited: it does not run.
                    result = (false, "Refused: the webhook was removed or switched off before the delivery ran.");
                    state = WebhookDeliveryStore.Refused;
                }
                else
                {
                    _logger.LogInformation("Webhook triggered: {Name} ({Action} on {Target}), run {RunId}",
                        webhook.Name, webhook.Action, webhook.TargetId, run.RunId);
                    bool refused;
                    try
                    {
                        (result.Success, result.Output, refused) = await RunAsOwnerAsync(webhook, run.RunId);
                    }
                    catch (Exception ex)
                    {
                        (result, refused) = ((false, ex.Message), false);
                    }
                    state = refused ? WebhookDeliveryStore.Refused
                        : result.Success ? WebhookDeliveryStore.Succeeded : WebhookDeliveryStore.Failed;
                }

                try
                {
                    await _deliveries.FinishAsync(run.RunId, state);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Webhook run {RunId}: its outcome could not be recorded", run.RunId);
                }
                if (webhook is not null)
                {
                    webhook.LastTriggered = _time.GetUtcNow().UtcDateTime;
                    webhook.TriggerCount++;
                    db.WebhookLogs.Add(new WebhookLogEntity
                    {
                        WebhookId = webhook.WebhookId,
                        WebhookName = webhook.Name,
                        Success = result.Success,
                        Output = result.Success ? result.Output : null,
                        Error = result.Success ? null : result.Output,
                        SourceIp = run.SourceIp,
                    });
                    await db.SaveChangesAsync();
                }
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            completion?.TrySetResult(result);
            await PruneIfDueAsync();
        }
    }

    /// <summary>Runs on the same resource never overlap: the server and the target, whichever webhook fires them.</summary>
    internal static string TargetKey(WebhookEntity webhook) =>
        webhook.Action == "git-deploy"
            ? "git:" + webhook.TargetId
            : $"{WebhookTargets.ServerOf(webhook)}|{webhook.TargetId}";

    /// <summary>Removes the keys of old finished deliveries, at most once an hour.</summary>
    private async Task PruneIfDueAsync()
    {
        var now = _time.GetUtcNow();
        var last = Interlocked.Read(ref _lastPrune);
        if (now.ToUnixTimeSeconds() - last < 3600 || Interlocked.CompareExchange(ref _lastPrune, now.ToUnixTimeSeconds(), last) != last)
            return;
        try
        {
            await _deliveries.PruneAsync((now - _options.DeliveryRetention).UtcDateTime);
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
    private async Task<(bool Success, string Output, bool Refused)> RunAsOwnerAsync(WebhookEntity webhook, string runId)
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
                // An unresolved container is decided by its stored name: the resolver refuses it (audited).
                var (container, error) = await LookupAsync(webhook, () => WebhookTargets.ContainerAsync(_docker, webhook));
                outcome = await automation.RunAsync(runActor, "container.restart",
                    new { containerId = container?.Id ?? webhook.TargetId, name = container?.Name ?? webhook.TargetId, serverId = sid },
                    async _ =>
                    {
                        EnsureOwned(webhook);
                        if (container is null) throw Unresolved(error ?? $"Container '{webhook.TargetId}' not found");
                        await _docker.RestartContainerAsync(container.Id, sid);
                        return (true, $"Container {container.Name} restarted.");
                    });
                break;
            }
            case "rebuild":
            {
                capability = "container.update";
                var (container, error) = await LookupAsync(webhook, () => WebhookTargets.ContainerAsync(_docker, webhook));
                outcome = await automation.RunAsync(runActor, "container.update",
                    new { containerId = container?.Id ?? webhook.TargetId, name = container?.Name ?? webhook.TargetId, serverId = sid },
                    async _ =>
                    {
                        EnsureOwned(webhook);
                        if (container is null) throw Unresolved(error ?? $"Container '{webhook.TargetId}' not found");
                        var newId = await _docker.RecreateContainerAsync(container.Id, sid);
                        return (true, $"Container {container.Name} rebuilt (new ID: {newId[..Math.Min(12, newId.Length)]}).");
                    });
                break;
            }
            case "deploy":
            {
                capability = "compose.deploy";
                // A plan that cannot be made is still decided on the project directory; then nothing runs.
                var (plan, error) = await LookupAsync(webhook, () => PlanAsync(webhook));
                var gate = automation.GateFor(actor, "compose.deploy");
                outcome = await automation.RunAsync(runActor, "compose.deploy",
                    new { projectName = plan?.ProjectNameForAudit, projectDir = webhook.TargetId, serverId = sid },
                    async _ =>
                    {
                        EnsureOwned(webhook);
                        if (plan is null) throw Unresolved(error!);
                        (bool, string) result = (false, "The deployment did not run.");
                        await gate(plan, async () => result = await DeployCompose(webhook));
                        return result;
                    });
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
                    (await gitDeploy.GetAppAsync(webhook.TargetId), (string?)"Git deploy app not found."));
                outcome = await automation.RunAsync(runActor, "gitdeploy.deploy",
                    new { appId = app?.Id ?? webhook.TargetId, name = app?.Name, serverId = app?.ServerId, revision = app?.Revision },
                    async _ =>
                    {
                        EnsureOwned(webhook);
                        if (app is null) throw Unresolved(error!);
                        var (success, output, refused) = await GitDeployOwners.RunAsOwnerAsync(gitDeploy, automation, app.Id);
                        if (refused)
                            throw new MutationRefusedException($"Git deployment '{app.Name}': {output}", MutationStatus.Denied, "");
                        return (success, output);
                    });
                break;
            }
            default:
                return (false, $"Unknown action: {webhook.Action}", true);
        }

        var refused = outcome.Error as MutationRefusedException;
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
    private async Task<(T? Value, string? Error)> LookupAsync<T>(WebhookEntity webhook, Func<Task<(T? Value, string? Error)>> lookup)
        where T : class
    {
        try
        {
            var (value, error) = await lookup();
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
    private async Task<(ComposePlan? Plan, string? Error)> PlanAsync(WebhookEntity webhook)
    {
        var (plan, _) = await WebhookTargets.ComposePlanAsync(_planner, webhook);
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

    private async Task<(bool, string)> DeployCompose(WebhookEntity webhook)
    {
        var sid = WebhookTargets.ServerOf(webhook);
        // The plan (R1-07) made again right before writing: the compose files of the directory as compose finds them,
        // the project and every existing container `up` touches — then exactly that runs (explicit -p / -f). The owner
        // decided every container of the plan made before (R1-09); one that turns up only now stops it.
        // Either failing is a refusal (nothing ran), reported to an admin like the first plan's — also a planning
        // pass that throws (a Docker timeout), whose detail stays in the log.
        var (plan, error) = await LookupAsync(webhook, () => PlanAsync(webhook));
        if (plan is null) throw Unresolved(error!);
        try
        {
            plan.EnsureDecided(MutationScope.Current
                ?? throw new ComposePlanException("The deployment was not decided."));
        }
        catch (ComposePlanException ex)
        {
            throw Unresolved(ex.Message);   // names only containers
        }
        var result = await _executor.ExecuteAsync(sid, plan.DeployScript(pull: true), TimeSpan.FromMinutes(5));
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
        if (key is null) return (false, $"The test delivery was not valid: {problem}");
        var completion = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = await QueueAsync(webhook, key, request.SourceIp, completion, CancellationToken.None);
        if (accepted.Reply != WebhookReply.Accepted)
            return (false, accepted.Reply == WebhookReply.Busy
                ? "The delivery queue is full; try again shortly."
                : $"The test delivery was not accepted ({accepted.Reply}).");
        try
        {
            return await completion.Task.WaitAsync(_options.TestRunTimeout);
        }
        catch (TimeoutException)
        {
            return (false, $"Run {accepted.RunId} is still queued or running; its result will appear in the log.");
        }
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

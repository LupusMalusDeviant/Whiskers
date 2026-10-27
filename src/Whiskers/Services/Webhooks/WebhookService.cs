using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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

    public WebhookService(IServiceScopeFactory scopeFactory, IDockerService docker,
        IHostCommandExecutor executor, IComposePlanner planner, ILogger<WebhookService> logger)
    {
        _scopeFactory = scopeFactory;
        _docker = docker;
        _executor = executor;
        _planner = planner;
        _logger = logger;
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

    /// <summary>Execute a webhook trigger. Validates HMAC signature if provided.</summary>
    public async Task<(bool Success, string Output)> TriggerAsync(string webhookId, string? signature = null, string? body = null, string? sourceIp = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();

        var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId);
        if (webhook == null) return (false, "Webhook not found");
        if (!webhook.Enabled) return (false, "Webhook is disabled");

        // Secrets are mandatory (HOCH-12 part 2). A webhook without one (legacy row) is rejected
        // fail-closed instead of accepted unauthenticated — the boot migration disables such rows,
        // but this guard also covers a row edited behind the app's back.
        if (string.IsNullOrEmpty(webhook.Secret))
            return (false, "Webhook has no secret configured — regenerate its secret in the UI.");

        // Validate HMAC: a valid signature over the raw body is REQUIRED — otherwise anyone who
        // learns the (unauthenticated) webhook URL could trigger it without knowing the shared
        // secret. Compare in constant time to avoid signature-timing leaks.
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(body))
            return (false, "Signature required");

        var expected = Encoding.UTF8.GetBytes($"sha256={ComputeHmac(webhook.Secret, body)}");
        var provided = Encoding.UTF8.GetBytes(signature.Trim());
        if (expected.Length != provided.Length ||
            !CryptographicOperations.FixedTimeEquals(expected, provided))
            return (false, "Invalid signature");

        _logger.LogInformation("Webhook triggered: {Name} ({Action} on {Target})", webhook.Name, webhook.Action, webhook.TargetId);

        string output;
        bool success;

        try
        {
            (success, output) = await RunAsOwnerAsync(webhook);
        }
        catch (Exception ex)
        {
            success = false;
            output = ex.Message;
        }

        // Log
        webhook.LastTriggered = DateTime.UtcNow;
        webhook.TriggerCount++;
        db.WebhookLogs.Add(new WebhookLogEntity
        {
            WebhookId = webhook.WebhookId,
            WebhookName = webhook.Name,
            Success = success,
            Output = success ? output : null,
            Error = success ? null : output,
            SourceIp = sourceIp
        });
        await db.SaveChangesAsync();

        return (success, output);
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
    private async Task<(bool, string)> RunAsOwnerAsync(WebhookEntity webhook)
    {
        using var scope = _scopeFactory.CreateScope();
        var automation = scope.ServiceProvider.GetService<IAutomationMutationRunner>();
        if (automation is null) return (false, "Automated actions are not available.");

        var actor = ActorOf(webhook);
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
                outcome = await automation.RunAsync(actor, "container.restart",
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
                outcome = await automation.RunAsync(actor, "container.update",
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
                outcome = await automation.RunAsync(actor, "compose.deploy",
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
                outcome = await automation.RunAsync(actor, "gitdeploy.deploy",
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
                return (false, $"Unknown action: {webhook.Action}");
        }

        var refused = outcome.Error as MutationRefusedException;
        if (refused is null && outcome.Status is MutationStatus.Executed or MutationStatus.Failed)
            return outcome.Value.Item2 is not null
                ? outcome.Value
                : (false, outcome.Error?.Message ?? outcome.Reason ?? "The action failed.");

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
            return (false, $"Suspended: {lost}. {reason}");
        }
        await NotifyAsync(scope, "automation_refused", webhook,
            $"Webhook \"{webhook.Name}\" was called, but its action did not run. {reason}");
        return (false, $"Refused: {reason}");
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

    /// <summary>Signs a synthetic test payload with the stored secret and runs it through the exact
    /// same path as an external CI caller — proves URL, secret and HMAC wiring end-to-end.</summary>
    public async Task<(bool Success, string Output)> TriggerSignedTestAsync(string webhookId)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MetricsDbContext>();
            var webhook = await db.Webhooks.FirstOrDefaultAsync(w => w.WebhookId == webhookId);
            if (webhook == null) return (false, "Webhook not found");
            if (string.IsNullOrEmpty(webhook.Secret))
                return (false, "Webhook has no secret configured — regenerate its secret first.");

            var body = $"{{\"test\":true,\"webhookId\":\"{webhook.WebhookId}\",\"timestamp\":\"{DateTime.UtcNow:O}\"}}";
            var signature = $"sha256={ComputeHmac(webhook.Secret, body)}";
            return await TriggerAsync(webhookId, signature, body, "ui-test");
        }
    }

    /// <summary>256-bit cryptographically random HMAC secret, lowercase hex (64 chars).</summary>
    internal static string GenerateSecret() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static string ComputeHmac(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }
}

using System.Security.Cryptography;
using System.Text;
using Whiskers.Models;

namespace Whiskers.Services.Notifications;

/// <summary>
/// Contract 0017, amendment 4 (G2): the one place that builds <see cref="NotificationEvent.ThrottleSubject"/>. Every value is
/// a fixed prefix plus ids or fixed names — never free text, a secret, or a webhook's URL token — so the key can neither
/// leak anything nor grow without bound. A key only: nothing shows or delivers it.
/// </summary>
public static class NotificationThrottleSubjects
{
    /// <summary><c>webhook_disabled</c> and the webhook's <c>automation_*</c>: the webhook's database id (not its
    /// <c>WebhookId</c>, which is part of its URL).</summary>
    public static string Webhook(long id) => $"webhook:{id}";

    /// <summary>One notice for several webhooks (the startup migration, the reconcile of unsettled deliveries): their ids,
    /// distinct and sorted, so the same set is the same subject.</summary>
    public static string Webhooks(IEnumerable<long> ids) =>
        "webhooks:" + string.Join(',', ids.Distinct().Order());

    /// <summary><c>automation_*</c> of a scheduled task: its database id.</summary>
    public static string Task(long id) => $"task:{id}";

    /// <summary><c>automation_suspended</c> of an AI trigger: its id.</summary>
    public static string AiTrigger(string id) => $"ai-trigger:{id}";

    /// <summary><c>automation_suspended</c> of an endpoint check: its id.</summary>
    public static string EndpointCheck(string id) => $"endpoint-check:{id}";

    /// <summary><c>automation_suspended</c> of an owned automation (<c>autopatch</c>, <c>autoupdate</c>): its fixed name.</summary>
    public static string Automation(string name) => $"automation:{name}";

    /// <summary><c>monitoring_stalled</c> / <c>monitoring_resumed</c>: the loop's fixed name.</summary>
    public static string Loop(string loop) => $"loop:{loop}";

    /// <summary><c>exposure_violation</c>: the compose project (stack) plus the rules it broke — the rule set it ran
    /// under and the violations reported. Hashed, because project names and violation lines come from compose files: the
    /// same project with the same violations stays throttled, a new violation (the restore that failed afterwards) is
    /// a new subject.</summary>
    public static string Exposure(string project, bool agentRules, IEnumerable<string> violations)
    {
        var text = string.Join('\n', new[] { project, agentRules ? "agent" : "person" }
            .Concat(violations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "exposure:" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}

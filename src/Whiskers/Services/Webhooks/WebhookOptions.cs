namespace Whiskers.Services.Webhooks;

/// <summary>
/// The bounds of webhook delivery (R1-10, INV-42), configuration section <c>Webhooks</c>. Every resource an
/// unauthenticated caller can make Whiskers spend is limited: the body it reads, the requests it answers, the
/// deliveries it queues and the runs it executes at once (runs on the same target never overlap).
/// </summary>
public sealed class WebhookOptions
{
    public const string Section = "Webhooks";

    /// <summary>The largest body read; a larger one is refused unread.</summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Requests answered per minute for all webhooks together, valid or not.</summary>
    public int RequestsPerMinute { get; set; } = 300;

    /// <summary>Requests answered per minute for one webhook.</summary>
    public int RequestsPerMinutePerWebhook { get; set; } = 30;

    /// <summary>Accepted deliveries waiting to run; when it is full a delivery is refused as busy (the provider retries).</summary>
    public int QueueCapacity { get; set; } = 100;

    /// <summary>Runs executing at once.</summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>How far a signed timestamp (provider <c>whiskers</c>) may be from now.</summary>
    public TimeSpan TimestampTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a finished delivery's key is kept. For provider <c>github</c>, which signs no time, this is how
    /// long a replayed delivery is recognised.</summary>
    public TimeSpan DeliveryRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long a run may take, from leaving the queue: waiting for its target, looking it up and the action.
    /// A run that has not got its target by then fails. An action that does not stop when cancelled keeps its target
    /// until it ends (nothing overlaps it) but gives its worker back; at most <see cref="MaxConcurrentRuns"/> such runs
    /// exist at once.</summary>
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the Webhooks page's Test waits for its run.</summary>
    public TimeSpan TestRunTimeout { get; set; } = TimeSpan.FromMinutes(10);
}

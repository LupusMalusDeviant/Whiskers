using System.Globalization;
using Microsoft.Extensions.Hosting;

namespace Whiskers.Services.Notifications;

/// <summary>Where a channel connects: scheme, host and port only — never a path, query or user info (Slack and Discord
/// carry their secret in the URL path, R1-25).</summary>
public sealed record OutboundEndpoint(string Scheme, string Host, int Port)
{
    /// <summary>The endpoint of an http(s) URL, or <c>null</c> when it is none.</summary>
    public static OutboundEndpoint? FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return new OutboundEndpoint(uri.Scheme, uri.IdnHost.Trim('[', ']'), uri.Port);
    }

    public override string ToString() =>
        $"{Scheme}://{OutboundException.Bracketed(Host)}:{Port.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>One configured channel the guard would refuse, or whose host does not resolve (<see cref="Address"/> is
/// then <c>"unresolved"</c>).</summary>
public sealed record OutboundRefusal(string Channel, OutboundEndpoint Target, string Address, string? Range)
{
    public const string Unresolved = "unresolved";
}

/// <summary>R1-54: lists each configured channel the guard would refuse — at start (log) and on the Admin-only
/// notification settings.</summary>
public interface IOutboundTargetReport
{
    Task<IReadOnlyList<OutboundRefusal>> ScanAsync(CancellationToken ct);
}

/// <summary>Implements <see cref="IOutboundTargetReport"/> over the registered channels' <see cref="INotificationChannel.Targets"/>.</summary>
public sealed class OutboundTargetReport(IEnumerable<INotificationChannel> channels, IOutboundTargetGuard guard) : IOutboundTargetReport
{
    /// <summary>How long one host may take to resolve before it is listed as unresolved.</summary>
    internal static readonly TimeSpan PerHostTimeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<OutboundRefusal>> ScanAsync(CancellationToken ct)
    {
        var refusals = new List<OutboundRefusal>();
        foreach (var channel in channels)
        {
            IReadOnlyList<OutboundEndpoint> targets;
            try { targets = channel.Targets; }
            catch (Exception) { continue; }   // a channel whose settings cannot be read has nothing to list
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                OutboundVerdict verdict;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(PerHostTimeout);
                try { verdict = await guard.CheckAsync(target.Host, target.Port, deadline.Token).WaitAsync(deadline.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    verdict = new OutboundVerdict(false, null, null, Unresolved: true);
                }
                if (verdict.Unresolved)
                    refusals.Add(new OutboundRefusal(channel.Name, target, OutboundRefusal.Unresolved, null));
                else if (!verdict.Allowed)
                    refusals.Add(new OutboundRefusal(channel.Name, target, verdict.RefusedAddress!.ToString(), verdict.Range));
            }
        }
        return refusals;
    }
}

/// <summary>
/// The start-up listing (R1-54): after an upgrade every LAN or Tailscale channel is refused until an Admin adds an
/// exception, so each configured channel the guard would refuse is logged once at start, with the refused address.
/// Best-effort and in the background: <see cref="StartAsync"/> returns at once and never delays <c>/readyz</c>; a host
/// that does not resolve is listed as unresolved.
/// </summary>
public sealed class OutboundTargetStartupCheck(IOutboundTargetReport report, ILogger<OutboundTargetStartupCheck> logger) : IHostedService
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>The background scan (tests wait for it).</summary>
    internal Task Completion { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Completion = Task.Run(() => LogRefusalsAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LogRefusalsAsync(CancellationToken ct)
    {
        try
        {
            foreach (var r in await report.ScanAsync(ct))
            {
                if (r.Address == OutboundRefusal.Unresolved)
                    logger.LogWarning("Notification channel {Channel}: {Target} is unresolved; the outbound guard (R1-54) " +
                                      "judges it when it resolves", r.Channel, r.Target.ToString());
                else
                    logger.LogWarning("Notification channel {Channel}: {Target} resolves to {Address} ({Range}) and is " +
                                      "refused by the outbound guard (R1-54) until an Admin adds an exception under Settings",
                        r.Channel, r.Target.ToString(), r.Address, r.Range);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("The outbound guard's start-up listing failed ({Error})", ex.GetType().Name);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync();
        try { await Completion.WaitAsync(cancellationToken); }
        catch (Exception) { /* stopping */ }
    }
}

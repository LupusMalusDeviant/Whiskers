using System.Net;

namespace Whiskers.Utils;

/// <summary>R1-50: remembers the last peer that sent forwarded headers without being a trusted proxy — a reverse
/// proxy that still needs to be named in <see cref="ForwardedHeadersConfig.SectionName"/>, or a client trying to claim
/// another address. Its headers were ignored; the production-readiness check names it so the operator can tell which.</summary>
public sealed class ForwardedHeadersObserver
{
    private readonly TimeProvider _time;
    private (string Peer, DateTimeOffset At)? _last;

    public ForwardedHeadersObserver(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The last untrusted peer that sent forwarded headers, and when; null when none has since the start.</summary>
    public (string Peer, DateTimeOffset At)? LastUntrustedPeer => _last;

    /// <summary>Records <paramref name="peer"/> as having sent forwarded headers it is not trusted for.</summary>
    public void Untrusted(IPAddress? peer) =>
        _last = (peer?.ToString() ?? "unknown", _time.GetUtcNow());
}

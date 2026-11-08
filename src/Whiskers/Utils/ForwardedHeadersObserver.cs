using System.Net;

namespace Whiskers.Utils;

/// <summary>R1-50: remembers the peers that sent forwarded headers without being a trusted proxy — a reverse proxy that
/// still needs to be named in <see cref="ForwardedHeadersConfig.SectionName"/>, or a client trying to claim another
/// address. Their headers were ignored; the production-readiness check names them so the operator can tell which.
/// Keeps up to <see cref="Capacity"/> distinct peers (the most recent), each forgotten <see cref="Window"/> after it was
/// last seen — a single scanner neither hides the real proxy nor leaves the warning standing until the next restart.</summary>
public sealed class ForwardedHeadersObserver
{
    /// <summary>How many distinct peers are kept.</summary>
    public const int Capacity = 8;

    /// <summary>How long a peer is reported after it was last seen.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>One untrusted peer and when it last sent forwarded headers.</summary>
    public sealed record Sighting(string Peer, DateTimeOffset At);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Sighting> _seen = new();

    public ForwardedHeadersObserver(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The untrusted peers seen within <see cref="Window"/>, most recent first; empty when there are none.</summary>
    public IReadOnlyList<Sighting> RecentUntrustedPeers
    {
        get
        {
            var cutoff = _time.GetUtcNow() - Window;
            lock (_gate)
                return _seen.Where(s => s.At >= cutoff).OrderByDescending(s => s.At).ToList();
        }
    }

    /// <summary>Records <paramref name="peer"/> as having sent forwarded headers it is not trusted for.</summary>
    public void Untrusted(IPAddress? peer)
    {
        var sighting = new Sighting(peer?.ToString() ?? "unknown", _time.GetUtcNow());
        lock (_gate)
        {
            _seen.RemoveAll(s => s.Peer == sighting.Peer);
            _seen.Add(sighting);
            if (_seen.Count > Capacity)
                _seen.Remove(_seen.MinBy(s => s.At)!);
        }
    }
}

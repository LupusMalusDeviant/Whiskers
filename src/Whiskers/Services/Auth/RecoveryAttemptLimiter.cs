using System.Collections.Concurrent;

namespace Whiskers.Services.Auth;

/// <summary>
/// The rate limit of the anonymous recovery-code redemption (R1-49). The page runs inside a Blazor circuit, which the
/// HTTP rate limiter of <c>/login-local</c> does not see, so the service limits attempts itself — per client address
/// and per account email, each in a fixed window. A refused attempt runs no check and changes nothing; the caller
/// gets the same answer as for a wrong code.
/// </summary>
public interface IRecoveryAttemptLimiter
{
    /// <summary>Counts one attempt of <paramref name="client"/> for <paramref name="account"/>. <c>Allowed</c> when both
    /// are within their limits; otherwise how many attempts of this client were refused in the current window (1 for
    /// the first), so the caller can record a refusal storm once instead of row by row.</summary>
    RecoveryAttempt TryAcquire(string client, string account);
}

/// <param name="Allowed">The attempt may run.</param>
/// <param name="RefusedInWindow">Throttled attempts of this client in the current window, this one included.</param>
public readonly record struct RecoveryAttempt(bool Allowed, int RefusedInWindow);

public sealed class RecoveryAttemptLimiter : IRecoveryAttemptLimiter
{
    /// <summary>Attempts per client address per <see cref="ClientWindow"/> (the sign-in's limit).</summary>
    public const int ClientLimit = 10;
    public static readonly TimeSpan ClientWindow = TimeSpan.FromMinutes(1);

    /// <summary>Attempts per account email per <see cref="AccountWindow"/>, whichever clients they come from.</summary>
    public const int AccountLimit = 10;
    public static readonly TimeSpan AccountWindow = TimeSpan.FromMinutes(15);

    /// <summary>Beyond this many remembered keys the expired ones are pruned.</summary>
    private const int Memory = 4096;

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start = start;
        public int Count;
        public int Refused;
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public RecoveryAttemptLimiter(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public RecoveryAttempt TryAcquire(string client, string account)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_windows.Count > Memory) Prune(now);
            var byClient = Current("c|" + (string.IsNullOrWhiteSpace(client) ? "unknown" : client), ClientWindow, now);
            // Every attempt of a client counts, also a refused one: a client that keeps trying stays refused.
            byClient.Count++;
            if (byClient.Count <= ClientLimit)
            {
                // An account's budget is spent only by attempts a client was allowed to make, so one throttled client
                // cannot use it up for everyone else.
                var byAccount = Current("a|" + (account ?? "").Trim().ToUpperInvariant(), AccountWindow, now);
                byAccount.Count++;
                if (byAccount.Count <= AccountLimit) return new RecoveryAttempt(true, 0);
            }
            byClient.Refused++;
            return new RecoveryAttempt(false, byClient.Refused);
        }
    }

    private Window Current(string key, TimeSpan length, DateTimeOffset now)
    {
        var window = _windows.GetOrAdd(key, _ => new Window(now));
        if (now - window.Start >= length)
        {
            window.Start = now;
            window.Count = 0;
            window.Refused = 0;
        }
        return window;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, window) in _windows)
            if (now - window.Start >= AccountWindow) _windows.TryRemove(key, out _);
    }
}

/// <summary>The address of the client a circuit (or request) belongs to, as the forwarded-headers middleware
/// resolved it. Scoped: read once, when the scope first asks — in a Blazor circuit that is while the circuit's
/// connection request is current. <c>null</c> when it cannot be told; callers then share one bucket (fail closed).</summary>
public interface IClientAddress
{
    string? Address { get; }
}

public sealed class HttpClientAddress(IHttpContextAccessor accessor) : IClientAddress
{
    private string? _address;
    private bool _read;

    public string? Address
    {
        get
        {
            if (_read) return _address;
            _address = accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
            _read = true;
            return _address;
        }
    }
}

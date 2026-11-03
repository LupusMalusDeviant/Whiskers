using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;

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

/// <summary>
/// The in-memory limiter. A client is its IPv4 address, or its IPv6 /64 (one end site — a client can pick any address
/// in it, so a single address would be no limit). Memory is bounded: each table holds at most <see cref="Capacity"/>
/// keys; expired ones are swept at most once per window length; when a table is still full, a new key shares one
/// overflow bucket with every other new key (fail closed — a flood of new addresses or emails cannot buy fresh budgets
/// or grow the table, and no attempt pays for a scan).
/// </summary>
public sealed class RecoveryAttemptLimiter : IRecoveryAttemptLimiter
{
    /// <summary>Attempts per client per <see cref="ClientWindow"/> (the sign-in's limit).</summary>
    public const int ClientLimit = 10;
    public static readonly TimeSpan ClientWindow = TimeSpan.FromMinutes(1);

    /// <summary>Attempts per account email per <see cref="AccountWindow"/>, whichever clients they come from.</summary>
    public const int AccountLimit = 10;
    public static readonly TimeSpan AccountWindow = TimeSpan.FromMinutes(15);

    /// <summary>The most keys each table (clients, accounts) remembers.</summary>
    public const int DefaultCapacity = 4096;

    /// <summary>The bucket of a client whose address cannot be told.</summary>
    public const string UnknownClient = "unknown";

    /// <summary>The bucket every new key shares while its table is full (not a valid address or email).</summary>
    public const string OverflowKey = "#overflow";

    private sealed class Window(DateTimeOffset start)
    {
        public DateTimeOffset Start = start;
        public int Count;
        public int Refused;
    }

    private sealed class Table(TimeSpan length, int capacity)
    {
        public readonly Dictionary<string, Window> Windows = new(StringComparer.Ordinal);
        public readonly TimeSpan Length = length;
        public readonly int Capacity = capacity;
        public DateTimeOffset? Swept;
        public int Sweeps;
    }

    private readonly Table _clients;
    private readonly Table _accounts;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public RecoveryAttemptLimiter(TimeProvider? time = null) : this(time, DefaultCapacity) { }

    /// <summary>With a smaller <paramref name="capacity"/> (tests).</summary>
    internal RecoveryAttemptLimiter(TimeProvider? time, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time ?? TimeProvider.System;
        _clients = new Table(ClientWindow, capacity);
        _accounts = new Table(AccountWindow, capacity);
    }

    /// <summary>How many keys the two tables hold and how often each was swept (tests).</summary>
    internal (int Clients, int Accounts, int ClientSweeps, int AccountSweeps) Footprint
    {
        get { lock (_gate) return (_clients.Windows.Count, _accounts.Windows.Count, _clients.Sweeps, _accounts.Sweeps); }
    }

    public RecoveryAttempt TryAcquire(string client, string account)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var byClient = Current(_clients, ClientKey(client), now);
            // Every attempt of a client counts, also a refused one: a client that keeps trying stays refused.
            byClient.Count++;
            if (byClient.Count <= ClientLimit)
            {
                // An account's budget is spent only by attempts a client was allowed to make, so one throttled client
                // cannot use it up for everyone else.
                var byAccount = Current(_accounts, (account ?? "").Trim().ToUpperInvariant(), now);
                byAccount.Count++;
                if (byAccount.Count <= AccountLimit) return new RecoveryAttempt(true, 0);
            }
            byClient.Refused++;
            return new RecoveryAttempt(false, byClient.Refused);
        }
    }

    /// <summary>The bucket of a client address: IPv4 as is (also when IPv4-mapped), IPv6 by its /64, anything else
    /// <see cref="UnknownClient"/>.</summary>
    public static string ClientKey(string? client)
    {
        if (string.IsNullOrWhiteSpace(client) || !IPAddress.TryParse(client.Trim(), out var address)) return UnknownClient;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static Window Current(Table table, string key, DateTimeOffset now)
    {
        if (!table.Windows.TryGetValue(key, out var window))
        {
            if (table.Windows.Count >= table.Capacity) Sweep(table, now);
            if (table.Windows.Count >= table.Capacity) key = OverflowKey;
            if (!table.Windows.TryGetValue(key, out window))
            {
                window = new Window(now);
                table.Windows[key] = window;   // the overflow bucket may be the one key beyond the capacity
            }
        }
        if (now - window.Start >= table.Length)
        {
            window.Start = now;
            window.Count = 0;
            window.Refused = 0;
        }
        return window;
    }

    /// <summary>Drops the expired keys of a full table — at most once per window length, so a flood of new keys pays
    /// for one scan per window, not one per attempt.</summary>
    private static void Sweep(Table table, DateTimeOffset now)
    {
        if (table.Swept is { } last && now - last < table.Length) return;
        table.Swept = now;
        table.Sweeps++;
        foreach (var key in table.Windows.Where(w => now - w.Value.Start >= table.Length).Select(w => w.Key).ToList())
            table.Windows.Remove(key);
    }
}

/// <summary>The address of the client a circuit (or request) belongs to, as the forwarded-headers middleware
/// resolved it. Scoped: read once, when the scope first asks. <c>null</c> when it cannot be told; callers then share
/// one bucket (fail closed).</summary>
public interface IClientAddress
{
    string? Address { get; }
}

/// <summary>
/// The client address of a circuit or request: in a circuit, the address of its hub connection
/// (<see cref="CircuitClientAddress"/>, else the hub call running now — WebSockets and long polling alike); otherwise
/// the current HTTP request (prerendering, plain requests). On a long-polling connection the HTTP request is long gone
/// when the circuit renders, so <see cref="IHttpContextAccessor"/> alone would be empty there.
/// </summary>
public sealed class HttpClientAddress(IHttpContextAccessor accessor, CircuitClientAddress? circuit = null) : IClientAddress
{
    private string? _address;
    private bool _read;

    public string? Address
    {
        get
        {
            if (_read) return _address;
            _address = circuit?.Address
                ?? ClientAddressHubFilter.Current
                ?? accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
            _read = true;
            return _address;
        }
    }
}

/// <summary>
/// The client address of the hub connection a circuit runs on. Blazor creates a circuit's handlers while it starts the
/// circuit — inside the hub call <c>StartCircuit</c>, where <see cref="ClientAddressHubFilter"/> has the connection's
/// address — so it is taken then, and again whenever the connection comes back up (reconnect).
/// </summary>
public sealed class CircuitClientAddress : CircuitHandler
{
    public CircuitClientAddress() => Address = ClientAddressHubFilter.Current;

    public string? Address { get; private set; }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (ClientAddressHubFilter.Current is { } address) Address = address;
        return Task.CompletedTask;
    }
}

/// <summary>
/// For the duration of every hub call, makes the calling connection's client address available
/// (<see cref="Current"/>) — from the connection's own <see cref="Microsoft.AspNetCore.Http.HttpContext"/>, which
/// SignalR keeps for WebSockets and long polling alike, after the forwarded-headers middleware. It reads one
/// value and decides nothing; it neither refuses a call nor touches the session check.
/// </summary>
public sealed class ClientAddressHubFilter : IHubFilter
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The client address of the hub call running in this flow, if any.</summary>
    public static string? Current => Ambient.Value;

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);
        // Set inside this async method: the value flows into the call (and the work it starts), not back out of it.
        Ambient.Value = invocationContext.Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        return await next(invocationContext);
    }
}

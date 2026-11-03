using System.Net;
using System.Net.Sockets;

namespace Whiskers.Services.Notifications;

/// <summary>
/// R1-54 (INV-60): the one resolve-filter-connect primitive of the outbound notification channels and webhooks. It
/// resolves the host once, judges every answer by <see cref="OutboundAddressPolicy"/> and the live exceptions, and
/// connects itself to an address it judged — so the address checked is the address connected to, and a name that
/// rebinds between a check and a connect cannot reach anything else. The HTTP channels use it as their
/// <c>SocketsHttpHandler.ConnectCallback</c> (<see cref="OutboundHttp"/>), the e-mail channel for its SMTP socket.
/// </summary>
public interface IOutboundTargetGuard
{
    /// <summary>Resolves <paramref name="host"/>, judges each answer and connects to the first allowed one that
    /// answers. Throws <see cref="OutboundTargetRefusedException"/> when an answer is denied and not excepted — also
    /// when other answers are public: a name that points partly inside is refused as a whole.</summary>
    Task<Stream> ConnectAsync(string host, int port, CancellationToken ct);

    /// <summary>The judgement without connecting (the start-up and Settings listing).</summary>
    Task<OutboundVerdict> CheckAsync(string host, int port, CancellationToken ct);
}

/// <summary>What <see cref="IOutboundTargetGuard.CheckAsync"/> found: allowed, refused at
/// <see cref="RefusedAddress"/> (in <see cref="Range"/>), or <see cref="Unresolved"/>.</summary>
public sealed record OutboundVerdict(bool Allowed, IPAddress? RefusedAddress, string? Range, bool Unresolved)
{
    public static OutboundVerdict Allow { get; } = new(true, null, null, false);
}

/// <summary>Resolves a host name; the seam the tests answer with their own addresses (no real DNS).</summary>
public interface IOutboundDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>The system resolver.</summary>
public sealed class SystemOutboundDnsResolver : IOutboundDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => System.Net.Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// The guard refused a delivery (R1-54). Final for that delivery: the composite does not retry it and does not keep it
/// for redelivery. The message names the host and port and the refused address, never a URL path or query.
/// </summary>
public class OutboundTargetRefusedException : Exception
{
    public OutboundTargetRefusedException(string message) : base(message) { }

    /// <summary>The refusal in <paramref name="error"/> or its inner exceptions (an HTTP client wraps the one thrown in
    /// its connect callback), or <c>null</c>.</summary>
    public static OutboundTargetRefusedException? Find(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; depth++)
        {
            if (error is OutboundTargetRefusedException refused) return refused;
            error = error is AggregateException { InnerExceptions.Count: 1 } one ? one.InnerExceptions[0] : error.InnerException;
        }
        return null;
    }
}

/// <summary>A 3xx answer: redirects are never followed (R1-54) — the location could point anywhere, inside included.</summary>
public sealed class OutboundRedirectRefusedException(string message) : OutboundTargetRefusedException(message);

/// <summary>The direct connection failed while an egress proxy is configured for the destination: notification
/// channels no longer use a proxy (R1-54), so this is a reason of its own, not a generic network error.</summary>
public sealed class OutboundProxyNotUsedException(string message, Exception inner) : HttpRequestException(message, inner);

/// <summary>Implements <see cref="IOutboundTargetGuard"/>.</summary>
public sealed class OutboundTargetGuard : IOutboundTargetGuard
{
    private readonly IOutboundExceptionService _exceptions;
    private readonly IOutboundDnsResolver _resolver;

    public OutboundTargetGuard(IOutboundExceptionService exceptions, IOutboundDnsResolver resolver)
    {
        _exceptions = exceptions;
        _resolver = resolver;
    }

    /// <summary>Opens the connection to an address the guard allowed. The tests replace it to see which address is
    /// connected to (and to reach a local listener for a public address); production connects a TCP socket.</summary>
    internal Func<IPEndPoint, CancellationToken, ValueTask<Stream>> Connector { get; init; } = ConnectSocketAsync;

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var (addresses, verdict) = await JudgeAsync(host, port, ct);
        if (verdict.Unresolved) throw new SocketException((int)SocketError.HostNotFound);
        if (!verdict.Allowed) throw Refusal(host, port, verdict);

        Exception? last = null;
        foreach (var address in addresses)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // The address judged above, never the name again: what was checked is what is connected to.
                return await Connector(new IPEndPoint(address, port), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    public async Task<OutboundVerdict> CheckAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            return (await JudgeAsync(host, port, ct)).Verdict;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new OutboundVerdict(false, null, null, Unresolved: true);
        }
    }

    private async Task<(IReadOnlyList<IPAddress> Addresses, OutboundVerdict Verdict)> JudgeAsync(string host, int port, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var name = host.Trim();
        IReadOnlyList<IPAddress> answers = OutboundAddressPolicy.TryParseLiteral(name, out var literal)
            ? [literal]
            : await _resolver.ResolveAsync(name.TrimEnd('.'), ct);
        if (answers.Count == 0) return (answers, new OutboundVerdict(false, null, null, Unresolved: true));

        var exceptions = _exceptions.Current;
        foreach (var address in answers)
        {
            if (OutboundAddressPolicy.DeniedRange(address) is { } range && !exceptions.Allows(name, port, address))
                return (answers, new OutboundVerdict(false, OutboundAddressPolicy.Canonicalize(address), range, false));
        }
        return (answers, OutboundVerdict.Allow);
    }

    private static OutboundTargetRefusedException Refusal(string host, int port, OutboundVerdict verdict) =>
        new(
            $"Refused by the outbound guard: {Target(host, port)} resolves to {verdict.RefusedAddress} ({verdict.Range}). " +
            "Notification targets inside the network need an exact exception (Settings → Outbound targets).");

    internal static string Target(string host, int port) =>
        $"{OutboundException.Bracketed(host.Trim().Trim('[', ']'))}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

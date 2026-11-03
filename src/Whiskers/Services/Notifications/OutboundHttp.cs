using System.Net;

namespace Whiskers.Services.Notifications;

/// <summary>R1-54 (INV-60): the HTTP clients of the notification channels.</summary>
public interface IOutboundHttp
{
    /// <summary>A client over the guarded handler (15 s timeout). Clients share one handler; it outlives them.</summary>
    HttpClient CreateClient();

    /// <summary>The primary handler of the <see cref="OutboundHttp.ClientName"/> factory client: it forwards to the
    /// guarded handler and may be disposed by the factory without touching it. The factory adds its request logging
    /// (redacted, R1-25) in front.</summary>
    HttpMessageHandler CreatePrimaryHandler();
}

/// <summary>
/// Implements <see cref="IOutboundHttp"/>. Every request goes through a <see cref="SocketsHttpHandler"/> whose
/// <c>ConnectCallback</c> is <see cref="IOutboundTargetGuard.ConnectAsync"/>, with <c>UseProxy = false</c> (a proxy's
/// address says nothing about the destination) and <c>AllowAutoRedirect = false</c> (a 3xx answer is refused: its
/// location could point anywhere).
///
/// <para>The guard runs when a connection is opened, and a pooled keep-alive connection does not pass it again. So the
/// inner handler — and with it the connection pool — is replaced as soon as the exception list's version changed,
/// before the next request is sent: a removed or narrowed exception also ends the connections it allowed. The old
/// handler is disposed once requests that were already running on it have ended (after the client timeout).</para>
/// </summary>
public sealed class OutboundHttp : IOutboundHttp, IDisposable
{
    /// <summary>The timeout of every channel's client.</summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The name of the factory client every HTTP channel is constructed with.</summary>
    public const string ClientName = "notification-channels";

    private readonly IOutboundTargetGuard _guard;
    private readonly IOutboundExceptionService _exceptions;
    private readonly IWebProxy? _systemProxy;
    private readonly GuardedHandler _handler;
    private readonly object _sync = new();
    private HttpMessageInvoker? _current;
    private long _currentVersion = -1;

    public OutboundHttp(IOutboundTargetGuard guard, IOutboundExceptionService exceptions)
        : this(guard, exceptions, HttpClient.DefaultProxy) { }

    /// <param name="systemProxy">The egress proxy the process is configured with (the environment's
    /// <c>HTTPS_PROXY</c> and friends). It is never used — only consulted to name a failed direct connection that used to
    /// go through it.</param>
    internal OutboundHttp(IOutboundTargetGuard guard, IOutboundExceptionService exceptions, IWebProxy? systemProxy)
    {
        _guard = guard;
        _exceptions = exceptions;
        _systemProxy = systemProxy;
        _handler = new GuardedHandler(this);
    }

    /// <summary>How long a replaced handler is kept for the requests still running on it.</summary>
    internal TimeSpan RetireAfter { get; init; } = ClientTimeout + TimeSpan.FromSeconds(15);

    public HttpClient CreateClient() => new(_handler, disposeHandler: false) { Timeout = ClientTimeout };

    public HttpMessageHandler CreatePrimaryHandler() => new Forwarding(new HttpMessageInvoker(_handler, disposeHandler: false));

    /// <summary>The inner handler as every channel gets it; internal so the tests can check its settings.</summary>
    internal static SocketsHttpHandler NewInnerHandler(IOutboundTargetGuard guard) => new()
    {
        UseProxy = false,
        Proxy = null,
        AllowAutoRedirect = false,
        UseCookies = false,
        // A public name is judged again now and then, not only once per process.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, ct) =>
            new ValueTask<Stream>(guard.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct)),
    };

    private HttpMessageInvoker Current()
    {
        var version = _exceptions.Current.Version;
        lock (_sync)
        {
            if (_current is null || _currentVersion != version)
            {
                var old = _current;
                _current = new HttpMessageInvoker(NewInnerHandler(_guard), disposeHandler: true);
                _currentVersion = version;
                if (old is not null) Retire(old);
            }
            return _current;
        }
    }

    private void Retire(HttpMessageInvoker old) =>
        _ = Task.Delay(RetireAfter).ContinueWith(_ => old.Dispose(), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);

    private bool ProxyWouldApply(Uri? uri)
    {
        if (_systemProxy is null || uri is null) return false;
        try
        {
            return !_systemProxy.IsBypassed(uri) && _systemProxy.GetProxy(uri) is { } proxy && proxy != uri;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Scheme, host and port of <paramref name="uri"/> — never its path or query (Slack and Discord carry
    /// their secret in the path, R1-25).</summary>
    internal static string Origin(Uri? uri) => uri is null ? "(no URL)" : $"{uri.Scheme}://{uri.Authority}";

    public void Dispose()
    {
        lock (_sync)
        {
            _current?.Dispose();
            _current = null;
        }
        _handler.Dispose();
    }

    private sealed class Forwarding(HttpMessageInvoker inner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            inner.SendAsync(request, ct);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();   // the shared guarded handler itself stays
            base.Dispose(disposing);
        }
    }

    private sealed class GuardedHandler(OutboundHttp owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage response;
            try
            {
                response = await owner.Current().SendAsync(request, ct);
            }
            catch (HttpRequestException ex) when (OutboundTargetRefusedException.Find(ex) is null
                                                 && owner.ProxyWouldApply(request.RequestUri))
            {
                throw new OutboundProxyNotUsedException(
                    $"The direct connection to {Origin(request.RequestUri)} failed. An egress proxy is configured for it, " +
                    "but notification channels do not use a proxy (R1-54): allow the direct connection.", ex);
            }

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                response.Dispose();
                throw new OutboundRedirectRefusedException(
                    $"{Origin(request.RequestUri)} answered with a redirect ({status}); redirects are not followed (R1-54).");
            }
            return response;
        }
    }
}

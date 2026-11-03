using System.Net;
using System.Net.Sockets;
using Whiskers.Configuration;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>R1-54: test doubles for the outbound guard — a settable exception list, a resolver that answers from the
/// test (no real DNS), and a connector that records the address actually connected to and reaches a local listener for
/// it, so a "public" address never leaves the machine.</summary>
internal static class OutboundTestDoubles
{
    /// <summary>A guard with no resolver behind it that lets exactly <paramref name="host"/>:<paramref name="port"/>
    /// (an IP literal) through, connecting for real.</summary>
    public static OutboundTargetGuard GuardAllowing(string host, int port) =>
        new(new Exceptions(new OutboundExceptionEntry { Host = host, Port = port }), new Resolver());

    /// <summary>A settable exception list; every <see cref="Set"/> publishes a new version.</summary>
    internal sealed class Exceptions(params OutboundExceptionEntry[] entries) : IOutboundExceptionService
    {
        private OutboundExceptionList _current = OutboundExceptionList.From(entries, 1);

        public OutboundExceptionList Current => _current;

        public void Set(params OutboundExceptionEntry[] entries) =>
            _current = OutboundExceptionList.From(entries, _current.Version + 1);

        public Task SaveAsync(IReadOnlyList<OutboundExceptionEntry> entries, CancellationToken ct = default)
        {
            Set([.. entries]);
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers each name from <see cref="Answers"/>; a name with several queued answers gives the next one on
    /// each call (rebinding). Unknown names do not resolve.</summary>
    internal sealed class Resolver : IOutboundDnsResolver
    {
        private readonly Dictionary<string, Queue<IPAddress[]>> _answers = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Asked { get; } = [];

        public Resolver Answer(string host, params string[] addresses)
        {
            if (!_answers.TryGetValue(host, out var queue)) _answers[host] = queue = new Queue<IPAddress[]>();
            queue.Enqueue(addresses.Select(IPAddress.Parse).ToArray());
            return this;
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        {
            lock (Asked) Asked.Add(host);
            if (!_answers.TryGetValue(host, out var queue) || queue.Count == 0)
                throw new SocketException((int)SocketError.HostNotFound);
            return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        }
    }

    /// <summary>Records every endpoint the guard connects to and connects to <c>127.0.0.1:</c><see cref="Port"/>
    /// instead (or fails, when <see cref="Fail"/> is set).</summary>
    internal sealed class Connector
    {
        public List<IPEndPoint> Connected { get; } = [];
        public int Port { get; set; }
        public bool Fail { get; set; }

        public async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
        {
            lock (Connected) Connected.Add(endpoint);
            if (Fail) throw new SocketException((int)SocketError.ConnectionRefused);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public List<IPEndPoint> Snapshot() { lock (Connected) return Connected.ToList(); }
    }

    /// <summary>A minimal HTTP/1.1 server on loopback: answers every request with <see cref="Respond"/> (status plus
    /// extra header lines), keeps the connection open unless <see cref="Close"/>, and counts connections and requests.</summary>
    internal sealed class HttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _connections;
        private int _requests;

        public Func<(int Status, string[] Headers)> Respond { get; set; } = () => (204, []);
        public bool Close { get; set; }
        public int Port { get; }
        public int Connections => Volatile.Read(ref _connections);
        public int Requests => Volatile.Read(ref _requests);

        public HttpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                Interlocked.Increment(ref _connections);
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    // The head, up to the blank line.
                    int end;
                    while ((end = IndexOfHeadEnd(buffer)) < 0)
                    {
                        var n = await stream.ReadAsync(chunk, _stop.Token);
                        if (n == 0) return;
                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    }
                    var head = System.Text.Encoding.ASCII.GetString(buffer.GetRange(0, end).ToArray());
                    buffer.RemoveRange(0, end + 4);
                    var length = head.Split("\r\n")
                        .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        .Select(l => int.Parse(l[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                        .FirstOrDefault();
                    while (buffer.Count < length)
                    {
                        var n = await stream.ReadAsync(chunk, _stop.Token);
                        if (n == 0) return;
                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    }
                    buffer.RemoveRange(0, length);
                    Interlocked.Increment(ref _requests);

                    var (status, headers) = Respond();
                    var response = $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: {(Close ? "close" : "keep-alive")}\r\n"
                                   + string.Concat(headers.Select(h => h + "\r\n")) + "\r\n";
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), _stop.Token);
                    if (Close) return;
                }
            }
            catch (Exception) { /* the client went away, or the server stops */ }
        }

        private static int IndexOfHeadEnd(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { /* stopped */ }
            _stop.Dispose();
        }
    }
}

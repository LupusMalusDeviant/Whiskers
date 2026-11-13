using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Docker.DotNet;
using Docker.DotNet.X509;
using Whiskers.Models;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Docker;

public class DockerConnectionManager : IDockerConnectionManager
{
    private readonly IServerConfigService _serverConfig;
    private readonly ISshTunnelManager _sshTunnelManager;
    private readonly ILogger<DockerConnectionManager> _logger;
    // INV-59 (#187): a cached client is bound to the connection it was built for (CacheIdentityOf). A record that now
    // reaches another host never gets the old host's client, even when that client was published after the edit.
    private readonly ConcurrentDictionary<string, CachedClient> _clients = new();

    private sealed record CachedClient(DockerClient Client, string Identity);

    /// <summary>What a cached client is bound to: the host the record reaches
    /// (<see cref="Certificates.MtlsOnboardingState.ConnectionOf"/>) plus the transport settings the client was built
    /// with (TLS material, SSH user). Any difference means the cached client is not this record's client.</summary>
    internal static string CacheIdentityOf(Models.ServerConfig server) =>
        string.Join('\u001e', Certificates.MtlsOnboardingState.ConnectionOf(server),
            server.TcpUseTls ? "tls" : "", server.TcpClientCertPath ?? "", server.TcpClientKeyPath ?? "",
            server.TcpCaCertPath ?? "", server.SshUser ?? "");

    // Serializes client/tunnel creation per server so concurrent callers (the several background
    // pollers) can't each spawn a tunnel for the same server at once — that would leak orphaned
    // ssh processes and waste local ports.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private readonly Budget.IServerBudget _budget;
    private readonly Budget.IServerCircuitBreaker _circuit;
    private readonly Observability.ILoopSuspensionService _suspension;

    // Contract 0015 [mTLS layer]: the client key is loaded through the vault (sealed at rest), never from a PEM path.
    private readonly Certificates.IMtlsClientKeyLoader? _keyLoader;

    public DockerConnectionManager(
        IServerConfigService serverConfig,
        ISshTunnelManager sshTunnelManager,
        Budget.IServerBudget budget,
        Budget.IServerCircuitBreaker circuit,
        Observability.ILoopSuspensionService suspension,
        ILogger<DockerConnectionManager> logger,
        Certificates.IMtlsClientKeyLoader? keyLoader = null)
    {
        _keyLoader = keyLoader;
        _serverConfig = serverConfig;
        _sshTunnelManager = sshTunnelManager;
        _budget = budget;
        _circuit = circuit;
        _suspension = suspension;
        _logger = logger;
    }

    /// <summary>Tests only: where the budgeted paths take their client from instead of building one (the test project
    /// cannot load the Docker client this assembly builds). Null in the app.</summary>
    internal Func<string?, Task<DockerClient>>? ClientSourceForTests { get; init; }

    /// <summary>Tests only: awaited inside the budget slot, with the call's deadline, before a retried operation reaches
    /// the client — stands in for a server that was sent the request and does not answer. Null in the app.</summary>
    internal Func<CancellationToken, Task>? SentRequestForTests { get; init; }

    /// <summary>Tests only: builds the client in place of <c>CreateClientAsync</c>, and disposes one (the test project
    /// cannot load the Docker client configuration). Null in the app.</summary>
    internal Func<Models.ServerConfig, Task<DockerClient>>? BuildClientForTests { get; init; }

    /// <inheritdoc cref="BuildClientForTests"/>
    internal Action<DockerClient>? DisposeClientForTests { get; init; }

    /// <summary>The record with exactly this id, or null: never another server found by its display name.</summary>
    private Models.ServerConfig? Exact(string id) =>
        _serverConfig.GetServer(id) is { } server && string.Equals(server.Id, id, StringComparison.Ordinal) ? server : null;

    private void DisposeClient(DockerClient client)
    {
        if (DisposeClientForTests is { } dispose) dispose(client);
        else client.Dispose();
    }

    private Task<DockerClient> AcquireClientAsync(string? serverId) =>
        ClientSourceForTests?.Invoke(serverId) ?? GetClientAsync(serverId);

    /// <summary>R1-41: the wait for a client ends on the caller's cancellation and on the server's deadline. Building the
    /// client is shared with every other caller of that server and is not cancelled — only this call stops waiting.</summary>
    private static async Task<DockerClient> AwaitClientAsync(Task<DockerClient> acquiring, CancellationToken callerToken,
        CancellationToken deadline)
    {
        if (!deadline.CanBeCanceled) return await acquiring.WaitAsync(callerToken);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(callerToken, deadline);
        return await acquiring.WaitAsync(either.Token);
    }

    /// <summary>
    /// Get or create a DockerClient for the given server.
    /// If serverId is null, returns the default server's client.
    /// </summary>
    public async Task<DockerClient> GetClientAsync(string? serverId = null)
    {
        var server = serverId != null
            ? _serverConfig.GetServer(serverId)
            : _serverConfig.GetDefaultServer();

        if (server == null)
            throw new InvalidOperationException($"Server '{serverId ?? "default"}' not found");

        // A Kubernetes cluster is not a Docker host — any Docker-path caller reaching this is a
        // routing bug (workloads go through Services/Workloads). Fail loud instead of trying to
        // build a Docker transport out of kube fields.
        // The emergency stop, at the one point every Docker call passes through (Plan-0005 WP1). Background
        // traffic to a paused server stops here; interactive traffic does not, so the operator can still look
        // at the server they just paused. During the 2026-08-26 incident there was no way to do this from
        // inside Whiskers at all — the fix had to travel over SSH, past the tool causing the problem.
        if (_budget.IsBackgroundCall && _suspension.IsSuspended(server.Id))
            throw new Observability.ServerSuspendedException(server.Id, "emergency stop");

        if (server.ConnectionType == ConnectionType.Kubernetes)
            throw new InvalidOperationException(
                $"Server '{server.Name}' is a Kubernetes cluster — handled by the workload seam, not the Docker connection manager.");

        // Fast path: a cached client whose underlying transport is still alive.
        if (TryGetLiveClient(server, out var live))
            return live!;

        // Slow path: build (or rebuild) the client under a per-server lock so we never create two
        // tunnels for the same server concurrently.
        var gate = _locks.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // The record as it is now: a caller that read it before the wait must neither drop a client another caller
            // built for the record's current connection nor build one for its earlier connection (Codex round 6).
            // Exactly this id (Codex round 7): the registry's name fallback would answer with another server once this one
            // is removed and another is named like its id.
            server = Exact(server.Id)
                ?? throw new InvalidOperationException($"Server '{serverId ?? "default"}' not found");
            if (server.ConnectionType == ConnectionType.Kubernetes)
                throw new InvalidOperationException(
                    $"Server '{server.Name}' is a Kubernetes cluster — handled by the workload seam, not the Docker connection manager.");

            // Re-check under the lock — another caller may have just built it.
            if (TryGetLiveClient(server, out var live2))
                return live2!;

            // Tear down any stale client + dead tunnel before rebuilding. The tunnel goes even without a cached client:
            // the tunnel manager reuses a live tunnel by server id, whatever host it leads to (INV-59).
            InvalidateClient(server.Id);
            _sshTunnelManager?.CloseTunnel(server.Id);

            var identity = CacheIdentityOf(server);
            var client = await (BuildClientForTests?.Invoke(server) ?? CreateClientAsync(server));
            // The record may have moved to another host while the client was built (an edit's invalidation found
            // nothing to drop then). Such a client is never published; the caller retries against the current record.
            var current = Exact(server.Id);
            if (current is null || !string.Equals(CacheIdentityOf(current), identity, StringComparison.Ordinal))
            {
                DisposeClient(client);
                _sshTunnelManager?.CloseTunnel(server.Id);
                throw new InvalidOperationException(
                    $"The connection of server '{server.Name}' changed while its client was built; try again.");
            }
            _clients[server.Id] = new CachedClient(client, identity);
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Returns a cached client only if its transport is still usable. For SSH servers the client
    /// points at a local SSH-tunnel port; if that tunnel has died (network blip, remote sshd
    /// restart, keepalive timeout) the cached client is pinned to a dead port and every call fails
    /// with "connection refused" forever. Treating a dead tunnel as "not live" forces a rebuild on
    /// the next call, so the app self-heals within one poll cycle instead of needing a restart.
    /// </summary>
    private bool TryGetLiveClient(Models.ServerConfig server, out DockerClient? client)
    {
        if (_clients.TryGetValue(server.Id, out var cached))
        {
            // INV-59 (#187): a client built for another connection than the record's is not this record's client.
            if (!string.Equals(cached.Identity, CacheIdentityOf(server), StringComparison.Ordinal))
            {
                _logger.LogInformation("Connection of '{ServerName}' changed; its client will be rebuilt", server.Name);
                client = null;
                return false;
            }
            client = cached.Client;
            if (server.ConnectionType != ConnectionType.SSH || _sshTunnelManager.IsTunnelActive(server.Id))
                return true;
            _logger.LogWarning("SSH tunnel for '{ServerName}' is no longer alive; connection will be rebuilt", server.Name);
        }
        client = null;
        return false;
    }

    /// <inheritdoc />
    public async Task<T> ExecuteGuardedAsync<T>(string? serverId, Func<DockerClient, Task<T>> operation, string? singleFlightKey = null,
        CancellationToken callerToken = default, CancellationToken deadline = default)
    {
        var budgetKey = serverId ?? _serverConfig.GetDefaultServer()?.Id ?? "local";
        _circuit.ThrowIfOpen(budgetKey);
        try
        {
            var client = await AwaitClientAsync(AcquireClientAsync(serverId), callerToken, deadline);
            try
            {
                var result = await _budget.RunAsync(budgetKey, () => operation(client), callerToken, singleFlightKey, deadline);
                _circuit.RecordSuccess(budgetKey);
                return result;
            }
            catch (Exception ex) when (ex is not Budget.DuplicateRequestException
                                       and not Budget.BudgetSaturatedException
                                       && !callerToken.IsCancellationRequested)
            {
                // A discarded duplicate is not a health signal — the server never saw it. Neither is a call that
                // ran out of time in our own queue: blaming the host for our backlog opens circuits across the
                // whole fleet at once, which is what happened on 2026-08-27. Nor is a call whose client Whiskers
                // replaced while it ran (see IsSupersededClientFailure).
                if (!IsSupersededClientFailure(budgetKey, client, ex))
                    _circuit.RecordFailure(budgetKey, ex);
                throw;
            }
        }
        finally
        {
            // If this call was the half-open probe and ended without a verdict (client build threw, duplicate,
            // saturation), free the slot — otherwise the server stays "paused" until Whiskers restarts.
            _circuit.ReleaseProbe(budgetKey);
        }
    }

    /// <summary>
    /// Runs a Docker operation under the budget and circuit breaker and, if it fails with a transport-level
    /// error (a dead tunnel that died mid-flight, a half-open connection the liveness check couldn't catch),
    /// invalidates the connection and retries exactly once against a freshly established tunnel.
    ///
    /// <para><b>Not every Docker call comes through here yet.</b> Several operation classes still take a bare
    /// client from <c>GetClientAsync</c> and are therefore outside the budget — see
    /// <c>DockerBudgetCoverageTests</c>, which pins the remaining list so it can only shrink. The log fetch,
    /// the call behind the 2026-08-26 incident, was moved onto <see cref="ExecuteGuardedAsync{T}"/> first.</para>
    /// </summary>
    public async Task<T> ExecuteAsync<T>(string? serverId, Func<DockerClient, Task<T>> operation, string? singleFlightKey = null,
        CancellationToken callerToken = default, CancellationToken deadline = default)
    {
        var budgetKey = serverId ?? _serverConfig.GetDefaultServer()?.Id ?? "local";

        // Fail fast while the circuit is open: a host that answers nothing does not get healthier from being
        // asked again by five loops every cycle, and the attempt costs Whiskers a slot it could use elsewhere.
        _circuit.ThrowIfOpen(budgetKey);
        try
        {
            return await ExecuteWithRetryAsync(serverId, budgetKey, operation, singleFlightKey, callerToken, deadline);
        }
        finally
        {
            // Same as in ExecuteGuardedAsync: a probe that ended without a verdict (a DockerApiException the
            // catches below don't handle, a failing client build) must not keep the slot.
            _circuit.ReleaseProbe(budgetKey);
        }
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        string? serverId, string budgetKey, Func<DockerClient, Task<T>> operation, string? singleFlightKey,
        CancellationToken callerToken, CancellationToken deadline)
    {
        var client = await AwaitClientAsync(AcquireClientAsync(serverId), callerToken, deadline);
        try
        {
            Func<Task<T>> call = SentRequestForTests is { } sent
                ? async () => { await sent(deadline); return await operation(client); }
                : () => operation(client);
            var result = await _budget.RunAsync(budgetKey, call, callerToken, singleFlightKey, deadline);
            _circuit.RecordSuccess(budgetKey);
            return result;
        }
        // R1-41: the caller stopped waiting (a chat closed, a preview cancelled). Nothing about the server: no retry, no
        // circuit failure.
        catch (Exception) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        // Saturation is not a connection failure even when it wraps a TimeoutException (the chain walk below would
        // otherwise rebuild a healthy tunnel and retry a request that only waited in Whiskers' own queue).
        catch (Exception ex) when (ex is not Budget.BudgetSaturatedException && IsConnectionFailure(ex))
        {
            var id = serverId
                ?? _serverConfig.GetDefaultServer()?.Id
                ?? throw new InvalidOperationException("No default server configured");
            _logger.LogWarning(ex,
                "Docker operation on '{ServerId}' failed with a connection error; rebuilding tunnel and retrying once", id);
            // Invalidate only the exact client instance this call used — a parallel caller may have
            // already rebuilt a fresh client for this server, and we must not tear that healthy one down.
            InvalidateClient(id, ifCurrent: client);
            client = await AwaitClientAsync(AcquireClientAsync(serverId), callerToken, deadline);
            try
            {
                // The retry takes a slot of its own: it is a second request the server has to serve.
                var retried = await _budget.RunAsync(id, () => operation(client), callerToken, singleFlightKey, deadline);
                _circuit.RecordSuccess(id);
                return retried;
            }
            // A throw out of this catch is not seen by the saturation catch below: the retry logs its own.
            catch (Budget.BudgetSaturatedException saturated)
            {
                LogSaturation(id, saturated);
                throw;
            }
            catch (Exception retryFailure) when (retryFailure is not Budget.DuplicateRequestException
                                                 and not Budget.BudgetSaturatedException
                                                 && !callerToken.IsCancellationRequested)
            {
                // Only the failure AFTER a fresh tunnel counts towards the circuit — and, as for the first attempt, not
                // a discarded duplicate or a retry that ran out of time in Whiskers' own queue (R1-41 follow-up). The
                // first one is exactly the transient the retry exists for; counting both would open the circuit twice
                // as fast as configured and pause a server that is merely reconnecting.
                if (!IsSupersededClientFailure(id, client, retryFailure))
                    _circuit.RecordFailure(id, retryFailure);
                throw;
            }
        }
        // Saturation first: a deadline that expired in our own queue is a statement about this budget, not
        // about the host, and must not reach the circuit below.
        catch (Budget.BudgetSaturatedException ex)
        {
            LogSaturation(budgetKey, ex);
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Our own deadline expiring AT THE HOST is a statement about its responsiveness, so it counts —
            // that is the signal the 2026-08-26 incident produced for six days and nobody tallied. The
            // saturation case above is deliberately excluded: same exception, opposite meaning.
            _circuit.RecordFailure(budgetKey, ex);
            throw;
        }
    }

    private void LogSaturation(string server, Budget.BudgetSaturatedException ex)
    {
        if (ex.Ran == TimeSpan.Zero)
            _logger.LogWarning(
                "Call to {Server} gave up while queued ({Waited:F1}s waiting). This is Whiskers being the " +
                "bottleneck — the server was never asked.",
                server, ex.Waited.TotalSeconds);
        else
            _logger.LogWarning(
                "Call to {Server} ran out of time after a long queue ({Waited:F1}s waiting, {Ran:F1}s running at " +
                "the server). The request was sent; most of its time went into Whiskers' own queue.",
                server, ex.Waited.TotalSeconds, ex.Ran.TotalSeconds);
    }

    /// <summary>True when <paramref name="ex"/> comes from a client Whiskers itself disposed and replaced while the
    /// call was in flight: an <see cref="ObjectDisposedException"/> in the chain, and <paramref name="used"/> is no
    /// longer the cached client for <paramref name="serverId"/>. That says nothing about the host. Counting it let one
    /// <see cref="InvalidateClient"/> (the mTLS switch check rebuilds the connection on purpose) fail every open call
    /// at once, open the circuit and refuse the very check that caused it (2026-11-10, Infomaniak). A disposed client
    /// that is still the cached one is not covered: nothing replaced it, so the failure counts as before.</summary>
    internal bool IsSupersededClientFailure(string serverId, DockerClient used, Exception ex) =>
        IsSupersededClientFailure(ex, used, _clients.TryGetValue(serverId, out var current) ? current.Client : null);

    /// <summary>The rule of <see cref="IsSupersededClientFailure(string, DockerClient, Exception)"/> with the cached
    /// client passed in (<c>null</c>: none cached).</summary>
    internal static bool IsSupersededClientFailure(Exception ex, DockerClient? used, DockerClient? current)
    {
        var disposed = false;
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is ObjectDisposedException) { disposed = true; break; }
        }
        return disposed && (current is null || !ReferenceEquals(current, used));
    }

    /// <summary>True when the exception (or any inner exception) is a transport-level failure that a
    /// tunnel rebuild + retry can recover from. Includes <see cref="ObjectDisposedException"/>: a client
    /// disposed out from under an in-flight call must retry against a fresh one, not surface the dispose.</summary>
    public static bool IsConnectionFailure(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException or HttpRequestException or TimeoutException or IOException or ObjectDisposedException)
                return true;
        }
        return false;
    }

    /// <summary>Tears down a cached client + its tunnel. When <paramref name="ifCurrent"/> is supplied the
    /// removal is instance-aware — it disposes only if the cached client is still that exact instance
    /// (atomic <c>TryRemove(KeyValuePair)</c>), so a retry can't dispose a fresh client another caller just
    /// built. <c>null</c> = unconditional teardown (used under the build lock before rebuilding).</summary>
    public void InvalidateClient(string serverId, DockerClient? ifCurrent = null)
    {
        if (ifCurrent is null)
        {
            if (_clients.TryRemove(serverId, out var cached))
            {
                DisposeClient(cached.Client);
                _sshTunnelManager.CloseTunnel(serverId);
            }
            return;
        }

        // Only remove+dispose when the cached value is still the instance the caller used.
        if (_clients.TryGetValue(serverId, out var current) && ReferenceEquals(current.Client, ifCurrent)
            && _clients.TryRemove(new KeyValuePair<string, CachedClient>(serverId, current)))
        {
            DisposeClient(ifCurrent);
            _sshTunnelManager.CloseTunnel(serverId);
        }
    }

    private async Task<DockerClient> CreateClientAsync(Models.ServerConfig server)
    {
        switch (server.ConnectionType)
        {
            case ConnectionType.Local:
                return new DockerClientConfiguration(new Uri(server.SocketPath)).CreateClient();

            case ConnectionType.TCP:
            {
                var uri = new Uri(server.TcpUseTls
                    ? $"https://{server.TcpHost}:{server.TcpPort}"
                    : $"http://{server.TcpHost}:{server.TcpPort}");
                // mTLS path: present a client cert and verify the server against the CA. No SSH key.
                if (server.TcpUseTls && !string.IsNullOrEmpty(server.TcpClientCertPath))
                    return new DockerClientConfiguration(uri, BuildMtlsCredentials(server, _keyLoader
                        ?? throw new InvalidOperationException("No mTLS client key loader is configured."))).CreateClient();
                return new DockerClientConfiguration(uri).CreateClient();
            }

            case ConnectionType.SSH:
            {
                var localPort = await _sshTunnelManager.EstablishTunnelAsync(server);
                return new DockerClientConfiguration(new Uri($"http://127.0.0.1:{localPort}")).CreateClient();
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(server.ConnectionType));
        }
    }

    /// <summary>
    /// Builds mutual-TLS credentials for the TCP path: presents the client certificate and verifies
    /// the server's certificate chain against the configured CA (custom root trust, no reliance on
    /// the system trust store). The client certificate and its key come from <paramref name="keyLoader"/> (contract
    /// 0015: the key is sealed at rest and only ever in memory here), round-tripped through PKCS#12 so the private
    /// key is usable for TLS client auth across platforms.
    /// </summary>
    internal static CertificateCredentials BuildMtlsCredentials(Models.ServerConfig server, Certificates.IMtlsClientKeyLoader keyLoader)
    {
        ArgumentNullException.ThrowIfNull(keyLoader);
        var clientCert = keyLoader.Load(server);

        var credentials = new CertificateCredentials(clientCert);

        if (!string.IsNullOrEmpty(server.TcpCaCertPath))
        {
            // Load ALL certs from the CA file (root, and intermediate if the file is a bundle).
            var trustAnchors = new X509Certificate2Collection();
            trustAnchors.ImportFromPemFile(server.TcpCaCertPath);
            credentials.ServerCertificateValidationCallback = (_, cert, presentedChain, _) =>
            {
                if (cert is null) return false;
                var cert2 = cert as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
                return ValidateMtlsServerCert(cert2, trustAnchors, presentedChain, server.TcpHost);
            };
        }

        return credentials;
    }

    /// <summary>Validates the server's mTLS certificate: the chain must build to the configured custom CA
    /// AND the certificate's identity must match the host we intended to reach (NIED-13). Without the
    /// hostname check any certificate our CA signed would be accepted, letting a MITM impersonate the
    /// Docker host. Fails closed on an empty expected host.</summary>
    public static bool ValidateMtlsServerCert(
        X509Certificate2 serverCert, X509Certificate2Collection trustAnchors, X509Chain? presentedChain, string? expectedHost)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustAnchors);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // Also trust any intermediates the server presents, so the chain can build to the root.
        if (presentedChain is not null)
            foreach (var element in presentedChain.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);

        var chainOk = chain.Build(serverCert);
        var hostnameOk = !string.IsNullOrEmpty(expectedHost) && serverCert.MatchesHostname(expectedHost);
        return chainOk && hostnameOk;
    }

    public void Dispose()
    {
        foreach (var cached in _clients.Values)
            DisposeClient(cached.Client);
        _clients.Clear();

        foreach (var gate in _locks.Values)
            gate.Dispose();
        _locks.Clear();
    }
}

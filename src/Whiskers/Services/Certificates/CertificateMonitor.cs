using System.Security.Cryptography.X509Certificates;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Services.Server;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Services.Certificates;

/// <summary>
/// Watches every certificate Whiskers depends on and warns 30, 14 and 7 days before it expires, and once more
/// when it has.
///
/// <para><b>Why.</b> The fleet's Docker plane runs over mTLS: one expired client certificate or CA and Whiskers
/// loses every SSH-free server at once, and the first sign would be a wall of "server unreachable". The certbot
/// certificates were visible in the UI and over MCP, but only to someone who went looking.</para>
///
/// <para>Covers Whiskers' own client certificate and CA bundle (read from disk), the certificate each mTLS
/// proxy presents (read from the handshake) and certbot's certificates on every enabled server.</para>
/// </summary>
public sealed class CertificateMonitor : FleetBackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly IServerConfigService _servers;
    private readonly ISslCertService _certbot;
    private readonly INotificationService _notifications;
    private readonly Observability.SelfMetrics.ISelfMetrics _selfMetrics;
    private readonly CertificateAlertState _state;
    private readonly ILogger<CertificateMonitor> _logger;

    public CertificateMonitor(
        IServerConfigService servers,
        ISslCertService certbot,
        INotificationService notifications,
        Observability.SelfMetrics.ISelfMetrics selfMetrics,
        ILogger<CertificateMonitor> logger,
        DataPathOptions? dataPaths = null)
    {
        _servers = servers;
        _certbot = certbot;
        _notifications = notifications;
        _selfMetrics = selfMetrics;
        _logger = logger;
        _state = new CertificateAlertState((dataPaths ?? DataPathOptions.Default).CertificateAlertsJson);
    }

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            var success = true;
            try
            {
                var observed = await CollectAsync(stoppingToken);
                var now = DateTime.UtcNow;
                foreach (var (cert, stage) in _state.Advance(observed, now))
                    await _notifications.SendAsync(ToEvent(cert, stage, now));

                _logger.LogInformation("Certificate check: {Count} certificate(s) looked at", observed.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                success = false;
                _logger.LogError(ex, "Certificate check failed");
            }

            _selfMetrics.RecordCycle("certificates", "*", DateTime.UtcNow - startedAt, success, Interval);

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task<List<ObservedCertificate>> CollectAsync(CancellationToken ct)
    {
        var observed = new List<ObservedCertificate>();
        var servers = _servers.GetEnabledServers();

        // Whiskers' own files. Usually one client certificate and one CA bundle shared by every server.
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in servers.Where(s => s.TcpUseTls))
        {
            if (!string.IsNullOrWhiteSpace(s.TcpClientCertPath)) files.TryAdd(s.TcpClientCertPath, "mTLS client");
            if (!string.IsNullOrWhiteSpace(s.TcpCaCertPath)) files.TryAdd(s.TcpCaCertPath, "mTLS CA");
        }
        foreach (var (path, kind) in files)
        {
            try
            {
                foreach (var cert in CertificateProbe.ReadPemFile(path))
                    using (cert) observed.Add(Observe(cert, kind, path, null, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _logger.LogWarning("Could not read certificate file {Path}: {Error}", path, ex.Message);
            }
        }

        foreach (var s in servers)
        {
            ct.ThrowIfCancellationRequested();

            if (s.TcpUseTls && !string.IsNullOrWhiteSpace(s.TcpHost))
            {
                try
                {
                    using var cert = await CertificateProbe.FetchServerCertificateAsync(s.TcpHost, s.TcpPort, HandshakeTimeout, ct);
                    if (cert is not null) observed.Add(Observe(cert, "mTLS server", $"{s.TcpHost}:{s.TcpPort}", s.Id, s.Name));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Unreachable servers are the reachability tracker's business, not ours.
                    _logger.LogDebug("No server certificate from {Server}: {Error}", s.Name, ex.Message);
                }
            }

            try
            {
                foreach (var c in await _certbot.ListCertificatesAsync(s.Id))
                {
                    if (c.ExpiresAt is not { } expires) continue;
                    var name = string.IsNullOrWhiteSpace(c.CertName) ? c.Domain : c.CertName;
                    // certbot does not print a fingerprint; the expiry date changes on every renewal, which is
                    // what the alert state needs to start over.
                    observed.Add(new ObservedCertificate("certbot", name, $"certbot:{name}", s.Id, s.Name,
                        expires, expires.ToString("O")));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug("certbot listing failed on {Server}: {Error}", s.Name, ex.Message);
            }
        }

        return observed;
    }

    private static ObservedCertificate Observe(X509Certificate2 cert, string kind, string location, string? serverId, string? serverName) =>
        new(kind, cert.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } cn ? cn : cert.Subject,
            location, serverId, serverName, cert.NotAfter.ToUniversalTime(), cert.Thumbprint);

    internal static NotificationEvent ToEvent(ObservedCertificate cert, int stage, DateTime nowUtc) => new()
    {
        EventType = stage == 0 ? "certificate_expired" : "certificate_expiring",
        ServerId = cert.ServerId,
        ServerName = cert.ServerName ?? "Whiskers",
        ImageInfo = CertificateExpiry.Describe(cert, nowUtc),
        Timestamp = nowUtc,
    };
}

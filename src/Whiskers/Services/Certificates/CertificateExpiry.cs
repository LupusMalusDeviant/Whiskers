namespace Whiskers.Services.Certificates;

/// <summary>A certificate the monitor looked at: what it is, where it lives and when it stops working.</summary>
/// <param name="Kind">Human label, e.g. "mTLS client", "mTLS CA", "mTLS server", "certbot".</param>
/// <param name="Name">Subject or certbot certificate name.</param>
/// <param name="Location">File path, host:port or certbot name — whatever tells an operator which one to renew.</param>
/// <param name="ServerId">The server it belongs to; null for Whiskers' own files.</param>
/// <param name="Fingerprint">Identifies this exact certificate, so a renewed one starts with a clean alert state.</param>
public sealed record ObservedCertificate(
    string Kind, string Name, string Location, string? ServerId, string? ServerName,
    DateTime NotAfterUtc, string Fingerprint)
{
    public string AlertKey => $"{Kind}|{Location}|{Fingerprint}";
}

/// <summary>When a certificate is worth an alert. Each stage is announced once: a daily "still 25 days" is noise
/// that teaches people to ignore the channel, while the step from 14 to 7 days is news.</summary>
public static class CertificateExpiry
{
    /// <summary>The stages, loosest first. 0 means expired.</summary>
    public static readonly IReadOnlyList<int> Stages = new[] { 30, 14, 7, 0 };

    /// <summary>The tightest stage the certificate has reached, or null while it has more than 30 days left.</summary>
    public static int? StageFor(DateTime notAfterUtc, DateTime nowUtc)
    {
        var left = notAfterUtc - nowUtc;
        if (left <= TimeSpan.Zero) return 0;
        int? stage = null;
        foreach (var s in Stages)
            if (s > 0 && left <= TimeSpan.FromDays(s)) stage = s;
        return stage;
    }

    /// <summary>Alert when a stage is reached that is tighter than the last one announced.</summary>
    public static bool ShouldAlert(int? stage, int? lastAnnounced) =>
        stage is { } s && (lastAnnounced is not { } last || s < last);

    public static string Describe(ObservedCertificate cert, DateTime nowUtc)
    {
        var date = cert.NotAfterUtc.ToString("yyyy-MM-dd HH:mm 'UTC'");
        var left = cert.NotAfterUtc - nowUtc;
        var when = left <= TimeSpan.Zero
            ? $"expired on {date}"
            : $"expires on {date} (in {Math.Max(0, (int)left.TotalDays)} days)";
        var where = string.IsNullOrWhiteSpace(cert.ServerName) ? "" : $" @ {cert.ServerName}";
        return $"{cert.Kind} certificate {cert.Name}{where} ({cert.Location}) {when}";
    }
}

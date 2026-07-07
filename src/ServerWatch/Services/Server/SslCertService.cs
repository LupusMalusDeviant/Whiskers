using System.Globalization;
using System.Text.RegularExpressions;

namespace ServerWatch.Services.Server;

public class SslCertificate
{
    public string Domain { get; set; } = "";
    public string CertName { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime IssuedAt { get; set; }
    public int DaysUntilExpiry => (int)(ExpiresAt - DateTime.UtcNow).TotalDays;
    public bool IsExpiringSoon => DaysUntilExpiry <= 14;
    public string Issuer { get; set; } = "";
    public List<string> Domains { get; set; } = new();
}

public class SslCertService : ISslCertService
{
    private readonly IHostCommandExecutor _executor;
    private readonly ILogger<SslCertService> _logger;

    // Only allow safe cert names (similar to domain names); must start with a letter/digit so a name can
    // never begin with '-' (option-injection into certbot).
    private static readonly Regex SafeCertNameRegex = new(@"^[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.Compiled);

    // Certbot expiry date format: "2024-03-15 12:00:00+00:00 (VALID: 89 days)" or similar
    private static readonly Regex ExpiryDateRegex = new(
        @"(\d{4}-\d{2}-\d{2})",
        RegexOptions.Compiled);

    public SslCertService(IHostCommandExecutor executor, ILogger<SslCertService> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<List<SslCertificate>> ListCertificatesAsync(string serverId)
    {
        var result = await _executor.ExecuteAsync(serverId,
            "certbot certificates 2>/dev/null",
            timeout: TimeSpan.FromSeconds(60));

        var certs = new List<SslCertificate>();

        if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
        {
            _logger.LogWarning("certbot certificates failed on {ServerId}: {Error}", serverId, result.Error);
            return certs;
        }

        // certbot output groups certificates separated by blank lines or dashes
        // Each certificate block looks like:
        //
        // Certificate Name: example.com
        //   Domains: example.com www.example.com
        //   Expiry Date: 2024-03-15 12:00:00+00:00 (VALID: 89 days)
        //   Certificate Path: /etc/letsencrypt/live/example.com/fullchain.pem
        //   Private Key Path: /etc/letsencrypt/live/example.com/privkey.pem

        SslCertificate? current = null;

        foreach (var rawLine in result.Output.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("Certificate Name:", StringComparison.OrdinalIgnoreCase))
            {
                if (current != null) certs.Add(current);

                current = new SslCertificate
                {
                    CertName = ExtractValue(line, "Certificate Name:"),
                };
                current.Domain = current.CertName;
                continue;
            }

            if (current == null) continue;

            if (line.StartsWith("Domains:", StringComparison.OrdinalIgnoreCase))
            {
                var domainsStr = ExtractValue(line, "Domains:");
                current.Domains = domainsStr
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

                if (current.Domains.Count > 0)
                    current.Domain = current.Domains[0];
                continue;
            }

            if (line.StartsWith("Expiry Date:", StringComparison.OrdinalIgnoreCase))
            {
                var dateStr = ExtractValue(line, "Expiry Date:");
                var dateMatch = ExpiryDateRegex.Match(dateStr);
                if (dateMatch.Success &&
                    DateTime.TryParseExact(dateMatch.Value, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry))
                {
                    current.ExpiresAt = expiry.ToUniversalTime();
                }
                continue;
            }

            if (line.StartsWith("Issuer:", StringComparison.OrdinalIgnoreCase))
            {
                current.Issuer = ExtractValue(line, "Issuer:");
                continue;
            }
        }

        if (current != null) certs.Add(current);

        _logger.LogDebug("Found {Count} SSL certificates on {ServerId}", certs.Count, serverId);
        return certs;
    }

    public async Task<CommandResult> RenewAsync(string serverId, string certName)
    {
        ValidateCertName(certName);

        _logger.LogInformation("Renewing certificate '{CertName}' on {ServerId}", certName, serverId);
        return await _executor.ExecuteAsync(serverId,
            $"certbot renew --cert-name {certName} --non-interactive 2>&1",
            timeout: TimeSpan.FromMinutes(5));
    }

    public async Task<CommandResult> RenewAllAsync(string serverId)
    {
        _logger.LogInformation("Renewing all certificates on {ServerId}", serverId);
        return await _executor.ExecuteAsync(serverId,
            "certbot renew --non-interactive 2>&1",
            timeout: TimeSpan.FromMinutes(10));
    }

    // --- Helpers ---

    private static void ValidateCertName(string certName)
    {
        if (string.IsNullOrWhiteSpace(certName))
            throw new ArgumentException("Certificate name cannot be null or empty", nameof(certName));

        if (!SafeCertNameRegex.IsMatch(certName))
            throw new ArgumentException(
                $"Invalid certificate name '{certName}'. Only letters, digits, dots, hyphens, and underscores are allowed.",
                nameof(certName));
    }

    private static string ExtractValue(string line, string prefix)
    {
        var idx = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        return line[(idx + prefix.Length)..].Trim();
    }
}

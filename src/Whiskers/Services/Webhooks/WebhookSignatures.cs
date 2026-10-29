using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Whiskers.Models;

namespace Whiskers.Services.Webhooks;

/// <summary>An incoming delivery as the endpoint read it: the raw body and the headers the providers sign with.</summary>
public sealed record WebhookRequest(
    byte[] Body,
    string? GitHubSignature = null,
    string? WhiskersSignature = null,
    string? WhiskersTimestamp = null,
    string? WhiskersDelivery = null,
    string? SourceIp = null);

/// <summary>
/// The signature and freshness rules per provider (R1-10, INV-42). Each rule says what is signed, how a delivery is
/// told apart from a replay and how fresh it must be — and nothing unsigned decides either.
/// <list type="bullet">
/// <item><b>github</b> — <c>X-Hub-Signature-256: sha256=HMAC(secret, body)</c>. GitHub signs no time and no delivery
/// id (<c>X-GitHub-Delivery</c> is unsigned and ignored), so freshness cannot be proven: a delivery is identified by
/// the hash of its signed body, and the same body never runs twice within the retention period of
/// <see cref="WebhookOptions.DeliveryRetention"/>.</item>
/// <item><b>whiskers</b> — <c>X-Whiskers-Timestamp</c> (Unix seconds), <c>X-Whiskers-Delivery</c> (a unique id) and
/// <c>X-Whiskers-Signature: sha256=HMAC(secret, "{timestamp}.{delivery}.{body}")</c>. A timestamp further than
/// <see cref="WebhookOptions.TimestampTolerance"/> from now is refused, and the signed delivery id runs once.</item>
/// </list>
/// </summary>
public static class WebhookSignatures
{
    public const string GitHub = "github";
    public const string Whiskers = "whiskers";

    /// <summary>The providers a webhook can be set to.</summary>
    public static readonly IReadOnlyList<string> Providers = [GitHub, Whiskers];

    private static readonly Regex DeliveryIdPattern = new("^[A-Za-z0-9._-]{8,128}$", RegexOptions.CultureInvariant);

    /// <summary>Checks the delivery against the webhook's provider rule. The key that identifies the delivery when it
    /// is valid; otherwise <c>null</c> and why (for the log only — the caller gets a generic answer).</summary>
    public static (string? DeliveryKey, string? Problem) Verify(WebhookEntity webhook, WebhookRequest request,
        DateTimeOffset now, TimeSpan tolerance)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(webhook.Secret)) return (null, "the webhook has no secret");
        if (request.Body.Length == 0) return (null, "the body is empty");
        switch (webhook.Provider)
        {
            case GitHub:
                if (!Matches(request.GitHubSignature, Sign(webhook.Secret, request.Body)))
                    return (null, "invalid or missing X-Hub-Signature-256");
                return ("body:" + Convert.ToHexStringLower(SHA256.HashData(request.Body)), null);

            case Whiskers:
                if (string.IsNullOrEmpty(request.WhiskersTimestamp) || string.IsNullOrEmpty(request.WhiskersDelivery))
                    return (null, "X-Whiskers-Timestamp or X-Whiskers-Delivery is missing");
                if (!DeliveryIdPattern.IsMatch(request.WhiskersDelivery))
                    return (null, "X-Whiskers-Delivery is not a valid delivery id");
                if (!long.TryParse(request.WhiskersTimestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    return (null, "X-Whiskers-Timestamp is not a Unix time");
                // Signed first: an unsigned timestamp says nothing, so it is judged only once it is authentic.
                if (!Matches(request.WhiskersSignature,
                        Sign(webhook.Secret, SignedPayload(request.WhiskersTimestamp, request.WhiskersDelivery, request.Body))))
                    return (null, "invalid or missing X-Whiskers-Signature");
                var sent = seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.MaxValue
                    : DateTimeOffset.FromUnixTimeSeconds(seconds);
                if ((now - sent).Duration() > tolerance)
                    return (null, "the timestamp is outside the allowed window");
                return ("id:" + request.WhiskersDelivery, null);

            default:
                return (null, $"unknown provider '{webhook.Provider}'");
        }
    }

    /// <summary>A signed test delivery for <paramref name="webhook"/>, as its provider would send it.</summary>
    public static WebhookRequest SignedTest(WebhookEntity webhook, DateTimeOffset now, string sourceIp)
    {
        var delivery = Guid.NewGuid().ToString("N");
        // The id in the body makes every test a new delivery for a provider that identifies it by its body.
        var body = Encoding.UTF8.GetBytes(
            $"{{\"test\":true,\"webhookId\":\"{webhook.WebhookId}\",\"delivery\":\"{delivery}\",\"timestamp\":\"{now.UtcDateTime:O}\"}}");
        if (webhook.Provider == Whiskers)
        {
            var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            return new WebhookRequest(body, WhiskersSignature: Sign(webhook.Secret, SignedPayload(timestamp, delivery, body)),
                WhiskersTimestamp: timestamp, WhiskersDelivery: delivery, SourceIp: sourceIp);
        }
        return new WebhookRequest(body, GitHubSignature: Sign(webhook.Secret, body), SourceIp: sourceIp);
    }

    /// <summary><c>sha256=</c> and the lowercase hex HMAC-SHA256 of <paramref name="payload"/>.</summary>
    public static string Sign(string secret, byte[] payload) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));

    /// <summary>What the whiskers provider signs: <c>{timestamp}.{delivery}.</c> followed by the raw body.</summary>
    public static byte[] SignedPayload(string timestamp, string delivery, byte[] body) =>
        [.. Encoding.UTF8.GetBytes($"{timestamp}.{delivery}."), .. body];

    /// <summary>Constant-time comparison, so the time it takes says nothing about the expected signature.</summary>
    private static bool Matches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var a = Encoding.UTF8.GetBytes(provided.Trim());
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

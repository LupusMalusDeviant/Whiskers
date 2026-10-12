using System.Security.Cryptography;
using System.Text;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

public class WebhookSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"event\":\"server_unreachable\"}");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_secret_nothing_is_signed(string? secret)
    {
        Assert.Empty(WebhookNotificationService.SignatureHeaders(secret, Body, Now));
    }

    [Fact]
    public void V2_signs_timestamp_and_body_so_a_replay_with_a_new_timestamp_fails()
    {
        var headers = WebhookNotificationService.SignatureHeaders("s3cret", Body, Now).ToDictionary(h => h.Name, h => h.Value);

        Assert.Equal("1800000000", headers["X-Webhook-Timestamp"]);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes("1800000000.").Concat(Body).ToArray()));
        Assert.Equal(expected, headers["X-Webhook-Signature-V2"]);

        var later = WebhookNotificationService.SignatureHeaders("s3cret", Body, Now.AddMinutes(10))
            .Single(h => h.Name == "X-Webhook-Signature-V2").Value;
        Assert.NotEqual(expected, later);
    }

    [Fact]
    public void GitHub_style_header_is_the_hex_hmac_of_the_body()
    {
        var hub = WebhookNotificationService.SignatureHeaders("s3cret", Body, Now)
            .Single(h => h.Name == "X-Hub-Signature-256").Value;
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Body)), hub);
    }
}

using Whiskers.Models;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>Matrix messages carry an HTML body. Since the log-alert detail now includes the matched log
/// line, third-party text reaches that body — it must be encoded, or a log line can inject markup into the
/// room (and a stray "&lt;" silently swallows the rest of the message).</summary>
public class NotificationRenderingTests
{
    private static NotificationEvent LogAlert(string detail) => new()
    {
        EventType = "log_alert:error",
        ContainerName = "web",
        ServerName = "Host-D",
        ImageInfo = detail
    };

    [Fact]
    public void The_html_body_encodes_the_matched_log_line()
    {
        var (_, html) = MatrixNotificationService.FormatMessage(
            LogAlert("rule · web @ Host-D — FATAL <img src=x onerror=alert(1)> & done"));

        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
        Assert.Contains("&amp; done", html);
    }

    [Fact]
    public void The_plain_body_keeps_the_line_verbatim()
    {
        var (plain, _) = MatrixNotificationService.FormatMessage(LogAlert("FATAL <tag> & more"));
        Assert.Contains("FATAL <tag> & more", plain);
    }

    [Fact]
    public void The_markup_of_the_message_itself_survives_encoding()
    {
        // Only the interpolated values are escaped — the template's own tags must stay real markup.
        var (_, html) = MatrixNotificationService.FormatMessage(LogAlert("plain detail"));
        Assert.Contains("<strong>Log-Alert</strong>", html);
        Assert.Contains("<code>web</code>", html);
    }

    [Fact]
    public void A_container_name_with_markup_cannot_break_out()
    {
        var (_, html) = MatrixNotificationService.FormatMessage(new NotificationEvent
        {
            EventType = "unhealthy",
            ContainerName = "</code><b>pwned</b>",
            Image = "img:1"
        });

        Assert.DoesNotContain("<b>pwned</b>", html);
        Assert.Contains("&lt;/code&gt;", html);
    }

    [Fact]
    public void Server_events_render_with_the_host_name()
    {
        var (plain, html) = MatrixNotificationService.FormatMessage(new NotificationEvent
        {
            EventType = "server_unreachable",
            ServerId = "host-d",
            ServerName = "Host-D (Hetzner)",
            ImageInfo = "Host-D (Hetzner) — Connection failed"
        });

        Assert.Contains("Server Unreachable", plain);
        Assert.Contains("Host-D (Hetzner)", plain);
        Assert.Contains("<strong>Server Unreachable</strong>", html);
    }

    [Fact]
    public void The_shared_detail_line_names_the_server()
    {
        // NotificationFormatter feeds Telegram/Ntfy/Discord/Email/Webhook and the in-app feed.
        var detail = NotificationFormatter.Detail(new NotificationEvent
        {
            EventType = "stopped",
            ContainerName = "web",
            ServerName = "Host-D",
            Image = "img:1"
        });

        Assert.Contains("web", detail);
        Assert.Contains("@ Host-D", detail);
    }

    [Fact]
    public void Server_events_get_their_own_title_and_link()
    {
        var evt = new NotificationEvent { EventType = "server_unreachable", ServerId = "host-d" };
        var (title, severity) = NotificationFormatter.Describe(evt);

        Assert.Equal("Server unreachable", title);
        Assert.Equal("Error", severity);
        Assert.Equal("servers", NotificationFormatter.LinkFor(evt));
    }
}

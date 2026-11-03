using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.Notifications;
using Whiskers.Utils;

namespace Whiskers.Tests;

/// <summary>
/// R1-40 (F-08): the e-mail channel against a scripted SMTP server. A stalled server or a stop ends the conversation
/// within the deadline — also while the server withholds its final answer — and the send has really ended when the
/// channel returns; a server that takes some recipients and refuses others never gets the message twice for the ones it
/// took, neither on the composite's retry nor on a redelivery after a restart.
/// </summary>
public sealed class NotificationSmtpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-smtp-{Guid.NewGuid():N}");

    public NotificationSmtpTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private static NotificationEvent Failed() =>
        new() { EventType = "auto_update_failed", ContainerName = "web", ContainerId = "web-1" };

    private static EmailNotificationService Email(FakeSmtp smtp, string to, TimeSpan? deadline = null) =>
        new(new StaticOptionsMonitor<EmailSettings>(new EmailSettings
        {
            Enabled = true, Host = "127.0.0.1", Port = smtp.Port, UseSsl = false, From = "whiskers@example.test", To = to,
            ThrottleMinutes = 0,
        }), OutboundTestDoubles.GuardAllowing("127.0.0.1", smtp.Port), NullLogger<EmailNotificationService>.Instance)
        { SmtpDeadline = deadline ?? TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task A_recipient_the_server_took_does_not_get_the_message_again()
    {
        await using var smtp = new FakeSmtp { Refuse = r => r == "b@example.test" };
        var email = Email(smtp, "a@example.test, b@example.test");
        var evt = Failed();

        var refused = await Assert.ThrowsAsync<EmailRecipientsRefusedException>(() => email.DeliverAsync(evt, CancellationToken.None));
        Assert.Equal(["b@example.test"], refused.Refused);
        Assert.Equal(["a@example.test"], evt.DeliveredRecipients!);
        Assert.Equal(1, smtp.Received("a@example.test"));      // the message went to the one the server took

        smtp.Refuse = _ => false;
        Assert.True(await email.DeliverAsync(evt, CancellationToken.None));

        Assert.Equal(1, smtp.Received("a@example.test"));
        Assert.Equal(1, smtp.Received("b@example.test"));
        Assert.Equal(["b@example.test"], smtp.Conversations.Last());   // the retry named only the one still missing
    }

    [Fact]
    public async Task A_server_that_refuses_every_recipient_sent_nothing_and_everyone_is_tried_again()
    {
        await using var smtp = new FakeSmtp { Refuse = _ => true };
        var email = Email(smtp, "a@example.test, b@example.test");
        var evt = Failed();

        await Assert.ThrowsAsync<EmailRecipientsRefusedException>(() => email.DeliverAsync(evt, CancellationToken.None));

        Assert.Null(evt.DeliveredRecipients);
        Assert.Equal(0, smtp.Received("a@example.test"));
    }

    [Fact]
    public async Task Partial_progress_survives_the_retry_the_outbox_and_a_restart()
    {
        await using var smtp = new FakeSmtp { Refuse = r => r == "b@example.test" };
        var paths = new DataPathOptions(_dir);
        var before = new NotificationOutbox(paths, NullLogger<NotificationOutbox>.Instance);
        var composite = NotificationDeliveryTests.Composite([Email(smtp, "a@example.test, b@example.test")], before);

        await composite.SendAsync(Failed());                    // two attempts, both refused for b
        Assert.True(await before.FlushAsync());
        Assert.Equal(1, smtp.Received("a@example.test"));      // the composite's retry went to b only

        // Restart: a new outbox reads the file; the server takes b now.
        smtp.Refuse = _ => false;
        var outbox = new NotificationOutbox(paths, NullLogger<NotificationOutbox>.Instance);
        Assert.True(await outbox.FlushAsync());
        var worker = new NotificationOutboxWorker([Email(smtp, "a@example.test, b@example.test")], outbox,
            NullLogger<NotificationOutboxWorker>.Instance, TimeSpan.FromSeconds(10));
        await worker.RedeliverDueAsync(DateTime.UtcNow.AddMinutes(2), CancellationToken.None);

        Assert.Equal(1, smtp.Received("a@example.test"));
        Assert.Equal(1, smtp.Received("b@example.test"));
        Assert.Equal(0, outbox.Status.Pending);
    }

    [Fact]
    public async Task An_invalid_recipient_is_a_configuration_error_not_a_failed_delivery()
    {
        await using var smtp = new FakeSmtp();
        var email = Email(smtp, "ops@example.test, not an address");

        Assert.False(await email.DeliverAsync(Failed(), CancellationToken.None));   // nothing to send, nothing kept
        Assert.Empty(smtp.Conversations);
    }

    [Fact]
    public async Task A_stop_while_the_server_withholds_its_final_answer_ends_the_send()
    {
        await using var smtp = new FakeSmtp { StallFinalAnswer = true };
        var email = Email(smtp, "a@example.test", deadline: TimeSpan.FromSeconds(60));
        using var stop = new CancellationTokenSource();

        var send = email.DeliverAsync(Failed(), stop.Token);
        await smtp.FinalAnswerAwaited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(5)));
        // The connection was really closed, not left open behind the cancelled call.
        await smtp.ClosedByClient.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_deadline_ends_a_send_whose_final_answer_never_comes()
    {
        await using var smtp = new FakeSmtp { StallFinalAnswer = true };
        var email = Email(smtp, "a@example.test", deadline: TimeSpan.FromMilliseconds(800));

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => email.DeliverAsync(Failed(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8)));

        Assert.True(error is TimeoutException, error.ToString());
        await smtp.ClosedByClient.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_stop_while_connecting_ends_the_send()
    {
        // 192.0.2.1 (TEST-NET-1) is never routed: the connect hangs (or fails at once where there is no route at all).
        // It is a documentation address the outbound guard denies (R1-54), so the test excepts it exactly.
        var email = new EmailNotificationService(new StaticOptionsMonitor<EmailSettings>(new EmailSettings
        {
            Enabled = true, Host = "192.0.2.1", Port = 25, UseSsl = false, From = "whiskers@example.test",
            To = "a@example.test",
        }), OutboundTestDoubles.GuardAllowing("192.0.2.1", 25), NullLogger<EmailNotificationService>.Instance)
        { SmtpDeadline = TimeSpan.FromSeconds(60) };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<Exception>(() => email.DeliverAsync(Failed(), stop.Token).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    /// <summary>A minimal SMTP server: takes or refuses each recipient by <see cref="Refuse"/>, records what reached
    /// whom, and can withhold its answer to the end of the message.</summary>
    internal sealed class FakeSmtp : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<string> _delivered = [];

        public Func<string, bool> Refuse { get; set; } = _ => false;
        public bool StallFinalAnswer { get; init; }
        public TaskCompletionSource FinalAnswerAwaited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ClosedByClient { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>The recipients each conversation named (taken or not).</summary>
        public List<List<string>> Conversations { get; } = [];
        public int Port { get; }

        public FakeSmtp()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptAsync();
        }

        public int Received(string recipient)
        {
            lock (_delivered) return _delivered.Count(r => r == recipient);
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            var named = new List<string>();
            var taken = new List<string>();
            lock (Conversations) Conversations.Add(named);
            try
            {
                await writer.WriteLineAsync("220 fake ESMTP");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var verb = line.Split(' ', ':')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO" or "HELO": await writer.WriteLineAsync("250 fake"); break;
                        case "MAIL": taken.Clear(); await writer.WriteLineAsync("250 ok"); break;
                        case "RCPT":
                            var address = line[(line.IndexOf('<') + 1)..line.IndexOf('>')];
                            lock (Conversations) named.Add(address);
                            if (Refuse(address)) await writer.WriteLineAsync("550 5.1.1 no such mailbox");
                            else { taken.Add(address); await writer.WriteLineAsync("250 ok"); }
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 go ahead");
                            while (await reader.ReadLineAsync(_stop.Token) is { } body && body != ".") { }
                            if (StallFinalAnswer)
                            {
                                FinalAnswerAwaited.TrySetResult();
                                // Never answers; the client has to close the connection.
                                while (await reader.ReadLineAsync(_stop.Token) is not null) { }
                                ClosedByClient.TrySetResult();
                                return;
                            }
                            lock (_delivered) _delivered.AddRange(taken);
                            await writer.WriteLineAsync("250 queued");
                            break;
                        case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                        default: await writer.WriteLineAsync("250 ok"); break;
                    }
                }
                ClosedByClient.TrySetResult();
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                ClosedByClient.TrySetResult();               // reset by the client
            }
            catch (Exception) { /* stopping */ }
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

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Models;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>
/// Contract 0017, amendment 4 (G2): events without a container name their subject (<see cref="NotificationEvent.ThrottleSubject"/>),
/// and the subject is part of the per-channel throttle key. Distinct subjects of one type are delivered once each, the same
/// subject stays throttled; approvals and state transitions stay exempt, <c>DeliveryId</c> stays out of the key, and an event
/// without a subject keeps the key it had. The subject is never shown, delivered or logged; it is persisted with the event in
/// the notification outbox (<c>notification-outbox.json</c>), which serializes the whole event so that a redelivery stays
/// identical — harmless, because it holds ids, GUIDs or a hash only and a redelivery skips the throttle.
/// </summary>
public sealed class NotificationThrottleSubjectTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    private static DiscordNotificationService Discord(CapturingHandler handler) => new(new HttpClient(handler),
        new StaticOptionsMonitor<Whiskers.Configuration.DiscordSettings>(new() { Enabled = true, WebhookUrl = "https://discord.example/api/webhooks/1/x", ThrottleMinutes = 5 }),
        NullLogger<DiscordNotificationService>.Instance);

    // The four emitters of the decision, with two distinct subjects each, as the emitters build them.
    public static TheoryData<string, string, string> Subjects() => new()
    {
        { NotificationEventTypes.WebhookDisabled, NotificationThrottleSubjects.Webhooks([1]), NotificationThrottleSubjects.Webhooks([2]) },
        { NotificationEventTypes.AutomationRefused, NotificationThrottleSubjects.Task(1), NotificationThrottleSubjects.Webhook(1) },
        { NotificationEventTypes.AutomationSuspended, NotificationThrottleSubjects.AiTrigger("t1"), NotificationThrottleSubjects.EndpointCheck("t1") },
        { NotificationEventTypes.ExposureViolation, NotificationThrottleSubjects.Exposure("shop", false, ["web: host network"]),
            NotificationThrottleSubjects.Exposure("blog", false, ["web: host network"]) },
        { NotificationEventTypes.MonitoringStalled, NotificationThrottleSubjects.Loop("logmonitor"), NotificationThrottleSubjects.Loop("scheduler") },
    };

    // As CompositeNotificationService hands them on: a fresh DeliveryId per send, no container.
    private static NotificationEvent Event(string type, string? subject, string? serverId = "srv") => new()
    {
        EventType = type, ServerId = serverId, ThrottleSubject = subject, ContainerName = "x", ImageInfo = "detail",
        DeliveryId = Guid.NewGuid().ToString("N"),
    };

    [Theory]
    [MemberData(nameof(Subjects))]
    public async Task Two_distinct_subjects_inside_the_window_are_two_deliveries(string type, string first, string second)
    {
        var handler = new CapturingHandler();
        var discord = Discord(handler);

        Assert.True(await discord.DeliverAsync(Event(type, first), CancellationToken.None));
        Assert.True(await discord.DeliverAsync(Event(type, second), CancellationToken.None),
            $"a second {type} about another subject was swallowed by the first one's throttle slot");
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Theory]
    [MemberData(nameof(Subjects))]
    public async Task The_same_subject_twice_inside_the_window_is_one_delivery(string type, string first, string second)
    {
        _ = second;
        var handler = new CapturingHandler();
        var discord = Discord(handler);

        Assert.True(await discord.DeliverAsync(Event(type, first), CancellationToken.None));
        Assert.False(await discord.DeliverAsync(Event(type, first), CancellationToken.None));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public void An_event_without_a_subject_keeps_the_key_it_had()
    {
        var plain = new NotificationEvent { ServerId = "srv", ContainerId = "c1", EventType = NotificationEventTypes.Stopped };
        Assert.Equal("srv/c1", NotificationThrottler.KeyFor(plain));
        Assert.Equal("srv/c1", NotificationThrottler.KeyFor(new NotificationEvent { ServerId = "srv", ContainerId = "c1", ThrottleSubject = "" }));

        // Two subject-less events of one type on one server still share the slot (flood protection as before).
        var t = new NotificationThrottler();
        t.Record(Event(NotificationEventTypes.AutomationRefused, null), 5);
        Assert.True(t.IsThrottled(Event(NotificationEventTypes.AutomationRefused, null), 5));
        // A subject-less event and one with a subject do not share it.
        Assert.False(t.IsThrottled(Event(NotificationEventTypes.AutomationRefused, NotificationThrottleSubjects.Task(1)), 5));
        // The subject does not merge servers either.
        t.Record(Event(NotificationEventTypes.AutomationRefused, NotificationThrottleSubjects.Task(1), "a"), 5);
        Assert.False(t.IsThrottled(Event(NotificationEventTypes.AutomationRefused, NotificationThrottleSubjects.Task(1), "b"), 5));
    }

    [Fact]
    public void Approvals_and_state_transitions_stay_exempt_whatever_the_subject()
    {
        var t = new NotificationThrottler();
        var approval = new NotificationEvent { EventType = NotificationEventTypes.AgentApproval, ApprovalId = "a-1", ThrottleSubject = "s" };
        t.Record(approval, 5);
        Assert.False(t.IsThrottled(approval.Copy(), 5));

        var transition = Event(NotificationEventTypes.ServerUnreachable, "s");
        t.Record(transition, 5);
        Assert.False(t.IsThrottled(Event(NotificationEventTypes.ServerUnreachable, "s"), 5));
    }

    [Fact]
    public void The_delivery_id_is_still_not_part_of_the_key()
    {
        var a = Event(NotificationEventTypes.AutomationRefused, NotificationThrottleSubjects.Task(7));
        var b = Event(NotificationEventTypes.AutomationRefused, NotificationThrottleSubjects.Task(7));
        Assert.NotEqual(a.DeliveryId, b.DeliveryId);
        Assert.Equal(NotificationThrottler.KeyFor(a), NotificationThrottler.KeyFor(b));
        Assert.DoesNotContain(a.DeliveryId!, NotificationThrottler.KeyFor(a));

        var t = new NotificationThrottler();
        t.Record(a, 5);
        Assert.True(t.IsThrottled(b, 5));
    }

    [Fact]
    public void Subjects_are_built_from_ids_and_never_carry_free_text()
    {
        // The webhook's database id, not its WebhookId (part of its URL).
        Assert.Equal("webhook:42", NotificationThrottleSubjects.Webhook(42));
        Assert.Equal(NotificationThrottleSubjects.Webhooks([3, 1, 3]), NotificationThrottleSubjects.Webhooks([1, 3]));
        Assert.Equal("loop:logmonitor", NotificationThrottleSubjects.Loop("logmonitor"));

        // The exposure subject is a hash: the project name and the violation lines (compose text) are not in it.
        var subject = NotificationThrottleSubjects.Exposure("shop-secret-project", true, ["web: port 80", "db: host network"]);
        Assert.Matches("^exposure:[0-9a-f]{32}$", subject);
        Assert.DoesNotContain("shop", subject);
        Assert.Equal(subject, NotificationThrottleSubjects.Exposure("shop-secret-project", true, ["db: host network", "web: port 80", "web: port 80"]));
        // Another rule set, or a violation found after the first report, is another subject.
        Assert.NotEqual(subject, NotificationThrottleSubjects.Exposure("shop-secret-project", false, ["web: port 80", "db: host network"]));
        Assert.NotEqual(subject, NotificationThrottleSubjects.Exposure("shop-secret-project", true,
            ["web: port 80", "db: host network", "the previous compose files could not be restored"]));
    }

    [Fact]
    public async Task No_channel_payload_or_formatter_shows_the_subject()
    {
        const string marker = "subject-marker-0f1e2d";
        var evt = Event(NotificationEventTypes.WebhookDisabled, marker);

        var handler = new CapturingHandler();
        Assert.True(await Discord(handler).DeliverAsync(evt, CancellationToken.None));
        Assert.DoesNotContain(marker, Assert.Single(handler.Bodies));
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(WebhookNotificationService.Body(evt)));
        Assert.DoesNotContain(marker, NotificationFormatter.PlainText(evt));
        Assert.DoesNotContain(marker, NotificationFormatter.Detail(evt));
        Assert.DoesNotContain(marker, NotificationFormatter.Describe(evt).Title);
        Assert.DoesNotContain(marker, NotificationFormatter.LinkFor(evt) ?? "");
    }

    [Fact]
    public void Only_the_throttler_reads_the_subject_and_only_the_subject_builder_makes_one()
    {
        var src = Path.Combine(SourceRoot(), "src", "Whiskers");
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                        && !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .ToList();
        Assert.True(files.Count > 100, $"Only {files.Count} source files found — the check would pass vacuously.");

        var (readers, assignments) = Scan(files.Select(f => (Path.GetFileName(f), File.ReadAllText(f))));

        Assert.True(readers.Count == 0, "ThrottleSubject is a key only; read outside the throttler: " + string.Join("; ", readers));
        Assert.True(assignments.Count == 0, "ThrottleSubject not built by NotificationThrottleSubjects: " + string.Join("; ", assignments));
    }

    [Fact]
    public void The_source_scan_sees_a_multi_line_initializer_a_compound_assignment_and_a_reader()
    {
        // Review r1: a value on the line after the '=' was invisible to a line-based scan.
        Assert.NotEmpty(Scan([("a.cs", "var e = new NotificationEvent\n{\n    ThrottleSubject =\n        evt.ImageInfo,\n};")]).Assignments);
        Assert.NotEmpty(Scan([("a.cs", "var e = new NotificationEvent { ThrottleSubject = $\"x:{name}\" };")]).Assignments);
        Assert.NotEmpty(Scan([("a.cs", "evt.ThrottleSubject ??= evt.ContainerName;")]).Assignments);
        Assert.NotEmpty(Scan([("a.cs", "evt.ThrottleSubject += \"x\";")]).Assignments);
        Assert.NotEmpty(Scan([("Formatter.cs", "var text = evt.ThrottleSubject;")]).Readers);
        Assert.NotEmpty(Scan([("Formatter.cs", "if (evt.ThrottleSubject == \"x\") { }")]).Readers);
        // Built by the builder, also across lines, and the throttler's own read pass.
        var clean = Scan([("a.cs", "var e = new NotificationEvent\r\n{\r\n    ThrottleSubject =\r\n        NotificationThrottleSubjects.Loop(loop.Loop),\r\n};"),
            ("NotificationThrottler.cs", "var k = evt.ThrottleSubject;"), ("b.cs", "// evt.ThrottleSubject = x;")]);
        Assert.Empty(clean.Readers);
        Assert.Empty(clean.Assignments);
    }

    /// <summary>Reads the sources whole, so an initializer whose value stands on the next line is seen. An assignment
    /// (<c>=</c>, <c>??=</c>, <c>+=</c>) must take its value from <see cref="NotificationThrottleSubjects"/> (or the two
    /// parameters that carry one through); any other mention outside the throttler is a reader. Not covered here, and
    /// harmless: the outbox serializes the whole event, subject included, to <c>notification-outbox.json</c> (contract
    /// 0017, amendment 4) — ids, GUIDs or a hash, and a redelivery skips the throttle.</summary>
    private static (List<string> Readers, List<string> Assignments) Scan(IEnumerable<(string Name, string Text)> files)
    {
        var readers = new List<string>();
        var assignments = new List<string>();
        foreach (var (name, raw) in files)
        {
            // Comment lines (XML docs included) are not code.
            var text = string.Join('\n', raw.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (Match m in Regex.Matches(text, @"\bThrottleSubject\b(?<op>\s*(?:\?\?|\+)?=(?!=))?(?<value>\s*[^,;}]*)"))
            {
                var value = Regex.Replace(m.Groups["value"].Value, @"\s+", " ").Trim();
                if (m.Groups["op"].Success)
                {
                    var op = m.Groups["op"].Value.Trim();
                    // ComposeRunner and WebhookService pass a parameter through that is itself built by the builder.
                    if (op != "=" || !(value.StartsWith("NotificationThrottleSubjects.", StringComparison.Ordinal)
                                       || value is "throttleSubject" or "throttleSubject ?? NotificationThrottleSubjects.Webhook(webhook.Id)"))
                        assignments.Add($"{name}: ThrottleSubject {op} {value}");
                }
                else if (name != "NotificationThrottler.cs" && m.Index > 0 && text[m.Index - 1] == '.')
                    readers.Add($"{name}: .ThrottleSubject{(value.Length > 40 ? value[..40] : value)}");
            }
        }
        return (readers, assignments);
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}

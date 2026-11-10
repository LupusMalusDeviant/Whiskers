using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.Agent.Triggers;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Mutations;
using Whiskers.Services.Notifications;
using Whiskers.Services.Persistence;

namespace Whiskers.Tests;

/// <summary>
/// R1-56: quiet hours suppress delivery to the channels, never evaluation — the in-app feed and <c>AlertHistory</c> keep
/// recording and AI triggers are dispatched. Windows in an explicit IANA zone (crossing midnight, across DST changes, an
/// invalid zone refused on save); a named never-suppressed set (each member delivered during quiet hours; a CVE batch with a
/// Critical finding); the per-container mute unchanged (INV-04); what was held recorded durably, bounded with an overflow
/// count, and summarized exactly once after the window through the outbox — across a restart and at the crash boundaries —
/// bypassing the per-channel throttle. Rule changes are gated, audited configuration writes. Every test runs on a fake clock.
/// </summary>
public sealed class QuietHoursTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sw-r156-qh-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly ServiceProvider _sp;
    private readonly SustainedAlertTests.ManualTime _clock;
    private readonly AgentToolTestHelpers.NullMutationAudit _audit = new();
    private readonly MutableUser _user = new(AppRole.Admin);

    // 23:30 in Berlin (CET, UTC+1) on 3 November 2026: inside "Night" (22:00–07:00), whose window started that day.
    private static readonly DateTime InWindow = new(2026, 11, 3, 22, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterWindow = new(2026, 11, 4, 6, 1, 0, DateTimeKind.Utc);

    public QuietHoursTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "metrics.db");
        _clock = new SustainedAlertTests.ManualTime(new DateTimeOffset(InWindow));
        _sp = Services();
    }

    public void Dispose()
    {
        _sp.Dispose();
        TestSqlite.ClearPool(_db);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string SettingsFile => Path.Combine(_dir, "app-settings.json");

    /// <summary>The services as the app has them, built anew for a "restart" (same database and settings file).</summary>
    private ServiceProvider Services(IAlertSuppressionStore? store = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<MetricsDbContext>(o => o.UseSqlite($"Data Source={_db}"));
        services.AddSingleton<IAiTriggerDispatcher>(_triggers);
        services.AddSingleton<IQuietHoursService>(_ => QuietHours());
        if (store is null) services.AddSingleton<IAlertSuppressionStore>(sp => new AlertSuppressionStore(sp));
        else services.AddSingleton(store);
        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.EnsureCreated();
        return sp;
    }

    private QuietHoursService QuietHours()
    {
        var builder = new ConfigurationBuilder();
        if (File.Exists(SettingsFile)) builder.AddJsonFile(SettingsFile, optional: false, reloadOnChange: false);
        return new QuietHoursService(builder.Build(), new AppSettingsStore(SettingsFile), NullLogger<QuietHoursService>.Instance, _clock);
    }

    private static QuietHoursRule Night(params string[] types) => new()
    {
        Id = "night", Name = "Night", TimeZone = "Europe/Berlin", Start = "22:00", End = "07:00", EventTypes = [.. types],
    };

    private async Task SaveRulesAsync(params QuietHoursRule[] rules) =>
        await _sp.GetRequiredService<IQuietHoursService>().SaveAsync(rules);

    private readonly RecordingChannel _channel = new();
    private readonly RecordingInApp _inApp = new();
    private readonly StubPrefs _prefs = new();
    private readonly CountingDispatcher _triggers = new();

    private CompositeNotificationService Composite(ServiceProvider? sp = null, params INotificationChannel[] channels) =>
        new(channels.Length == 0 ? [_channel] : channels, _inApp, _prefs, sp ?? _sp, NullLogger<CompositeNotificationService>.Instance,
            outbox: null, TimeSpan.FromSeconds(5), TimeSpan.Zero);

    private List<AlertHistoryEntity> History()
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>().AlertHistory.AsNoTracking().ToList();
    }

    private List<QuietHoursSuppressionEntity> Held()
    {
        using var scope = _sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MetricsDbContext>().QuietHoursSuppressions.AsNoTracking().ToList();
    }

    private static NotificationEvent Alert(string type = "high_cpu", string container = "web", DateTime? at = null) => new()
    {
        EventType = type, ContainerId = "c-" + container, ContainerName = container, ServerId = "local", ServerName = "Badwolf",
        ImageInfo = $"{container}: CPU at 97%", Timestamp = at ?? InWindow,
    };

    // --- the window -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-03T20:59:00Z", null)]            // 21:59 local
    [InlineData("2026-11-03T21:00:00Z", "2026-11-03")]    // 22:00 local: starts
    [InlineData("2026-11-04T02:00:00Z", "2026-11-03")]    // 03:00 local, the next day: the window that started the evening before
    [InlineData("2026-11-04T05:59:00Z", "2026-11-03")]    // 06:59 local
    [InlineData("2026-11-04T06:00:00Z", null)]            // 07:00 local: over
    public void A_window_crossing_midnight_belongs_to_the_evening_it_started(string utc, string? window)
    {
        Assert.Equal(window, QuietHoursWindow.Active(Night(), DateTime.Parse(utc).ToUniversalTime()));
    }

    [Fact]
    public void On_the_night_the_clocks_go_forward_the_skipped_hour_never_occurs()
    {
        // Europe/Berlin, 29 March 2026: 02:00 CET becomes 03:00 CEST (01:00 UTC).
        var skipped = new QuietHoursRule { TimeZone = "Europe/Berlin", Start = "02:00", End = "03:00" };
        Assert.Null(QuietHoursWindow.Active(skipped, new DateTime(2026, 3, 29, 0, 59, 0, DateTimeKind.Utc)));   // 01:59 CET
        Assert.Null(QuietHoursWindow.Active(skipped, new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc)));    // 03:00 CEST

        // 01:00–04:00 local lasts two real hours that night: 00:00–02:00 UTC.
        var spanning = new QuietHoursRule { TimeZone = "Europe/Berlin", Start = "01:00", End = "04:00" };
        Assert.Equal("2026-03-29", QuietHoursWindow.Active(spanning, new DateTime(2026, 3, 29, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-03-29", QuietHoursWindow.Active(spanning, new DateTime(2026, 3, 29, 1, 59, 0, DateTimeKind.Utc)));
        Assert.Null(QuietHoursWindow.Active(spanning, new DateTime(2026, 3, 29, 2, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void On_the_night_the_clocks_go_back_the_repeated_hour_is_inside_the_window_both_times()
    {
        // Europe/Berlin, 25 October 2026: 03:00 CEST becomes 02:00 CET (01:00 UTC).
        var repeated = new QuietHoursRule { TimeZone = "Europe/Berlin", Start = "02:00", End = "03:00" };
        Assert.Equal("2026-10-25", QuietHoursWindow.Active(repeated, new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc)));  // 02:30 CEST
        Assert.Equal("2026-10-25", QuietHoursWindow.Active(repeated, new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc)));  // 02:30 CET
        Assert.Null(QuietHoursWindow.Active(repeated, new DateTime(2026, 10, 25, 2, 0, 0, DateTimeKind.Utc)));                 // 03:00 CET
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons", "22:00", "07:00")]
    [InlineData("W. Europe Standard Time", "22:00", "07:00")]   // a Windows name, not IANA
    [InlineData("", "22:00", "07:00")]
    [InlineData("Europe/Berlin", "25:00", "07:00")]
    [InlineData("Europe/Berlin", "22:00", "22:00")]
    public async Task An_invalid_rule_is_refused_on_save_and_nothing_is_written(string zone, string start, string end)
    {
        var service = QuietHours();
        var rule = Night();
        rule.TimeZone = zone; rule.Start = start; rule.End = end;

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync([rule]));

        Assert.Empty(service.Rules);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_rule_naming_an_event_type_quiet_hours_cannot_hold_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => QuietHours().SaveAsync([Night("agent_approval")]));
        await Assert.ThrowsAsync<ArgumentException>(() => QuietHours().SaveAsync([Night("made_up")]));
    }

    // --- the pipeline -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task During_quiet_hours_the_channels_are_held_but_feed_history_and_triggers_are_not()
    {
        await SaveRulesAsync(Night());

        await Composite().SendAsync(Alert());

        Assert.Empty(_channel.Sent);
        Assert.Single(_inApp.Added);                          // the in-app feed keeps recording
        Assert.Single(History());                             // and AlertHistory
        Assert.Equal(1, _triggers.Calls);                     // AI-trigger dispatch is not held
        var held = Assert.Single(Held());
        Assert.Equal(("night", "2026-11-03", "web @ Badwolf", 1), (held.RuleId, held.WindowKey, held.Target, held.Count));

        // Outside the window it is delivered as before.
        _clock.Now = new DateTimeOffset(AfterWindow);
        await Composite().SendAsync(Alert());
        Assert.Single(_channel.Sent);
    }

    public static TheoryData<string> NeverSuppressed()
    {
        var data = new TheoryData<string>();
        foreach (var type in NotificationEventTypes.UnsuppressibleEvents.Order(StringComparer.Ordinal)) data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(NeverSuppressed))]
    public async Task Each_never_suppressed_event_is_delivered_during_quiet_hours(string type)
    {
        await SaveRulesAsync(Night());

        await Composite().SendAsync(Alert(type));

        Assert.Equal(type, Assert.Single(_channel.Sent).EventType);
        Assert.Empty(Held());
    }

    [Fact]
    public async Task One_Critical_finding_makes_the_whole_CVE_batch_unsuppressible()
    {
        await SaveRulesAsync(Night());
        var critical = Alert(NotificationEventTypes.CveFinding);
        critical.HighestCveSeverity = CveSeverity.Critical;
        var high = Alert(NotificationEventTypes.CveFinding, "api");
        high.HighestCveSeverity = CveSeverity.High;

        await Composite().SendAsync(critical);
        await Composite().SendAsync(high);

        Assert.Equal("web", Assert.Single(_channel.Sent).ContainerName);
        Assert.Equal("api @ Badwolf", Assert.Single(Held()).Target);
    }

    [Fact]
    public async Task The_per_container_mute_keeps_its_meaning_during_quiet_hours()
    {
        await SaveRulesAsync(Night());
        _prefs.Muted.Add("web");
        var critical = Alert(NotificationEventTypes.CveFinding);
        critical.HighestCveSeverity = CveSeverity.Critical;

        // A Critical CVE batch for a muted container stays muted: the never-suppressed set bypasses quiet hours only.
        await Composite().SendAsync(critical);
        await Composite().SendAsync(Alert(NotificationEventTypes.MonitoringStalled));
        Assert.Empty(_channel.Sent);

        // Only the administrative events override the mute (INV-04), during quiet hours too.
        await Composite().SendAsync(Alert(NotificationEventTypes.AutomationSuspended));
        Assert.Equal(NotificationEventTypes.AutomationSuspended, Assert.Single(_channel.Sent).EventType);
        Assert.Empty(Held());
    }

    [Fact]
    public async Task An_event_type_the_registry_does_not_know_is_delivered_never_held()
    {
        await SaveRulesAsync(Night());

        await Composite().SendAsync(Alert("made_up_event"));

        Assert.Single(_channel.Sent);
        Assert.Empty(Held());
    }

    [Fact]
    public async Task A_rule_for_chosen_types_holds_those_and_their_recovery_only()
    {
        await SaveRulesAsync(Night(NotificationEventTypes.HighCpu, NotificationEventTypes.LogAlertPrefix));

        await Composite().SendAsync(Alert("high_cpu"));
        await Composite().SendAsync(Alert("high_cpu_recovered"));
        await Composite().SendAsync(Alert("log_alert:error"));
        await Composite().SendAsync(Alert("high_memory"));

        Assert.Equal("high_memory", Assert.Single(_channel.Sent).EventType);
        Assert.Equal(3, Assert.Single(Held()).Count);
    }

    [Fact]
    public async Task An_event_that_cannot_be_recorded_is_delivered_rather_than_lost()
    {
        await SaveRulesAsync(Night());
        using (var scope = _sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MetricsDbContext>().Database.ExecuteSqlRawAsync("DROP TABLE QuietHoursSuppressions;");

        await Composite().SendAsync(Alert());

        Assert.Single(_channel.Sent);
    }

    // --- the summary ------------------------------------------------------------------------------------------------

    private NotificationOutbox Outbox() => new(new DataPathOptions(_dir), NullLogger<NotificationOutbox>.Instance);

    private QuietHoursSummarizer Summarizer(INotificationOutbox outbox, ServiceProvider? sp = null, params INotificationChannel[] channels) =>
        new((sp ?? _sp).GetRequiredService<IAlertSuppressionStore>(), (sp ?? _sp).GetRequiredService<IQuietHoursService>(), outbox,
            channels.Length == 0 ? [_channel] : channels, NullLogger<QuietHoursSummarizer>.Instance);

    private static NotificationOutboxWorker Worker(INotificationOutbox outbox, params INotificationChannel[] channels) =>
        new(channels, outbox, NullLogger<NotificationOutboxWorker>.Instance, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Each_window_is_summarized_once_after_it_ended_and_summaries_bypass_the_channel_throttle()
    {
        var handler = new NotificationDeliveryTests.ScriptedHandler();
        var slack = new SlackNotificationService(new HttpClient(handler), new StaticOptionsMonitor<SlackSettings>(new SlackSettings
        {
            Enabled = true, WebhookUrl = "https://hooks.slack.com/services/T0/B0/x", ThrottleMinutes = 600,
        }), NullLogger<SlackNotificationService>.Instance);
        // Two rules, two windows ending together: two summaries of one type for one key inside one throttle window.
        var early = Night(NotificationEventTypes.Unhealthy);
        early.Id = "early"; early.Name = "Early"; early.Start = "23:00";
        await SaveRulesAsync(Night(NotificationEventTypes.HighCpu, NotificationEventTypes.HighMemory), early);
        await Composite(null, slack).SendAsync(Alert("high_cpu", "web"));
        await Composite(null, slack).SendAsync(Alert("high_memory", "web", InWindow.AddMinutes(10)));
        await Composite(null, slack).SendAsync(Alert("high_cpu", "api", InWindow.AddMinutes(15)));
        await Composite(null, slack).SendAsync(Alert("unhealthy", "api", InWindow.AddMinutes(20)));
        Assert.Equal(0, handler.Requests);                                       // held

        var outbox = Outbox();
        var summarizer = Summarizer(outbox, null, slack);
        await summarizer.SummarizeDueAsync(InWindow.AddHours(1));               // still in the windows: nothing yet
        Assert.Equal(0, outbox.Status.Pending);

        await summarizer.SummarizeDueAsync(AfterWindow);
        await Worker(outbox, slack).RedeliverDueAsync(AfterWindow, CancellationToken.None);
        Assert.Equal(2, handler.Requests);                                       // the second is not throttled
        Assert.Contains(handler.Bodies, b => b.Contains("held back 3 notifications for 2 targets") && b.Contains("web @ Badwolf (2)"));
        Assert.Contains(handler.Bodies, b => b.Contains("held back 1 notification for 1 target"));
        Assert.All(handler.Bodies, b => Assert.Contains("Held back during quiet hours", b));

        // Exactly once: later passes make nothing new.
        await summarizer.SummarizeDueAsync(AfterWindow.AddMinutes(1));
        await Worker(outbox, slack).RedeliverDueAsync(AfterWindow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(2, handler.Requests);
        Assert.All(Held(), h => Assert.NotNull(h.SummarizedAt));
    }

    [Fact]
    public async Task Overflow_beyond_the_bound_is_counted_in_the_summary_never_silent()
    {
        await SaveRulesAsync(Night());
        for (var i = 0; i < AlertSuppressionStore.MaxItems + 5; i++)
            await Composite().SendAsync(Alert(at: InWindow.AddMinutes(i)));

        var held = Assert.Single(Held());
        Assert.Equal(AlertSuppressionStore.MaxItems + 5, held.Count);
        Assert.Equal(AlertSuppressionStore.MaxItems, AlertSuppressionStore.Items(held).Count);   // bounded

        var summary = QuietHoursSummarizer.Summary([held], AfterWindow);
        Assert.Contains($"held back {AlertSuppressionStore.MaxItems + 5} notifications", summary.ImageInfo);
        Assert.Contains("5 further events suppressed", summary.ImageInfo);
        Assert.True(summary.ImageInfo!.Length <= QuietHoursSummarizer.MaxText + 100);
    }

    [Fact]
    public async Task A_restart_during_quiet_hours_neither_loses_nor_doubles_the_summary()
    {
        await SaveRulesAsync(Night());
        await Composite().SendAsync(Alert());

        // Whiskers restarts in the middle of the night: new services over the same database and settings.
        await using var restarted = Services();
        await Composite(restarted).SendAsync(Alert(at: InWindow.AddHours(2)));

        var outbox = Outbox();
        await Summarizer(outbox, restarted, _channel.WithTarget()).SummarizeDueAsync(AfterWindow);
        await Worker(outbox, _channel).RedeliverDueAsync(AfterWindow, CancellationToken.None);

        var summary = Assert.Single(_channel.Sent);
        Assert.Contains("held back 2 notifications for 1 target", summary.ImageInfo);
    }

    [Fact]
    public async Task A_stop_after_the_outbox_write_but_before_the_mark_does_not_create_the_summary_twice()
    {
        await SaveRulesAsync(Night());
        await Composite().SendAsync(Alert());

        // The first pass writes the summary to the outbox file and is stopped before the rows are marked.
        var crashing = new CrashBeforeMark(_sp.GetRequiredService<IAlertSuppressionStore>());
        var outbox = Outbox();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QuietHoursSummarizer(crashing, _sp.GetRequiredService<IQuietHoursService>(), outbox, [_channel.WithTarget()],
                NullLogger<QuietHoursSummarizer>.Instance).SummarizeDueAsync(AfterWindow));
        Assert.All(Held(), h => Assert.Null(h.SummarizedAt));

        // The restart: a new outbox reads the file, the next pass finds the rows pending again — and adds nothing.
        var restarted = Outbox();
        Assert.True(await restarted.FlushAsync());
        await Summarizer(restarted, null, _channel.WithTarget()).SummarizeDueAsync(AfterWindow.AddMinutes(1));
        Assert.Equal(1, restarted.Status.Pending);
        Assert.All(Held(), h => Assert.NotNull(h.SummarizedAt));

        await Worker(restarted, _channel).RedeliverDueAsync(AfterWindow.AddMinutes(1), CancellationToken.None);
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task A_stop_after_a_channel_accepted_the_summary_but_before_it_was_recorded_delivers_it_again()
    {
        // Documented as at-least-once: Slack and most channels have no receiver-side deduplication.
        await SaveRulesAsync(Night());
        await Composite().SendAsync(Alert());
        var outbox = Outbox();
        await Summarizer(outbox, null, _channel.WithTarget()).SummarizeDueAsync(AfterWindow);
        var file = Path.Combine(_dir, Path.GetFileName(new DataPathOptions(_dir).NotificationOutboxJson));
        var beforeDelivery = await File.ReadAllBytesAsync(file);

        await Worker(outbox, _channel).RedeliverDueAsync(AfterWindow, CancellationToken.None);   // accepted …
        await File.WriteAllBytesAsync(file, beforeDelivery);                                      // … but not recorded

        var restarted = Outbox();
        Assert.True(await restarted.FlushAsync());
        await Worker(restarted, _channel).RedeliverDueAsync(AfterWindow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Equal(_channel.Sent[0].DeliveryId, _channel.Sent[1].DeliveryId);                  // one identity, for those who can deduplicate

        // It is not created a third time: the rows are marked and the key is remembered.
        await Summarizer(restarted, null, _channel.WithTarget()).SummarizeDueAsync(AfterWindow.AddMinutes(2));
        await Worker(restarted, _channel).RedeliverDueAsync(AfterWindow.AddMinutes(2), CancellationToken.None);
        Assert.Equal(2, _channel.Sent.Count);
    }

    [Fact]
    public async Task A_window_that_ends_early_because_the_rule_changed_is_summarized_then()
    {
        await SaveRulesAsync(Night());
        await Composite().SendAsync(Alert());
        var outbox = Outbox();
        var summarizer = Summarizer(outbox, null, _channel.WithTarget());
        await summarizer.SummarizeDueAsync(InWindow.AddMinutes(5));
        Assert.Equal(0, outbox.Status.Pending);

        // The rule is switched off at 23:40 local: its window is over now, not at 07:00.
        var off = Night();
        off.Enabled = false;
        await SaveRulesAsync(off);
        await summarizer.SummarizeDueAsync(InWindow.AddMinutes(10));

        Assert.Equal(1, outbox.Status.Pending);
        // And what is sent from now on is delivered at once.
        await Composite().SendAsync(Alert());
        Assert.Single(_channel.Sent);
    }

    [Fact]
    public async Task A_window_extended_after_its_summary_summarizes_what_it_held_since_never_drops_it()
    {
        // Review MAJOR-1: the window is summarized at 07:01 local, then an Admin moves the end to 09:00 — the same window
        // is in effect again. What it holds now is a new set with a summary of its own, not one the outbox refuses.
        await SaveRulesAsync(Night());
        await Composite().SendAsync(Alert("high_cpu", "web"));
        var outbox = Outbox();
        var summarizer = Summarizer(outbox, null, _channel.WithTarget());
        await summarizer.SummarizeDueAsync(AfterWindow);
        await Worker(outbox, _channel).RedeliverDueAsync(AfterWindow, CancellationToken.None);
        Assert.Contains("web @ Badwolf", Assert.Single(_channel.Sent).ImageInfo);

        var longer = Night();
        longer.End = "09:00";
        await SaveRulesAsync(longer);
        _clock.Now = new DateTimeOffset(AfterWindow.AddMinutes(29));                 // 07:30 local: in the window again
        await Composite().SendAsync(Alert("high_cpu", "api", AfterWindow.AddMinutes(29)));
        Assert.Single(_channel.Sent);                                                 // held

        var later = AfterWindow.AddHours(2);                                          // 09:01 local
        await summarizer.SummarizeDueAsync(later);
        await Worker(outbox, _channel).RedeliverDueAsync(later, CancellationToken.None);
        Assert.Equal(2, _channel.Sent.Count);
        Assert.Contains("api @ Badwolf", _channel.Sent[1].ImageInfo);
        Assert.NotEqual(_channel.Sent[0].DeliveryId, _channel.Sent[1].DeliveryId);
        Assert.All(Held(), h => Assert.NotNull(h.SummarizedAt));
    }

    [Fact]
    public async Task The_outbox_remembers_a_summary_key_for_seven_days_on_its_injected_clock()
    {
        // Review NIT-11: the key bookkeeping read the wall clock while the rest of the outbox took the caller's time.
        _clock.Now = new DateTimeOffset(AfterWindow);
        var outbox = new NotificationOutbox(new DataPathOptions(_dir), NullLogger<NotificationOutbox>.Instance, _clock);
        var summary = new NotificationEvent { EventType = NotificationEventTypes.QuietHoursSummary, ContainerName = "" };
        Assert.True(await outbox.AddOnceAsync(_channel.Name, summary, "k|Recording", AfterWindow));
        await Worker(outbox, _channel).RedeliverDueAsync(AfterWindow, CancellationToken.None);
        Assert.True(await outbox.FlushAsync());
        Assert.False(await outbox.AddOnceAsync(_channel.Name, summary, "k|Recording", AfterWindow.AddDays(1)));

        _clock.Now = new DateTimeOffset(AfterWindow + NotificationOutbox.KeyMemory + TimeSpan.FromHours(1));
        await outbox.AddOnceAsync(_channel.Name, summary, "other|Recording", _clock.Now.UtcDateTime);
        Assert.True(await outbox.FlushAsync());                                    // forgets what is older than the memory
        Assert.True(await outbox.AddOnceAsync(_channel.Name, summary, "k|Recording", _clock.Now.UtcDateTime));
    }

    // --- the gated write (R1-11, R1-22) -----------------------------------------------------------------------------

    private UiMutationRunner Runner(GuardrailPolicy? policy = null)
    {
        var store = new AgentToolTestHelpers.FixedGuardrailStore(policy ?? GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), _audit, [],
            NullLogger<MutationGateway>.Instance, targets: TestTargets.Resolver(), approvals: new ApprovalGate(new ApprovalStore(), store));
        return new UiMutationRunner(gateway, new CapabilityCatalog(), _user);
    }

    // As the Settings panel runs it.
    private static Task Save(UiMutationRunner runner, IQuietHoursService service, params QuietHoursRule[] rules) =>
        runner.RunAsync("config.save", new { section = "quiet-hours" }, (ResolvedTarget _, CancellationToken ct) => service.SaveAsync(rules, ct));

    [Fact]
    public async Task An_admin_save_is_audited_first_then_written_then_published_and_survives_a_restart()
    {
        var service = QuietHours();
        var intentFirst = false;

        await Runner().RunAsync("config.save", new { section = "quiet-hours" }, async (ResolvedTarget _, CancellationToken ct) =>
        {
            intentFirst = _audit.Events.Contains("begin:config.save:");
            await service.SaveAsync([Night()], ct);
        });

        Assert.True(intentFirst);
        Assert.Contains("complete:Executed", _audit.Events);
        Assert.Equal("night", Assert.Single(service.Rules).Id);
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile))!["QuietHours"]!["Rules"]!.AsArray();
        Assert.Equal("Europe/Berlin", stored.Single()!["TimeZone"]!.GetValue<string>());
        Assert.Equal("Night", Assert.Single(QuietHours().Rules).Name);          // read back at the next start
    }

    [Theory]
    [InlineData(AppRole.Viewer)]
    [InlineData(AppRole.Operator)]
    public async Task A_viewer_or_operator_is_refused_and_nothing_changes(AppRole role)
    {
        var service = QuietHours();
        _user.Role = role;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Runner(), service, Night()));

        Assert.Empty(service.Rules);
        Assert.False(File.Exists(SettingsFile));
        Assert.Contains("denied:Denied", _audit.Events);
    }

    [Fact]
    public async Task An_admin_demoted_while_the_page_is_open_is_refused_on_the_next_save()
    {
        var service = QuietHours();
        var runner = Runner();
        await Save(runner, service, Night());

        _user.Role = AppRole.Operator;
        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(runner, service));

        Assert.Single(service.Rules);
    }

    [Fact]
    public async Task The_kill_switch_refuses_a_quiet_hours_change()
    {
        var service = QuietHours();
        var policy = GuardrailPolicy.SafeDefault();
        policy.ReadOnlyMode = true;

        await Assert.ThrowsAsync<MutationRefusedException>(() => Save(Runner(policy), service, Night()));

        Assert.Empty(service.Rules);
    }

    [Fact]
    public async Task An_audit_failure_refuses_before_the_rules_are_published()
    {
        var service = QuietHours();
        _audit.FailNextBegin = true;

        await Assert.ThrowsAnyAsync<Exception>(() => Save(Runner(), service, Night()));

        Assert.Empty(service.Rules);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public async Task A_failed_save_leaves_the_live_rules_unchanged()
    {
        var blocker = Path.Combine(_dir, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");
        var service = new QuietHoursService(new ConfigurationBuilder().Build(),
            new AppSettingsStore(Path.Combine(blocker, "app-settings.json")), NullLogger<QuietHoursService>.Instance, _clock);

        await Assert.ThrowsAnyAsync<Exception>(() => Save(Runner(), service, Night()));

        Assert.Empty(service.Rules);
        Assert.Null(service.Match(Alert()));
    }

    [Fact]
    public void A_hand_edited_rule_that_cannot_be_read_holds_nothing()
    {
        File.WriteAllText(SettingsFile,
            """{ "QuietHours": { "Rules": [ { "Id": "x", "Name": "Bad", "TimeZone": "Nowhere/Land", "Start": "00:00", "End": "23:59" } ] } }""");

        var service = QuietHours();

        Assert.Empty(service.Rules);
        Assert.Null(service.Match(Alert()));
    }

    // --- doubles ----------------------------------------------------------------------------------------------------

    /// <summary>Runs the summary, then stops before the rows are marked — a crash at that boundary.</summary>
    private sealed class CrashBeforeMark(IAlertSuppressionStore inner) : IAlertSuppressionStore
    {
        public Task<bool> HoldAsync(QuietHoursMatch match, NotificationEvent evt, CancellationToken ct = default) => inner.HoldAsync(match, evt, ct);
        public Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default) => inner.PruneAsync(cutoff, ct);

        public Task SummarizeAsync(Func<IReadOnlyList<QuietHoursSuppressionEntity>, CancellationToken, Task<IReadOnlyCollection<string>>> summarize,
            DateTime at, CancellationToken ct = default) =>
            inner.SummarizeAsync(async (rows, token) =>
            {
                await summarize(rows, token);
                throw new InvalidOperationException("stopped before the rows were marked");
            }, at, ct);
    }

    private sealed class RecordingChannel : INotificationChannel
    {
        private bool _hasTarget;
        public List<NotificationEvent> Sent { get; } = [];
        public string Name => "Recording";
        public IReadOnlyList<OutboundEndpoint> Targets => _hasTarget ? [new OutboundEndpoint("https", "example.com", 443)] : [];

        /// <summary>Enabled and configured, as a channel the summary goes to.</summary>
        public RecordingChannel WithTarget() { _hasTarget = true; return this; }

        public Task<bool> DeliverAsync(NotificationEvent evt, CancellationToken ct)
        {
            lock (Sent) Sent.Add(evt);
            return Task.FromResult(true);
        }

        public Task SendTestAsync() => Task.CompletedTask;
    }

    private sealed class RecordingInApp : IInAppNotificationStore
    {
        public List<NotificationEvent> Added { get; } = [];
        public IReadOnlyList<InAppNotification> Recent => [];
        public int UnreadCount => 0;
        public void Add(NotificationEvent evt) => Added.Add(evt);
        public void MarkAllRead() { }
        public void Clear() { }
        public event Action? Changed { add { } remove { } }
        event Action<InAppNotification>? IInAppNotificationStore.Added { add { } remove { } }
        public Task<List<InAppNotification>> QueryAsync(string? severity, string? eventType, int skip, int take, CancellationToken ct = default)
            => Task.FromResult(new List<InAppNotification>());
        public Task<int> CountAsync(string? severity, string? eventType, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class StubPrefs : IContainerNotificationPrefsService
    {
        public HashSet<string> Muted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ContainerNotifEntry GetPrefs(string containerName) => new();
        public bool ShouldNotify(string containerName, string eventType) => !Muted.Contains(containerName);
        public Task SavePrefsAsync(string containerName, ContainerNotifEntry entry) => Task.CompletedTask;
    }

    private sealed class CountingDispatcher : IAiTriggerDispatcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task OnEventAsync(NotificationEvent evt) { Interlocked.Increment(ref _calls); return Task.CompletedTask; }
    }

    private sealed class MutableUser(AppRole role) : ICurrentUserService
    {
        public AppRole Role { get; set; } = role;
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("admin@example.com");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(Role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(Role >= required);
    }
}

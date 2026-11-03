using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whiskers.Models;
using Whiskers.Models.Cve;
using Whiskers.Services.Metrics.HostLoad;
using Whiskers.Services.Notifications;

namespace Whiskers.Tests;

/// <summary>
/// R1-56: event types come from one registry, <see cref="NotificationEventTypes"/>. The architecture test reads the syntax
/// tree of every source file and refuses an <c>EventType</c> assignment whose value is not drawn from it — a literal, an
/// interpolated string, or a variable (a method parameter is followed to every call site: <c>ServerCircuitBreaker</c>,
/// <c>WebhookService</c>, <c>LoopSuspensionService</c>). The classification test requires every registry entry to be
/// either suppressible or never suppressed, so a new type cannot land unclassified.
/// </summary>
public sealed class NotificationEventTypeArchitectureTests
{
    /// <summary>Copies of a type that already passed the registry — never a new type. Each with its reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> Copies = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // HostLoadFinding.EventType: its Kind or <Kind>_recovered; BreachTracker refuses a kind not in RecoverableKinds.
        ["finding.EventType"] = "HostLoadFinding",
        // The in-app feed and Matrix's payload copy the event they were given.
        ["n.EventType"] = "InAppNotificationStore",
        ["Enc(e.EventType)"] = "MatrixNotificationService",
    };

    [Fact]
    public void Every_event_type_a_producer_writes_is_drawn_from_the_registry()
    {
        var offenders = Offenders(SourceFiles().Select(f => (f, File.ReadAllText(f))).ToList());

        Assert.True(offenders.Count == 0,
            "EventType values not drawn from NotificationEventTypes (use a constant, LogAlert(…) or Recovered(…)): " +
            string.Join("; ", offenders));
        // A Razor page that wrote one would not be in the C# sources.
        var src = Path.Combine(SourceRoot(), "src", "Whiskers");
        Assert.Empty(Directory.EnumerateFiles(src, "*.razor", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"EventType\s*=\s*\$?""")));
    }

    [Fact]
    public void The_architecture_test_refuses_a_literal_an_interpolation_and_a_variable_fed_a_literal()
    {
        // The mutation cases: each would pass a type past the registry.
        Assert.NotEmpty(Offenders([("a.cs", "class A { void M() { var e = new NotificationEvent { EventType = \"made_up\" }; } }")]));
        Assert.NotEmpty(Offenders([("a.cs", "class A { void M(string s) { var e = new NotificationEvent { EventType = $\"x:{s}\" }; } }")]));
        Assert.NotEmpty(Offenders([("a.cs",
            "class A { void Announce(string type) { var e = new NotificationEvent { EventType = type }; } " +
            "void N() { Announce(NotificationEventTypes.LoopsPaused); Announce(\"loops_gone\"); } }")]));
        Assert.NotEmpty(Offenders([("a.cs", "class A { void M() { var t = \"x\"; evt.EventType = t; } }")]));
        // And what it accepts.
        Assert.Empty(Offenders([("a.cs",
            "class A { void Announce(string type) { var e = new NotificationEvent { EventType = type }; } " +
            "void N(bool b) { Announce(NotificationEventTypes.LoopsPaused); var e = new NotificationEvent { EventType = b " +
            "? NotificationEventTypes.Stopped : Whiskers.Services.Notifications.NotificationEventTypes.LogAlert(\"error\") }; } }")]));
    }

    [Fact]
    public void Every_registry_entry_is_classified_as_suppressible_or_never_suppressed()
    {
        var unclassified = NotificationEventTypes.All
            .Where(t => !NotificationEventTypes.SuppressibleEvents.Contains(t) && !NotificationEventTypes.UnsuppressibleEvents.Contains(t))
            .ToList();
        Assert.True(unclassified.Count == 0, "Unclassified event types: " + string.Join(", ", unclassified));
        Assert.Empty(NotificationEventTypes.SuppressibleEvents.Intersect(NotificationEventTypes.UnsuppressibleEvents));
        Assert.Empty(NotificationEventTypes.SuppressibleEvents.Except(NotificationEventTypes.All));
        Assert.Empty(NotificationEventTypes.UnsuppressibleEvents.Except(NotificationEventTypes.All));
        Assert.Empty(NotificationEventTypes.RecoverableKinds.Except(NotificationEventTypes.All));
    }

    [Fact]
    public void The_never_suppressed_set_holds_what_the_package_names()
    {
        Assert.Superset(new HashSet<string>
        {
            "automation_suspended", "automation_refused", "webhook_disabled", "agent_approval", "loops_paused_reminder",
            "log_scan_suspended", "monitoring_stalled", "auto_update_failed",
        }, (HashSet<string>)NotificationEventTypes.UnsuppressibleEvents);
        Assert.Superset((HashSet<string>)NotificationEventTypes.AdministrativeEvents, (HashSet<string>)NotificationEventTypes.UnsuppressibleEvents);
        // The per-container mute is overridden by the administrative events only (INV-04) — not by the wider set.
        Assert.Same(NotificationEventTypes.AdministrativeEvents, CompositeNotificationService.AdministrativeEvents);
    }

    [Theory]
    [InlineData(CveSeverity.Critical, false)]
    [InlineData(CveSeverity.High, true)]
    [InlineData(null, true)]
    public void A_CVE_batch_is_unsuppressible_when_its_highest_severity_is_Critical(CveSeverity? highest, bool suppressible)
    {
        Assert.Equal(suppressible, NotificationEventTypes.IsSuppressible(
            new NotificationEvent { EventType = NotificationEventTypes.CveFinding, HighestCveSeverity = highest }));
    }

    [Fact]
    public void The_runtime_patterns_are_known_and_an_unknown_type_is_never_suppressible()
    {
        Assert.True(NotificationEventTypes.IsKnown("log_alert:error"));
        Assert.True(NotificationEventTypes.IsKnown("high_cpu_recovered"));
        Assert.True(NotificationEventTypes.IsKnown("host_api_slow_recovered"));
        Assert.False(NotificationEventTypes.IsKnown("log_alert:"));
        Assert.False(NotificationEventTypes.IsKnown("stopped_recovered"));          // not a breach kind
        Assert.False(NotificationEventTypes.IsKnown("made_up"));
        Assert.False(NotificationEventTypes.IsSuppressible(new NotificationEvent { EventType = "made_up" }));
        Assert.True(NotificationEventTypes.IsSuppressible(new NotificationEvent { EventType = "log_alert:warning" }));
        Assert.Throws<ArgumentException>(() => NotificationEventTypes.Recovered("stopped"));
    }

    [Fact]
    public void Describe_and_the_notifications_log_read_the_registry()
    {
        // Every constant has a title of its own (only the channel test keeps its id), and so has every recovery.
        var raw = NotificationEventTypes.All.Where(t => t != NotificationEventTypes.Test)
            .Concat(NotificationEventTypes.RecoverableKinds.Select(NotificationEventTypes.Recovered))
            .Where(t => NotificationFormatter.Describe(new NotificationEvent { EventType = t }).Title == t)
            .ToList();
        Assert.True(raw.Count == 0, "Event types without a title in NotificationFormatter.Describe: " + string.Join(", ", raw));

        // The notifications log keys its labels by registry constants, not by literals.
        var log = File.ReadAllText(Path.Combine(SourceRoot(), "src", "Whiskers", "Components", "Pages", "NotificationsLog.razor"));
        Assert.DoesNotMatch(new Regex(@"\[""[a-z_]+""\]\s*=\s*""NotificationsLog_Type_"), log);
        Assert.Contains("NotificationEventTypes.", log);
    }

    [Fact]
    public void A_breach_kind_outside_the_registry_is_refused_at_run_time()
    {
        var tracker = new BreachTracker(new HostLoadThresholds());
        Assert.Throws<ArgumentException>(() => tracker.Consider(DateTime.UtcNow, "s", "S", "made_up", 99, 90, (_, _) => "", _ => ""));
    }

    // --- the analysis -------------------------------------------------------------------------------------------------

    private static readonly Regex Registry = new(
        @"^(global::)?(Whiskers\.Services\.Notifications\.)?NotificationEventTypes\.\w+(\(.*\))?$", RegexOptions.Singleline);

    /// <summary>Every <c>EventType = …</c> whose value is not drawn from the registry, as <c>file:line: expression</c>.</summary>
    internal static List<string> Offenders(IReadOnlyList<(string Path, string Text)> sources)
    {
        var roots = sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, path: s.Path).GetRoot()).ToList();
        var invocations = roots.SelectMany(r => r.DescendantNodes().OfType<InvocationExpressionSyntax>()).ToList();
        var offenders = new List<string>();
        foreach (var root in roots)
            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) continue;
                var name = assignment.Left switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => null,
                };
                if (name != "EventType") continue;
                if (Problem(assignment.Right, invocations, depth: 0) is { } problem)
                {
                    var line = assignment.GetLocation().GetLineSpan();
                    offenders.Add($"{Path.GetFileName(line.Path)}:{line.StartLinePosition.Line + 1}: {problem}");
                }
            }
        return offenders;
    }

    private static string? Problem(ExpressionSyntax value, IReadOnlyList<InvocationExpressionSyntax> invocations, int depth)
    {
        value = value is ParenthesizedExpressionSyntax p ? p.Expression : value;
        var text = Regex.Replace(value.ToString(), @"\s+", "");
        if (Registry.IsMatch(text) || Copies.ContainsKey(text)) return null;
        if (value is ConditionalExpressionSyntax conditional)
            return Problem(conditional.WhenTrue, invocations, depth) ?? Problem(conditional.WhenFalse, invocations, depth);
        if (value is IdentifierNameSyntax id && depth < 3
            && value.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() is MethodDeclarationSyntax method
            && method.ParameterList.Parameters.Select((pp, i) => (pp, i))
                .FirstOrDefault(x => x.pp.Identifier.ValueText == id.Identifier.ValueText) is { pp: not null } parameter)
        {
            // A parameter: every call of the method must pass a registry value in its place.
            var calls = invocations.Where(c => (c.Expression switch
            {
                IdentifierNameSyntax n => n.Identifier.ValueText,
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                _ => null,
            }) == method.Identifier.ValueText && c.ArgumentList.Arguments.Count == method.ParameterList.Parameters.Count
                 // A private method is called from its own file only; another file's method of that name is another method.
                 && (!method.Modifiers.Any(SyntaxKind.PrivateKeyword) && method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword))
                     || c.SyntaxTree == method.SyntaxTree)).ToList();
            if (calls.Count == 0) return $"{text} (parameter of {method.Identifier.ValueText}, never called)";
            foreach (var call in calls)
                if (Problem(call.ArgumentList.Arguments[parameter.i].Expression, invocations, depth + 1) is { } inner)
                    return $"{text} ← {method.Identifier.ValueText}({inner})";
            return null;
        }
        return text;
    }

    private static IEnumerable<string> SourceFiles()
    {
        var src = Path.Combine(SourceRoot(), "src", "Whiskers");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Whiskers.slnx not found above the test output.");
    }
}

using System.Text.RegularExpressions;

namespace Whiskers.Tests;

/// <summary>
/// How much of the Docker traffic actually runs under the load budget (Plan-0001 WP6.3).
///
/// <para>The budget sits inside <c>ExecuteAsync</c>/<c>ExecuteGuardedAsync</c>. An operation that takes a bare
/// client from <c>GetClient</c> and calls the daemon itself is invisible to it — no cap, no circuit breaker,
/// no counters. That is not a hypothetical: when the budget was first built, 21 of 24 call sites did exactly
/// that, <b>including the log fetch the 2026-08-26 incident was about</b>. The cap looked complete and
/// covered almost nothing.</para>
///
/// <para>So this test pins the bypasses that remain. It is not a green checkmark — it is a ratchet: adding a
/// new one fails the build, and every one that gets converted must be removed from the list here, which makes
/// the number visible in the diff.</para>
///
/// <para><b>The conversion is finished, and not by converting everything.</b> As of 2026-08-27 every piece of
/// steady background <em>read</em> traffic runs under the budget: container stats (every container, every 30
/// seconds — the largest single source of Docker calls Whiskers makes), host system info, container state
/// inspection, image digests, and the log fetch the incident was about. What remains is bypassed on purpose,
/// and for a better reason than the one originally written here:</para>
///
/// <list type="bullet">
/// <item><b>Interactive operations</b> — start, stop, restart, remove, the env read, every network call. The
/// budget's cap is not the problem; the circuit breaker is. It refuses calls to a server it has given up on,
/// and these are exactly what a person reaches for when a server is in trouble — the moment the circuit is
/// most likely open. Taking the fix away at that moment is worse than the load it saves.</item>
/// <item><b>Long-running operations</b> — the image pull and the container recreate. A budget slot held for
/// minutes starves the health checks and the log scan of the same server. The cap exists to stop many small
/// calls piling up, which is not what these are.</item>
/// <item><b>The nsenter helper</b> — carries its own timeout and lifecycle.</item>
/// </list>
///
/// <para>The earlier note here said mutating operations were excluded because they "must never be
/// auto-retried". That was wrong: <c>ExecuteGuardedAsync</c> does not retry. The reason above is the real
/// one, and it is why converting the rest would make Whiskers worse rather than better.</para>
/// </summary>
public class DockerBudgetCoverageTests
{
    /// <summary>Deliberate bypasses, per file, each with its reason. Shrink these; never grow them.</summary>
    private static readonly Dictionary<string, int> AllowedDirectClientUses = new(StringComparer.Ordinal)
    {
        ["ContainerOperations.cs"] = 6,           // start/stop/restart/remove + env read + disable_restart (contract 0006: the
                                                  // stop of an undecided exposure must not be refused by the circuit); see the note in that file
        ["ContainerLifecycleOperations.cs"] = 3,  // recreate/rollback: minutes long, and mutating
        ["NetworkOperations.cs"] = 5,             // all interactive; a circuit refusal would block the operator
        ["ImageOperations.cs"] = 1,             // the pull only: minutes-long, would starve the other loops
        ["HostShellOperations.cs"] = 1,           // nsenter helper container; has its own timeout
        // SystemInfoOperations.cs: converted 2026-08-27 — it ran once per server on every metrics cycle,
        // which is precisely the steady background traffic the cap exists for.
    };

    private static string OperationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whiskers.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, $"could not find Whiskers.slnx above {AppContext.BaseDirectory}");
        return Path.Combine(dir!.FullName, "src", "Whiskers", "Services", "Docker", "Operations");
    }

    [Fact]
    public void No_new_docker_call_bypasses_the_load_budget()
    {
        var directClient = new Regex(@"await\s+GetClient\(", RegexOptions.Compiled);
        var files = Directory.GetFiles(OperationsDirectory(), "*.cs");

        // A scanner that finds nothing and reports success would be the very blindness this package removes.
        Assert.True(files.Length > 0, "no Docker operation sources found");

        var actual = files.ToDictionary(
            Path.GetFileName,
            f => directClient.Matches(File.ReadAllText(f)).Count,
            StringComparer.Ordinal);

        var problems = new List<string>();

        foreach (var (file, count) in actual.Where(kv => kv.Value > 0))
        {
            var allowed = AllowedDirectClientUses.GetValueOrDefault(file, 0);
            if (count > allowed)
                problems.Add($"{file}: {count} calls take a bare Docker client, {allowed} are recorded. " +
                             "A new one is outside the budget and the circuit breaker — route it through " +
                             "ExecuteAsync (retry wanted) or ExecuteGuardedAsync (no retry).");
        }

        foreach (var (file, allowed) in AllowedDirectClientUses)
        {
            var count = actual.GetValueOrDefault(file, 0);
            if (count < allowed)
                problems.Add($"{file}: {count} bypasses left, {allowed} recorded — good news, update the list " +
                             "in this test so the ratchet keeps holding.");
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    [Fact]
    public void The_log_fetch_runs_under_the_budget()
    {
        // Pinned on its own: this is the call from the incident. If it ever goes back to a bare client the
        // whole package is undone, and it would happen quietly.
        var source = File.ReadAllText(Path.Combine(OperationsDirectory(), "ContainerOperations.cs"));
        var logFetch = source[source.IndexOf("public async Task<string> GetContainerLogsAsync", StringComparison.Ordinal)..];
        logFetch = logFetch[..logFetch.IndexOf("\n    public ", StringComparison.Ordinal)];

        Assert.Contains("ExecuteGuardedAsync", logFetch);
        Assert.DoesNotContain("await GetClient(", logFetch);
    }
}

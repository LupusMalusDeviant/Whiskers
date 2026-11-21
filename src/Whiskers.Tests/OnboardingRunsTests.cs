using Whiskers.Services.Onboarding;

namespace Whiskers.Tests;

/// <summary>Contract 0018 part E: the in-memory run registry behind <c>get_onboarding_status</c> — the latest run per
/// server, its progress capped at 200 lines, and a finished run that takes no more lines.</summary>
public sealed class OnboardingRunsTests
{
    [Fact]
    public void Finish_sets_the_step_and_a_report_after_finish_is_dropped()
    {
        var runs = new OnboardingRuns();
        var run = runs.Begin("web-new", "op-1");
        run.Report(new OnboardingProgress(OnboardingStep.Bootstrap, "bootstrap"));

        run.Finish(OnboardingResult.Fail([OnboardingStep.HostKey], OnboardingStep.TailscaleLogin, "timeout"));
        run.Report(new OnboardingProgress(OnboardingStep.Verify, "late line", "https://login.tailscale.com/a/late"));

        var snapshot = runs.Latest("web-new")!;
        Assert.True(snapshot.Finished);
        Assert.Equal(OnboardingStep.TailscaleLogin, snapshot.CurrentStep);   // the failed step, not the late report's
        Assert.Equal(["bootstrap"], snapshot.Log);
        Assert.Null(snapshot.TailscaleLoginUrl);
        Assert.Null(runs.Latest("WEB-NEW"));                                // exact id only
    }

    [Fact]
    public void A_new_run_replaces_the_latest_and_the_log_keeps_the_newest_lines()
    {
        var runs = new OnboardingRuns();
        runs.Begin("web-new", "op-1").Finish(OnboardingResult.Ok([OnboardingStep.HostKey]));
        var second = runs.Begin("web-new", "op-2");
        for (var i = 0; i < OnboardingRuns.MaxLogLines + 5; i++)
            second.Report(new OnboardingProgress(OnboardingStep.Docker, $"line {i}"));

        var snapshot = runs.Latest("web-new")!;
        Assert.Equal("op-2", snapshot.OperationId);
        Assert.False(snapshot.Finished);
        Assert.Equal(OnboardingRuns.MaxLogLines, snapshot.Log.Count);
        Assert.Equal("line 5", snapshot.Log[0]);
    }
}

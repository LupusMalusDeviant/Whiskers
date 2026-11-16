using Whiskers.Services.Scheduler;

namespace Whiskers.Tests;

public class CronValidationTests
{
    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 3 * * *")]
    [InlineData("15 14 1 * *")]
    [InlineData("0 0 29 2 *")]   // fires in leap years only, but fires
    public void TryParseCron_ValidExpressions(string expr)
    {
        Assert.True(SchedulerService.TryParseCron(expr, out var schedule));
        Assert.NotNull(schedule);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a cron")]
    [InlineData("99 99 99 99 99")]
    [InlineData("* * *")]
    public void TryParseCron_InvalidExpressions_ReturnFalseWithoutThrowing(string expr)
    {
        // The error path: an invalid schedule fails cleanly here instead of throwing every 30s in the loop.
        Assert.False(SchedulerService.TryParseCron(expr, out var schedule));
        Assert.Null(schedule);
    }

    [Theory]
    [InlineData("0 0 30 2 *")]
    [InlineData("0 0 31 4 *")]
    public void TryParseCron_ExpressionsThatNeverFire_AreInvalid(string expr)
    {
        // Parses, but has no next run: accepted, its NextRun would be the year 9999 and the task would silently never run.
        Assert.False(SchedulerService.TryParseCron(expr, out var schedule));
        Assert.Null(schedule);
    }

    [Fact]
    public void Task_times_are_shown_in_utc_like_the_cron_beside_them()
    {
        // Cron times are UTC; shown in the server's local time, "0 3 * * *" read as 04:00 on a CET host.
        var utc = new DateTime(2026, 11, 17, 3, 17, 0, DateTimeKind.Utc);
        Assert.Equal("2026-11-17 03:17 UTC", SchedulerService.FormatUtc(utc));
        Assert.Equal("2026-11-17 03:17 UTC", SchedulerService.FormatUtc(utc.ToLocalTime()));
        Assert.Equal("17.11. 03:17 UTC", SchedulerService.FormatUtc(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), "dd.MM. HH:mm"));
        Assert.Equal("", SchedulerService.FormatUtc(null));
    }
}

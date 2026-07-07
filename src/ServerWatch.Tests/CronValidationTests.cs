using ServerWatch.Services.Scheduler;

namespace ServerWatch.Tests;

public class CronValidationTests
{
    [Theory]
    [InlineData("*/5 * * * *")]
    [InlineData("0 3 * * *")]
    [InlineData("15 14 1 * *")]
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
}

using Whiskers.Services.Terminal;
using Xunit;

namespace Whiskers.Tests;

public class TerminalSessionTests
{
    [Fact]
    public void Touch_AdvancesLastActivity()
    {
        // A stale session (last active 30 min ago) — the idle sweep would reap this.
        var session = new TerminalSession { LastActivityAt = DateTime.UtcNow.AddMinutes(-30) };
        var stale = session.LastActivityAt;

        session.Touch();

        // Touch() (called on every output chunk) resets activity to ~now, so streaming output
        // keeps the session alive.
        Assert.True(session.LastActivityAt > stale);
        Assert.True(DateTime.UtcNow - session.LastActivityAt < TimeSpan.FromSeconds(5));
    }
}

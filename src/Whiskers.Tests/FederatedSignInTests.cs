using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Whiskers.Models;
using Whiskers.Services.Auth;

namespace Whiskers.Tests;

/// <summary>R1-22: the Google/OIDC whitelist gate and the failed provider round trip are audited; the gate decides
/// exactly as before.</summary>
public sealed class FederatedSignInTests
{
    private sealed class Whitelist(params string[] allowed) : IWhitelistService
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool IsEmailAllowed(string? email) => email is not null && allowed.Contains(email);
        public WhitelistData GetWhitelist() => new();
        public Task SaveWhitelistAsync(WhitelistData data) => Task.CompletedTask;
    }

    private sealed class Recording : IAuthAuditLog
    {
        public List<(string Kind, string Method, string? Account, string? Reason)> Rows { get; } = [];

        public Task SignedInAsync(HttpContext context, string method, string email)
        {
            Rows.Add(("in", method, email, null));
            return Task.CompletedTask;
        }

        public Task SignInRefusedAsync(HttpContext context, string method, string? account, string reason)
        {
            Rows.Add(("refused", method, account, reason));
            return Task.CompletedTask;
        }

        public Task SignedOutAsync(HttpContext context) => Task.CompletedTask;
    }

    private static (DefaultHttpContext Http, Recording Audit) Http(params string[] allowed)
    {
        var audit = new Recording();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IWhitelistService>(new Whitelist(allowed))
                .AddSingleton<IAuthAuditLog>(audit)
                .BuildServiceProvider(),
        };
        return (http, audit);
    }

    private static TicketReceivedContext Ticket(HttpContext http, string scheme, string email)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, email)], scheme));
        return new TicketReceivedContext(http, new AuthenticationScheme(scheme, scheme, typeof(GoogleHandler)),
            new GoogleOptions(), new AuthenticationTicket(principal, scheme));
    }

    [Theory]
    [InlineData("Google", "google")]
    [InlineData("oidc", "oidc")]
    public async Task An_allowed_sign_in_passes_and_is_recorded(string scheme, string method)
    {
        var (http, audit) = Http("ops@example.com");
        var context = Ticket(http, scheme, "ops@example.com");

        await FederatedSignIn.WhitelistGateAsync(context, "/sw");

        Assert.Null(context.Result);   // not handled: the cookie is issued as before
        Assert.Equal(("in", method, "ops@example.com", null), Assert.Single(audit.Rows));
    }

    [Fact]
    public async Task A_sign_in_off_the_whitelist_is_refused_as_before_and_recorded()
    {
        var (http, audit) = Http("ops@example.com");
        var context = Ticket(http, "oidc", "intruder@example.com");

        await FederatedSignIn.WhitelistGateAsync(context, "/sw");

        Assert.True(context.Result!.Handled);
        Assert.Equal("/sw/login?error=unauthorized", http.Response.Headers.Location.ToString());
        Assert.Equal(("refused", "oidc", "intruder@example.com", AuthAuditReasons.NotWhitelisted), Assert.Single(audit.Rows));
    }

    [Fact]
    public async Task A_failed_provider_round_trip_is_recorded_and_left_to_the_default_handling()
    {
        var (http, audit) = Http();
        var context = new RemoteFailureContext(http, new AuthenticationScheme("oidc", "oidc", typeof(GoogleHandler)),
            new GoogleOptions(), new AuthenticationFailureException("Correlation failed."));

        await FederatedSignIn.RecordRemoteFailureAsync(context);

        Assert.Null(context.Result);   // not handled: the default failure path still runs
        Assert.Equal(("refused", "oidc", null, AuthAuditReasons.ProviderFailure), Assert.Single(audit.Rows));
    }

    private sealed class CountingRecovery : Whiskers.Services.Mutations.IAuditRecovery
    {
        public int Lost;
        public void OutcomeNotWritten(Whiskers.Services.Mutations.PendingAuditOutcome outcome) { }
        public void LegacyWriteFailed() => Interlocked.Increment(ref Lost);
        public Whiskers.Services.Mutations.AuditHealth Health => new(0, 0, Lost > 0);
        public Task RecoverAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>An audit store whose writes wait for <see cref="Release"/>, as when its database stalls.</summary>
    private sealed class StalledAudit : Whiskers.Services.AuditLog.IAuditLogService
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<DateTime> Stamps { get; } = [];
        public void Release() => _gate.TrySetResult();

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) =>
            TryLogAsync(actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

        public Task<bool> TryLogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true) =>
            TryLogAtAsync(DateTime.UtcNow, actor, actorType, action, targetType, targetId, targetName, details, serverId, success);

        public async Task<bool> TryLogAtAsync(DateTime occurredUtc, string actor, string actorType, string action,
            string targetType, string targetId, string targetName, string? details = null, string? serverId = null,
            bool success = true)
        {
            await _gate.Task;
            lock (Stamps) Stamps.Add(occurredUtc);
            return true;
        }

        public Task<List<Whiskers.Models.AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0,
            string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(new List<Whiskers.Models.AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    [Fact]
    public async Task A_row_written_late_keeps_the_time_it_happened()
    {
        var audit = new StalledAudit();
        var log = new AuthAuditLog(audit, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthAuditLog>.Instance);
        await log.StartAsync(CancellationToken.None);
        var from = DateTime.UtcNow;
        await log.SignedInAsync(new DefaultHttpContext(), "local", "a@example.com");
        await log.SignedInAsync(new DefaultHttpContext(), "local", "b@example.com");
        var queued = DateTime.UtcNow;

        await Task.Delay(TimeSpan.FromMilliseconds(400));
        audit.Release();
        await log.StopAsync(CancellationToken.None);

        Assert.Equal(2, audit.Stamps.Count);
        Assert.All(audit.Stamps, stamp => Assert.InRange(stamp, from, queued));
    }

    [Fact]
    public async Task A_full_sign_in_queue_reports_every_row_it_cannot_take_and_never_blocks()
    {
        // The writer is not started: nothing drains the queue, as with a stalled audit database.
        var recovery = new CountingRecovery();
        var log = new AuthAuditLog(new TerminalDoubles.RecordingAudit(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthAuditLog>.Instance, recovery);
        var http = new DefaultHttpContext();

        var all = Task.WhenAll(Enumerable.Range(0, AuthAuditLog.Capacity + 3)
            .Select(i => log.SignedInAsync(http, "local", $"u{i}@example.com")));

        Assert.True(all.IsCompletedSuccessfully);   // no call waited
        await all;
        Assert.Equal(3, recovery.Lost);
        Assert.True(recovery.Health.LegacyWriteFailed);
    }
}

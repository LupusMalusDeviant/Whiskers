using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Models.Agent;
using Whiskers.Services.Agent;
using Whiskers.Services.Agent.Providers;
using Whiskers.Services.Approvals;
using Whiskers.Services.Auth;
using Whiskers.Services.Guardrails;
using Whiskers.Services.Mutations;
using Whiskers.Services.Persistence;
using Whiskers.Services.Terminal;
using static Whiskers.Tests.TerminalDoubles;

namespace Whiskers.Tests;

/// <summary>
/// R1-26 (INV-09): deleting a local account, removing a user from the whitelist or lowering their role ends their cookie
/// sessions, Blazor circuits and terminals within 60 seconds, and every security-relevant failure of the check denies.
/// The cookie part runs through the real pipeline; circuits run on an injected clock; terminals on their sweep.
/// </summary>
[Collection("WebAppBoot")] // the cookie test boots the app and needs the process-wide WHISKERS_DATA_DIR
public sealed class SessionRevocationTests
{
    private static readonly TimeSpan Inv09Bound = TimeSpan.FromSeconds(60);

    public enum Change { LocalDeletion, WhitelistRemoval, RoleDemotion }

    // ------------------------------------------------------------------------------------------------ cookies (wire)

    [Fact]
    public async Task Cookies_end_on_their_next_request_after_deletion_whitelist_removal_demotion_or_a_new_stamp() =>
        await BootAsync(async (factory, newClient) =>
        {
            const string deleted = "deleted@local.test", unlisted = "unlisted@local.test",
                demoted = "demoted@local.test", restamped = "restamped@local.test";
            var roles = factory.Services.GetRequiredService<IRoleService>();
            foreach (var email in new[] { deleted, unlisted, demoted, restamped })
            {
                await CreateLocalUserAsync(factory, email);
                await roles.SetRoleAsync(email, AppRole.Admin);
            }

            var clients = new Dictionary<string, HttpClient>();
            foreach (var email in new[] { deleted, unlisted, demoted, restamped })
            {
                clients[email] = newClient();
                await SignInAsync(clients[email], email);
                // Signed in as Admin: the admin-only download answers "no such archive", not a refusal.
                Assert.Equal(HttpStatusCode.NotFound, (await AdminProbeAsync(clients[email])).StatusCode);
            }

            // An account without a stamp gets one at sign-in and stays signed in (no sign-out loop).
            const string unstamped = "unstamped@local.test";
            await CreateLocalUserAsync(factory, unstamped);
            await roles.SetRoleAsync(unstamped, AppRole.Admin);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
                var row = await db.Users.SingleAsync(u => u.Email == unstamped);
                row.SecurityStamp = null;
                await db.SaveChangesAsync();
            }
            var unstampedClient = newClient();
            await SignInAsync(unstampedClient, unstamped);
            Assert.Equal(HttpStatusCode.NotFound, (await AdminProbeAsync(unstampedClient)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await AdminProbeAsync(unstampedClient)).StatusCode);

            // Local deletion.
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                Assert.True((await users.DeleteAsync((await users.FindByEmailAsync(deleted))!)).Succeeded);
            }
            AssertSignedOut(await AdminProbeAsync(clients[deleted]));

            // A new security stamp (what a password change does, R1-49).
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                await users.UpdateSecurityStampAsync((await users.FindByEmailAsync(restamped))!);
            }
            AssertSignedOut(await AdminProbeAsync(clients[restamped]));

            // Role demotion: the cookie carries no role; the very next request holds the lowered one.
            await roles.SetRoleAsync(demoted, AppRole.Operator);
            var refused = await AdminProbeAsync(clients[demoted]);
            Assert.NotEqual(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.DoesNotContain("/login", refused.Headers.Location?.OriginalString ?? "");

            // Whitelist removal (everyone else stays listed).
            var whitelist = factory.Services.GetRequiredService<IWhitelistService>();
            await whitelist.SaveWhitelistAsync(new WhitelistData
            {
                Enabled = true, Emails = [Email, demoted],
            });
            AssertSignedOut(await AdminProbeAsync(clients[unlisted]));
        });

    // ------------------------------------------------------------------------------------------------ circuits

    [Theory]
    [InlineData(Change.LocalDeletion)]
    [InlineData(Change.WhitelistRemoval)]
    [InlineData(Change.RoleDemotion)]
    public async Task Circuits_end_within_sixty_seconds(Change change)
    {
        await using var world = await World.CreateAsync();
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-03T10:00:00Z"));
        using var provider = new SessionRevalidatingAuthenticationStateProvider(world.Validator, clock);
        var ended = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AuthenticationStateChanged += async t =>
        {
            var s = await t;
            if (s.User.Identity?.IsAuthenticated != true) ended.TrySetResult(s);
        };

        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(world.OpsCookiePrincipal())));
        await WaitUntilAsync(() => clock.Periods.Count == 1);
        Assert.True(clock.Periods.Single() <= TimeSpan.FromSeconds(30));
        Assert.True(await provider.RevalidateAsync());   // still valid: the circuit goes on

        var start = clock.GetUtcNow();
        await world.ApplyAsync(change);
        clock.Advance(SessionRevalidatingAuthenticationStateProvider.RevalidationInterval);

        var state = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.True(clock.GetUtcNow() - start <= Inv09Bound);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        Assert.Empty(clock.Periods);                     // nothing is watched any more
    }

    [Fact]
    public async Task A_raised_role_keeps_the_circuit()
    {
        await using var world = await World.CreateAsync();
        await world.Roles.SetRoleAsync(World.Ops, AppRole.Operator);
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var provider = new SessionRevalidatingAuthenticationStateProvider(world.Validator, clock);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(world.OpsCookiePrincipal())));
        await WaitUntilAsync(() => clock.Periods.Count == 1);

        await world.Roles.SetRoleAsync(World.Ops, AppRole.Admin);
        Assert.True(await provider.RevalidateAsync());
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task A_raised_then_lowered_role_ends_the_circuit()
    {
        // A page opened after the promotion cached Admin; lowering the role again must end the circuit, even though
        // the new role is still at least the one the circuit started with.
        await using var world = await World.CreateAsync();
        await world.Roles.SetRoleAsync(World.Ops, AppRole.Viewer);
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var provider = new SessionRevalidatingAuthenticationStateProvider(world.Validator, clock);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(world.OpsCookiePrincipal())));
        await WaitUntilAsync(() => clock.Periods.Count == 1);

        await world.Roles.SetRoleAsync(World.Ops, AppRole.Admin);
        Assert.True(await provider.RevalidateAsync());               // promoted: kept
        await world.Roles.SetRoleAsync(World.Ops, AppRole.Operator);
        Assert.False(await provider.RevalidateAsync());              // below the highest role it held: ended
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    // ------------------------------------------------------------------------------------------------ hub calls

    [Theory]
    [InlineData(Change.LocalDeletion)]
    [InlineData(Change.WhitelistRemoval)]
    public async Task An_open_hub_connection_is_refused_and_closed_on_its_next_call(Change change)
    {
        await using var world = await World.CreateAsync();
        var filter = new Whiskers.Hubs.SessionHubFilter(world.Validator);
        var connection = new HubConnection(world.OpsCookiePrincipal());
        var calls = 0;
        ValueTask<object?> Next(Microsoft.AspNetCore.SignalR.HubInvocationContext _) { calls++; return ValueTask.FromResult<object?>(null); }
        var invocation = new Microsoft.AspNetCore.SignalR.HubInvocationContext(connection, world.Scopes.CreateScope().ServiceProvider,
            new NoHub(), typeof(NoHub).GetMethod(nameof(NoHub.Ping))!, []);

        await filter.InvokeMethodAsync(invocation, Next);
        Assert.Equal(1, calls);

        await world.ApplyAsync(change);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => filter.InvokeMethodAsync(invocation, Next).AsTask());
        Assert.Equal(1, calls);
        Assert.True(connection.Aborted);
    }

    private sealed class NoHub : Microsoft.AspNetCore.SignalR.Hub
    {
        public void Ping() { }
    }

    private sealed class HubConnection(ClaimsPrincipal user) : Microsoft.AspNetCore.SignalR.HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId => "c1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } =
            new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;
    }

    // ------------------------------------------------------------------------------------------------ audit rows

    [Fact]
    public async Task A_replayed_revoked_cookie_is_audited_once_per_minute_not_per_request()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var audit = new RecordingAudit();
        var log = new AuthAuditLog(audit, NullLogger<AuthAuditLog>.Instance, time: clock);
        await log.StartAsync(CancellationToken.None);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        for (var i = 0; i < 50; i++)
            await log.SessionRevokedAsync(http, World.Ops, "cookie", SessionRevocationReasons.AccountDeleted);
        await log.SessionRevokedAsync(http, World.Ops, "cookie", SessionRevocationReasons.NotWhitelisted);   // other reason
        clock.Advance(AuthAuditLog.RevokedRowWindow);
        await log.SessionRevokedAsync(http, World.Ops, "cookie", SessionRevocationReasons.AccountDeleted);   // next window
        await log.StopAsync(CancellationToken.None);

        Assert.Equal(3, audit.Rows.Count(r => r.Action == AuthAuditLog.SessionRevokedAction));
    }

    // ------------------------------------------------------------------------------------------------ page rendering

    [Fact]
    public async Task Page_role_lookups_reuse_a_fresh_check_but_a_mutation_check_never_does()
    {
        var counting = new CountingValidator();
        var user = new CurrentUserService(new FixedState(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Email, World.Ops)], "Google"))), new AutomationMutationRunnerTests.Roles(),
            counting, new ManualClock(DateTimeOffset.UnixEpoch));

        await user.GetRoleAsync();
        await user.GetRoleAsync();
        await user.HasRoleAsync(AppRole.Admin);
        Assert.Equal(1, counting.Calls);
        await user.CheckSessionAsync();
        await user.CheckSessionAsync();
        Assert.Equal(3, counting.Calls);
    }

    private sealed class CountingValidator : ISessionValidator
    {
        public int Calls;
        public Task<SessionCheck> CheckAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(SessionCheck.Allowed(AppRole.Admin));
        }
    }

    // ------------------------------------------------------------------------------------------------ terminals

    [Theory]
    [InlineData(Change.LocalDeletion)]
    [InlineData(Change.WhitelistRemoval)]
    [InlineData(Change.RoleDemotion)]
    public async Task Terminals_end_within_sixty_seconds(Change change)
    {
        await using var world = await World.CreateAsync();
        Assert.True(TerminalSessionManager.SweepInterval <= Inv09Bound);
        var audit = new RecordingAudit();
        var launcher = new FakeLauncher();
        using var manager = Manager(launcher, audit, new TerminalAuthority(
            new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault()), world.Owners));
        var ops = new MutationPrincipal(MutationPrincipalKind.Human, PrincipalIds.User(World.Ops), World.Ops,
            McpPermissionLevels.Admin);
        using (Decided("terminal.host_open", "t1", ops, "host", "local", "local"))
            await manager.OpenHostShellAsync(new TerminalOpenRequest("t1", "local"));

        await manager.SweepAsync();
        Assert.Single(manager.GetActiveSessions());

        await world.ApplyAsync(change);
        await manager.SweepAsync();

        Assert.Empty(manager.GetActiveSessions());
        Assert.True(launcher.Processes.Single().HasExited);
    }

    // ------------------------------------------------------------------------------------------------ fail closed

    [Fact]
    public async Task Security_relevant_check_failures_deny()
    {
        await using var world = await World.CreateAsync();
        Assert.True((await world.Validator.CheckAsync(world.OpsCookiePrincipal())).Valid);

        // A cookie issued before R1-26 carries no stamp; a forged one a wrong stamp.
        Assert.Equal(SessionRevocationReasons.StampMismatch,
            (await world.Validator.CheckAsync(world.OpsCookiePrincipal(stamp: null))).Reason);
        Assert.Equal(SessionRevocationReasons.StampMismatch,
            (await world.Validator.CheckAsync(world.OpsCookiePrincipal(stamp: "other"))).Reason);
        // No email; synthetic identities with auth enabled; an anonymous principal.
        Assert.Equal(SessionRevocationReasons.NoEmail, (await world.Validator.CheckAsync(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "x")], "Google")))).Reason);
        foreach (var scheme in new[] { AuthConstants.AuthDisabledScheme, AuthConstants.AgentSyntheticScheme })
            Assert.Equal(SessionRevocationReasons.SyntheticIdentity, (await world.Validator.CheckAsync(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Email, World.Ops)], scheme)))).Reason);
        Assert.False((await world.Validator.CheckAsync(new ClaimsPrincipal(new ClaimsIdentity()))).Valid);

        // The account store cannot be read: no table, and no store at all.
        await world.DropUsersTableAsync();
        Assert.Equal(SessionRevocationReasons.StoreUnavailable,
            (await world.Validator.CheckAsync(world.OpsCookiePrincipal())).Reason);
        var noStore = new SessionValidator(new ServiceCollection().BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(), world.Whitelist, world.Roles, world.Configuration);
        Assert.Equal(SessionRevocationReasons.StoreUnavailable,
            (await noStore.CheckAsync(world.OpsCookiePrincipal())).Reason);
    }

    [Fact]
    public async Task A_check_that_throws_ends_the_circuit()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var flaky = new FlakyValidator();
        using var provider = new SessionRevalidatingAuthenticationStateProvider(flaky, clock);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Email, World.Ops)], "Google")))));
        await WaitUntilAsync(() => clock.Periods.Count == 1);

        flaky.Throw = true;
        Assert.False(await provider.RevalidateAsync());
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Auth_disabled_keeps_its_local_user_and_a_cookie_never_gets_it()
    {
        await using var world = await World.CreateAsync();
        var local = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, CurrentUserService.AuthDisabledEmail)], AuthConstants.AuthDisabledScheme));
        var disabled = new SessionValidator(world.Scopes, world.Whitelist, world.Roles,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Disabled"] = "true" }).Build());

        Assert.Equal(SessionCheck.Allowed(AppRole.Admin), await disabled.CheckAsync(local));
        Assert.Equal(SessionRevocationReasons.SyntheticIdentity, (await world.Validator.CheckAsync(local)).Reason);
    }

    // ------------------------------------------------------------------------------------------------ mutating handlers

    [Theory]
    [InlineData(Change.LocalDeletion)]
    [InlineData(Change.WhitelistRemoval)]
    [InlineData(Change.RoleDemotion)]
    public async Task A_ui_mutation_rechecks_the_session_instead_of_the_role_the_page_cached(Change change)
    {
        await using var world = await World.CreateAsync();
        var user = new CurrentUserService(new FixedState(world.OpsCookiePrincipal()), world.Roles, world.Validator);
        var cachedRole = await user.GetRoleAsync();                 // what a page loads in OnInitializedAsync
        Assert.Equal(AppRole.Admin, cachedRole);

        var audit = new AgentToolTestHelpers.NullMutationAudit();
        var store = new AgentToolTestHelpers.FixedGuardrailStore(GuardrailPolicy.SafeDefault());
        var gateway = new MutationGateway(new CapabilityCatalog(), AgentToolTestHelpers.Policy(store), audit, [],
            NullLogger<MutationGateway>.Instance, approvals: new ApprovalGate(new ApprovalStore(), store));
        var runner = new UiMutationRunner(gateway, new CapabilityCatalog(), user);
        var before = false;
        await runner.RunAsync("config.roles", new { }, _ => { before = true; return Task.CompletedTask; });
        Assert.True(before);                                        // control: the Admin's session runs it

        await world.ApplyAsync(change);
        var ran = false;
        // config.roles needs Admin — what the page's cached role still claims.
        await Assert.ThrowsAsync<MutationRefusedException>(() => runner.RunAsync("config.roles", new { },
            _ => { ran = true; return Task.CompletedTask; }));

        Assert.False(ran);
        Assert.Contains(audit.Events, e => e.StartsWith("denied:", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ agent of a web user

    [Fact]
    public async Task A_web_users_inflight_agent_run_stops_when_their_account_is_deleted()
    {
        await using var world = await World.CreateAsync();
        var authority = new AgentAuthority(new AutomationMutationRunnerTests.Keys(), world.Owners, world.Roles);
        var invoker = new AgentRunDoubles.Invoker(onCall: calls =>
        {
            if (calls == 1) world.ApplyAsync(Change.LocalDeletion).GetAwaiter().GetResult();
        });
        var session = AgentRunDoubles.Session(new AgentPrincipal(AgentPrincipalKind.WebUser, World.Ops,
                McpPermissionLevels.Admin, null, UserEmail: World.Ops),
            AgentRunDoubles.Provider(AgentRunDoubles.ToolTurn("c1", "c2")), invoker, authority);

        var events = await AgentRunDoubles.CollectAsync(session);

        Assert.Equal(1, invoker.Calls);
        Assert.Contains("deleted", events.OfType<AgentEvent.ToolExecuted>().Last().Result.Content);
    }

    [Fact]
    public async Task A_web_users_inflight_agent_run_takes_its_next_call_at_the_lowered_role()
    {
        await using var world = await World.CreateAsync();
        var authority = new AgentAuthority(new AutomationMutationRunnerTests.Keys(), world.Owners, world.Roles);
        var invoker = new AgentRunDoubles.Invoker(onCall: calls =>
        {
            if (calls == 1) world.ApplyAsync(Change.RoleDemotion).GetAwaiter().GetResult();
        });
        var session = AgentRunDoubles.Session(new AgentPrincipal(AgentPrincipalKind.WebUser, World.Ops,
                McpPermissionLevels.Admin, null, UserEmail: World.Ops),
            AgentRunDoubles.Provider(AgentRunDoubles.ToolTurn("c1", "c2")), invoker, authority);

        await AgentRunDoubles.CollectAsync(session);

        Assert.Equal(new[] { McpPermissionLevels.Admin, McpPermissionLevels.Write }, invoker.Levels);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class FixedState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class FlakyValidator : ISessionValidator
    {
        public bool Throw { get; set; }
        public Task<SessionCheck> CheckAsync(ClaimsPrincipal? principal, CancellationToken ct = default) =>
            Throw ? throw new InvalidOperationException("store down") : Task.FromResult(SessionCheck.Allowed(AppRole.Admin));
    }

    /// <summary>The stores a session check reads, on disk: a local Identity database, roles.json, whitelist.json.
    /// <see cref="Ops"/> is a local Admin with a row, an explicit role and (whitelist disabled) the right to sign in.</summary>
    private sealed class World : IAsyncDisposable
    {
        public const string Root = "root@local.test";
        public const string Ops = "ops@local.test";
        private const string OpsId = "u-ops";
        private const string Stamp = "stamp-1";

        private readonly string _dir;
        private readonly ServiceProvider _sp;
        public IConfiguration Configuration { get; }
        public RoleService Roles { get; }
        public WhitelistService Whitelist { get; }
        public SessionValidator Validator { get; }
        public AutomationOwnerResolver Owners { get; }
        public IServiceScopeFactory Scopes => _sp.GetRequiredService<IServiceScopeFactory>();

        private World(string dir, ServiceProvider sp, IConfiguration configuration, RoleService roles,
            WhitelistService whitelist, DataPathOptions paths)
        {
            _dir = dir;
            _sp = sp;
            Configuration = configuration;
            Roles = roles;
            Whitelist = whitelist;
            Validator = new SessionValidator(Scopes, whitelist, roles, configuration);
            Owners = new AutomationOwnerResolver(roles, whitelist, new AutomationMutationRunnerTests.Keys(),
                new OwnerAccounts(Scopes, configuration, paths));
        }

        public static async Task<World> CreateAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"sw-revoke-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var paths = new DataPathOptions(dir);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WHISKERS_ADMIN_EMAIL"] = Root,
            }).Build();
            var services = new ServiceCollection();
            services.AddDbContext<WhiskersIdentityDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dir, "id.db")}"));
            var sp = services.BuildServiceProvider();
            using (var scope = sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Users.Add(new AppUser
                {
                    Id = OpsId, UserName = Ops, NormalizedUserName = Ops.ToUpperInvariant(),
                    Email = Ops, NormalizedEmail = Ops.ToUpperInvariant(), SecurityStamp = Stamp,
                });
                await db.SaveChangesAsync();
            }
            var roles = new RoleService(configuration, NullLogger<RoleService>.Instance, dataPaths: paths);
            await roles.InitializeAsync();
            await roles.SetRoleAsync(Ops, AppRole.Admin);
            var whitelist = new WhitelistService(configuration, roles, NullLogger<WhitelistService>.Instance, paths);
            await whitelist.InitializeAsync();
            return new World(dir, sp, configuration, roles, whitelist, paths);
        }

        /// <summary>The principal <c>/login-local</c> puts into Ops's cookie.</summary>
        public ClaimsPrincipal OpsCookiePrincipal(string? stamp = Stamp)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Email, Ops), new(ClaimTypes.Name, Ops), new(ClaimTypes.NameIdentifier, OpsId),
            };
            if (stamp is not null) claims.Add(new Claim(AuthConstants.SecurityStampClaim, stamp));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthConstants.LocalPasswordScheme));
        }

        public async Task ApplyAsync(Change change)
        {
            switch (change)
            {
                case Change.LocalDeletion:
                    using (var scope = _sp.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>();
                        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == OpsId));
                        await db.SaveChangesAsync();
                    }
                    break;
                case Change.WhitelistRemoval:
                    await Whitelist.SaveWhitelistAsync(new WhitelistData { Enabled = true, Emails = [Root] });
                    break;
                case Change.RoleDemotion:
                    await Roles.SetRoleAsync(Ops, AppRole.Operator);
                    break;
            }
        }

        public async Task DropUsersTableAsync()
        {
            using var scope = _sp.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WhiskersIdentityDbContext>().Database
                .ExecuteSqlRawAsync("DROP TABLE \"AspNetUsers\"");
        }

        public async ValueTask DisposeAsync()
        {
            await _sp.DisposeAsync();
            TestSqlite.ClearPools(_dir);
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp */ }
        }
    }

    // ------------------------------------------------------------------------------------------------ wire boot

    private const string Email = "admin@local.test";
    private const string Password = "CorrectHorse12!!";

    private static async Task CreateLocalUserAsync(WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var created = await users.CreateAsync(new AppUser { UserName = email, Email = email }, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Code)));
    }

    private static async Task SignInAsync(HttpClient client, string email)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token));
        var response = await client.PostAsync("/login-local", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = Password, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("error", response.Headers.Location!.OriginalString);
    }

    /// <summary>The Admin-only self-backup download for an archive that does not exist: 404 for an Admin.</summary>
    private static Task<HttpResponseMessage> AdminProbeAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/backups/self/none/download");
        request.Headers.Accept.ParseAdd("text/html");
        return client.SendAsync(request);
    }

    private static void AssertSignedOut(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.OriginalString);
        // The cookie itself is removed, not only refused.
        Assert.Contains(response.Headers, h => h.Key == "Set-Cookie"
            && h.Value.Any(v => v.StartsWith(".AspNetCore.Cookies=;", StringComparison.Ordinal)));
    }

    private static async Task BootAsync(Func<WebApplicationFactory<Program>, Func<HttpClient>, Task> body)
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"sw-revoke-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        var pwFile = Path.Combine(dataDir, "admin-pw.txt");
        await File.WriteAllTextAsync(pwFile, Password);
        var prev = Environment.GetEnvironmentVariable("WHISKERS_DATA_DIR");
        Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", dataDir);

        var config = new List<KeyValuePair<string, string?>>
        {
            new("Auth:Disabled", "false"),
            new("WHISKERS_ADMIN_EMAIL", Email),
            new("WHISKERS_ADMIN_PASSWORD_FILE", pwFile),
        };
        config.AddRange(BootSettings.OnlyCore());

        try
        {
            using var bootSettings = BootSettings.Apply(config);
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config));
            });
            var clients = new List<HttpClient>();
            try
            {
                BootSettings.AssertBoot(factory.Services, config);
                await body(factory, () =>
                {
                    var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                    clients.Add(client);
                    return client;
                });
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("WHISKERS_DATA_DIR", prev);
            TestSqlite.ClearPools(dataDir);
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}

/// <summary>A <see cref="TimeProvider"/> whose timers fire only when the test advances it.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>The periods of the timers still running.</summary>
    public IReadOnlyList<TimeSpan> Periods
    {
        get { lock (_timers) return _timers.Where(t => !t.Disposed).Select(t => t.Period).ToList(); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(callback, state, _now + dueTime, period);
        lock (_timers) _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock on and fires every timer that came due (once each).</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;
        List<Timer> due;
        lock (_timers) due = _timers.Where(t => !t.Disposed && t.Due <= _now).ToList();
        foreach (var timer in due)
        {
            timer.Due = timer.Period > TimeSpan.Zero ? timer.Due + timer.Period : DateTimeOffset.MaxValue;
            timer.Callback(timer.State);
        }
    }

    private sealed class Timer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset Due { get; set; } = due;
        public TimeSpan Period { get; private set; } = period;
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Period = period;
            return !Disposed;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Drives a real <see cref="AgentSession"/> with a scripted provider and a counting invoker.</summary>
internal static class AgentRunDoubles
{
    public static AgentPrincipal KeyPrincipal(string keyId) =>
        new(AgentPrincipalKind.McpKey, "ci", McpPermissionLevels.Admin, null, McpKeyId: keyId);

    public static List<AgentStreamDelta> ToolTurn(params string[] callIds) =>
        callIds.Select(id => new AgentStreamDelta(ToolCallDelta: new AgentToolCall(id, "restart_container", "{}")))
            .Append(new AgentStreamDelta(Final: AgentStopReason.ToolCalls)).ToList();

    public static List<AgentStreamDelta> TextTurn(string text) =>
        [new AgentStreamDelta(TextDelta: text), new AgentStreamDelta(Final: AgentStopReason.Stop)];

    public static ScriptedProvider Provider(params List<AgentStreamDelta>[] turns) => new(turns);

    public static AgentSession Session(AgentPrincipal principal, ScriptedProvider provider, Invoker invoker,
        IAgentAuthority authority, IAgentGuardrailEngine? engine = null) =>
        new(new AgentContext("s1", principal, AgentOrigin.McpTool, GuardrailPolicy.SafeDefault()), provider,
            new NoTools(), invoker, engine ?? new AllowAll(), AgentToolTestHelpers.DefaultRegistry(),
            new AgentSettings { Model = "x", MaxToolIterations = 4 }, "system", authority: authority);

    public static async Task<List<AgentEvent>> CollectAsync(AgentSession session)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in session.SendAsync("go")) events.Add(e);
        return events;
    }

    internal sealed class ScriptedProvider(List<AgentStreamDelta>[] turns) : IAgentLlmProvider
    {
        private readonly Queue<List<AgentStreamDelta>> _turns = new(turns);
        public int Remaining => _turns.Count;
        public string Id => "scripted";

        public async IAsyncEnumerable<AgentStreamDelta> StreamAsync(AgentCompletionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var turn = _turns.Count > 0 ? _turns.Dequeue() : [new AgentStreamDelta(Final: AgentStopReason.Stop)];
            foreach (var d in turn) yield return d;
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>Counts calls, records the level each ran with, and runs <c>onCall(count)</c> inside each call.</summary>
    internal sealed class Invoker(Action<int> onCall) : IAgentToolInvoker
    {
        public int Calls { get; private set; }
        public List<string> Levels { get; } = [];

        public Task<AgentToolResult> InvokeAsync(AgentToolCall call, AgentContext context, CancellationToken ct = default)
        {
            Calls++;
            Levels.Add(context.Principal.PermissionLevel);
            onCall(Calls);
            return Task.FromResult(new AgentToolResult(call.Id, "OK", false,
                new GuardrailDecision(GuardrailVerdict.Allow, "", [])));
        }
    }

    private sealed class NoTools : IAgentToolCatalog
    {
        public IReadOnlyList<AgentToolDefinition> GetVisibleTools(AgentContext context) => [];
    }

    private sealed class AllowAll : IAgentGuardrailEngine
    {
        public GuardrailDecision Evaluate(GuardrailRequest request) => new(GuardrailVerdict.Allow, "ok", []);
    }
}

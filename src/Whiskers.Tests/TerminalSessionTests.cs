using Microsoft.Extensions.Logging.Abstractions;
using Whiskers.Configuration;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Terminal;
using Xunit;

namespace Whiskers.Tests;

public class TerminalSessionTests
{
    [Fact]
    public void Touch_AdvancesLastActivity()
    {
        // A stale session (opened 30 min ago) — the idle sweep would reap this.
        var session = TerminalDoubles.Session(DateTime.UtcNow.AddMinutes(-30));
        var stale = session.LastActivityAt;

        session.Touch();

        // Touch() (called on every output chunk) resets activity to ~now, so streaming output
        // keeps the session alive.
        Assert.True(session.LastActivityAt > stale);
        Assert.True(DateTime.UtcNow - session.LastActivityAt < TimeSpan.FromSeconds(5));
    }
}

/// <summary>Terminal doubles: a launcher that starts nothing, an audit that keeps its rows, a settable clock.</summary>
internal static class TerminalDoubles
{
    public static readonly TerminalOwner Admin = new("user:admin@example.com", "admin@example.com");

    public static TerminalSession Session(DateTime openedAt) =>
        new(TerminalSession.NewId(), Admin, new TerminalTarget("local", "c1", "c1"), new FakeProcess(), openedAt);

    public static readonly Whiskers.Services.Mutations.MutationPrincipal AdminPrincipal =
        new(Whiskers.Services.Mutations.MutationPrincipalKind.Human, Admin.Id, Admin.DisplayName,
            Whiskers.Models.McpPermissionLevels.Admin);

    /// <summary>Opens a container shell inside the gateway's decision for it, as the pages do.</summary>
    public static Task<TerminalSession> OpenContainer(ITerminalSessionManager manager, string sessionId, string? serverId,
        string containerId)
    {
        using var _ = Decided("terminal.container_open", sessionId, AdminPrincipal, "container", containerId, serverId ?? "local");
        return manager.OpenContainerShellAsync(new TerminalOpenRequest(sessionId, serverId, containerId));
    }

    /// <summary>Opens a host shell inside the gateway's decision for it, as the pages do.</summary>
    public static Task<TerminalSession> OpenHost(ITerminalSessionManager manager, string sessionId, string? serverId)
    {
        using var _ = Decided("terminal.host_open", sessionId, AdminPrincipal, "host", serverId ?? "local", serverId ?? "local");
        return manager.OpenHostShellAsync(new TerminalOpenRequest(sessionId, serverId));
    }

    /// <summary>The scope the gateway opens around a decided call on <paramref name="targetId"/>.</summary>
    public static Whiskers.Services.Mutations.MutationScope Decided(string capability, string sessionId,
        Whiskers.Services.Mutations.MutationPrincipal principal, string kind, string targetId, string serverId) =>
        Whiskers.Services.Mutations.MutationScope.Begin(capability, "op-" + sessionId, principal,
            new Whiskers.Services.Mutations.MutationRequest(capability, principal, Whiskers.Services.Mutations.MutationSurface.Ui,
                new Whiskers.Services.Mutations.MutationTarget(kind, targetId, targetId, serverId),
                new Dictionary<string, System.Text.Json.JsonElement>()));

    /// <summary>Lets every session go on.</summary>
    internal sealed class Permissive : ITerminalAuthority
    {
        public TerminalCloseReason? MustEnd(TerminalSession session) => null;
    }

    public static TerminalSessionManager Manager(FakeLauncher launcher, RecordingAudit audit,
        ITerminalAuthority? authority = null, ManualTime? time = null, TerminalSettings? settings = null,
        params ServerConfig[] servers) =>
        new(new StaticOptionsMonitor<TerminalSettings>(settings ?? new TerminalSettings()),
            new FakeServerConfig(servers.Length > 0 ? servers : [new ServerConfig { Id = "local", Name = "Local", IsDefault = true }]),
            launcher, audit, NullLogger<TerminalSessionManager>.Instance, authority ?? new Permissive(), time);

    internal sealed class FakeProcess : ITerminalProcess
    {
        public StreamReader Output { get; } = new(new MemoryStream());
        public StreamReader Errors { get; } = new(new MemoryStream());
        public bool HasExited { get; set; }
        public List<string> Typed { get; } = [];
        public int Disposed { get; private set; }

        public Task WriteAsync(string data)
        {
            Typed.Add(data);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed++;
            HasExited = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class FakeLauncher : ITerminalLauncher
    {
        public List<TerminalLaunch> Started { get; } = [];
        public List<FakeProcess> Processes { get; } = [];

        public ITerminalProcess Start(TerminalLaunch launch)
        {
            Started.Add(launch);
            var process = new FakeProcess();
            Processes.Add(process);
            return process;
        }
    }

    internal sealed record AuditRow(string Actor, string ActorType, string Action, string TargetType, string TargetId,
        string TargetName, string? Details, string? ServerId);

    internal sealed class RecordingAudit : IAuditLogService
    {
        public List<AuditRow> Rows { get; } = [];

        public Task LogAsync(string actor, string actorType, string action, string targetType, string targetId,
            string targetName, string? details = null, string? serverId = null, bool success = true)
        {
            lock (Rows) Rows.Add(new(actor, actorType, action, targetType, targetId, targetName, details, serverId));
            return Task.CompletedTask;
        }

        public Task<List<AuditLogEntity>> GetRecentAsync(int count = 100, int offset = 0, string? actionFilter = null,
            string? targetTypeFilter = null) => Task.FromResult(new List<AuditLogEntity>());

        public Task<int> GetTotalCountAsync(string? actionFilter = null, string? targetTypeFilter = null) => Task.FromResult(0);
    }

    internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

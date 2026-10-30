using System.Collections.Concurrent;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Whiskers.Components.Pages;
using Whiskers.Models;
using Whiskers.Services.AuditLog;
using Whiskers.Services.Auth;
using Whiskers.Services.Observability;
using Whiskers.Services.Docker.Budget;
using Whiskers.Services.LogMonitor;
using Whiskers.Services.LogMonitor.Hygiene;
using Whiskers.Services.Observability.SelfMetrics;
using Whiskers.Services.ServerConfig;

namespace Whiskers.Tests.ComponentTests;

/// <summary>R1-23 / INV-50: the Audit log and Agent History pages show audit data to Admins only — for any other role
/// they render the Admin-only notice and never ask the stores for a row.</summary>
public class AuditPagesAccessTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private static bool ShowsNotice(string markup) => markup.Contains("AdminOnly") || markup.Contains("administrators");

    private readonly Recording<IAuditLogService> _audit = Recording<IAuditLogService>.Create();
    private readonly Recording<IMcpCallLogStore> _calls = Recording<IMcpCallLogStore>.Create();
    private readonly Recording<IAuditChain> _chain = Recording<IAuditChain>.Create();

    private void Setup(bool admin)
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService>(ServersKubeconfigSaveTests.NullProxy<IKeyInterceptorService>.Create());
        Services.AddSingleton((IAuditLogService)(object)_audit);
        Services.AddSingleton((IMcpCallLogStore)(object)_calls);
        Services.AddSingleton((IAuditChain)(object)_chain);
        Services.AddSingleton<ICurrentUserService>(new User(admin ? AppRole.Admin : AppRole.Operator));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void An_operator_sees_the_notice_and_no_audit_row_is_read()
    {
        Setup(admin: false);

        var page = Render<AuditLog>();

        page.WaitForAssertion(() => Assert.True(ShowsNotice(page.Markup), page.Markup));
        Assert.Empty(_audit.Calls);
        Assert.Empty(_chain.Calls);
        Assert.Empty(page.FindAll("table"));
    }

    [Fact]
    public void An_operator_sees_the_notice_and_no_agent_call_is_read()
    {
        Setup(admin: false);

        var page = Render<AgentHistory>();

        page.WaitForAssertion(() => Assert.True(ShowsNotice(page.Markup), page.Markup));
        Assert.Empty(_calls.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_Self_status_timeline_reads_audit_rows_for_admins_only(bool admin)
    {
        Setup(admin);
        Services.AddSingleton((ISelfMetrics)(object)Recording<ISelfMetrics>.Create());
        Services.AddSingleton((IServerBudget)(object)Recording<IServerBudget>.Create());
        Services.AddSingleton((IServerCircuitBreaker)(object)Recording<IServerCircuitBreaker>.Create());
        Services.AddSingleton((ILoopSuspensionService)(object)Recording<ILoopSuspensionService>.Create());
        Services.AddSingleton((ILogMonitorService)(object)Recording<ILogMonitorService>.Create());
        Services.AddSingleton((ILogScanExclusions)(object)Recording<ILogScanExclusions>.Create());
        Services.AddSingleton((IServerConfigService)(object)Recording<IServerConfigService>.Create());
        var alerts = Recording<Whiskers.Services.Notifications.IInAppNotificationStore>.Create();
        Services.AddSingleton((Whiskers.Services.Notifications.IInAppNotificationStore)(object)alerts);
        Services.AddSingleton((Whiskers.Services.Observability.Outcomes.IActionOutcomeService)(object)
            Recording<Whiskers.Services.Observability.Outcomes.IActionOutcomeService>.Create());

        var page = Render<SelfStatus>();

        if (admin) page.WaitForAssertion(() => Assert.Contains(nameof(IAuditLogService.GetRecentAsync), _audit.Calls));
        else
        {
            // The timeline was read (its alerts), just without the audit rows.
            page.WaitForAssertion(() => Assert.Contains("QueryAsync", alerts.Calls));
            Assert.DoesNotContain(nameof(IAuditLogService.GetRecentAsync), _audit.Calls);
        }
    }

    [Fact]
    public void An_admin_gets_the_rows()
    {
        Setup(admin: true);

        var audit = Render<AuditLog>();
        var history = Render<AgentHistory>();

        audit.WaitForAssertion(() => Assert.Contains(nameof(IAuditLogService.GetRecentAsync), _audit.Calls));
        history.WaitForAssertion(() => Assert.NotEmpty(_calls.Calls));
        Assert.False(ShowsNotice(audit.Markup));
    }

    private sealed class User(AppRole role) : ICurrentUserService
    {
        public Task<string?> GetEmailAsync() => Task.FromResult<string?>("someone@local.test");
        public Task<AppRole> GetRoleAsync() => Task.FromResult(role);
        public Task<bool> HasRoleAsync(AppRole required) => Task.FromResult(role >= required);
    }

    /// <summary>Records every call; answers with empty collections and default values.</summary>
    public class Recording<T> : DispatchProxy where T : class
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public static Recording<T> Create() => (Recording<T>)(object)DispatchProxy.Create<T, Recording<T>>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Enqueue(method!.Name);
            var type = method.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [Empty(inner)]);
            }
            return Empty(type);
        }

        private static object? Empty(Type type)
        {
            if (type.IsValueType) return Activator.CreateInstance(type);
            if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
            if (!type.IsGenericType) return null;
            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();
            if (definition == typeof(List<>) || definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>))
                return Activator.CreateInstance(type);
            if (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>))
                return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments));
            if (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(IList<>) || definition == typeof(ICollection<>))
                return Array.CreateInstance(arguments[0], 0);
            if (definition == typeof(IReadOnlySet<>) || definition == typeof(ISet<>))
                return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(arguments));
            return null;
        }
    }
}

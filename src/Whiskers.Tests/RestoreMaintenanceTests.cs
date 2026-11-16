using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Whiskers.Mcp;
using Whiskers.Services.Maintenance;
using Whiskers.Services.Webhooks;

namespace Whiskers.Tests;

/// <summary>While a restore is staged (maintenance), the machine entry points that pass the maintenance page — MCP and
/// the inbound webhooks — refuse instead of running: what they would audit would land in the database the restore
/// replaces, after the pre-restore safety backup (review 1.0.0, Data). The scheduler's half is in
/// <see cref="MutationShutdownTests"/>.</summary>
public sealed class RestoreMaintenanceTests
{
    private static IServiceProvider Services(bool maintenance, Action<IServiceCollection>? more = null)
    {
        var state = new MaintenanceStateService();
        if (maintenance) state.EnterMaintenance("test restore");
        var services = new ServiceCollection().AddSingleton<IMaintenanceStateService>(state);
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task An_mcp_tool_call_during_a_restore_is_refused_before_anything_runs()
    {
        var ran = false;
        var result = await McpGuardrailFilter.HandleAsync(Services(maintenance: true),
            new CallToolRequestParams { Name = "restart_container" }, "1",
            _ => { ran = true; return ValueTask.FromResult(new CallToolResult()); }, CancellationToken.None);

        Assert.False(ran);
        Assert.True(result.IsError);
        Assert.Contains("restore is in progress", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task A_webhook_delivery_during_a_restore_is_refused_with_retry_after_and_not_read()
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = Services(maintenance: true, s => s.AddSingleton<IWebhookService>(new NoopWebhookService())),
        };
        ctx.Request.Body = new MemoryStream("{}"u8.ToArray());

        var result = await WebhookEndpoint.HandleAsync("wh-1", ctx);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (result as IStatusCodeHttpResult)?.StatusCode);
        Assert.Contains("maintenance", (result as IValueHttpResult)?.Value?.ToString());
        Assert.Equal("30", ctx.Response.Headers.RetryAfter.ToString());
        Assert.Equal(0, ctx.Request.Body.Position);          // the body was not read
    }
}

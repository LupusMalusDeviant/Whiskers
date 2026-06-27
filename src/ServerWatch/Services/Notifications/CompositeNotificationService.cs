using Microsoft.Extensions.DependencyInjection;
using ServerWatch.Models;
using ServerWatch.Services.Agent.Triggers;

namespace ServerWatch.Services.Notifications;

/// <summary>
/// Delegates notifications to all configured providers (Mattermost, Matrix, etc.).
/// Each provider handles its own enabled/disabled check independently. Also feeds every
/// event to the AI-trigger dispatcher (resolved lazily to avoid a DI cycle).
/// </summary>
public class CompositeNotificationService : INotificationService
{
    private readonly IMattermostNotificationService _mattermost;
    private readonly IMatrixNotificationService _matrix;
    private readonly IServiceProvider _sp;
    private readonly ILogger<CompositeNotificationService> _logger;

    public CompositeNotificationService(
        IMattermostNotificationService mattermost,
        IMatrixNotificationService matrix,
        IServiceProvider sp,
        ILogger<CompositeNotificationService> logger)
    {
        _mattermost = mattermost;
        _matrix = matrix;
        _sp = sp;
        _logger = logger;
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var tasks = new List<Task>();

        tasks.Add(SafeSend("Mattermost", () => _mattermost.SendAsync(evt)));
        tasks.Add(SafeSend("Matrix", () => _matrix.SendAsync(evt)));
        tasks.Add(SafeSend("AI-Trigger", () => _sp.GetRequiredService<IAiTriggerDispatcher>().OnEventAsync(evt)));

        await Task.WhenAll(tasks);
    }

    public async Task SendTestAsync()
    {
        // Send test to all enabled providers
        var errors = new List<string>();

        try { await _mattermost.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Mattermost: {ex.Message}"); }

        try { await _matrix.SendTestAsync(); }
        catch (Exception ex) { errors.Add($"Matrix: {ex.Message}"); }

        if (errors.Count > 0)
            throw new AggregateException($"Some providers failed: {string.Join("; ", errors)}");
    }

    private async Task SafeSend(string provider, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification provider {Provider} failed", provider);
        }
    }
}

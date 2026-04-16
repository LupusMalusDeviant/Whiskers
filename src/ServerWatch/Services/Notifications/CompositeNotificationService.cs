using ServerWatch.Models;

namespace ServerWatch.Services.Notifications;

/// <summary>
/// Delegates notifications to all configured providers (Mattermost, Matrix, etc.).
/// Each provider handles its own enabled/disabled check independently.
/// </summary>
public class CompositeNotificationService : INotificationService
{
    private readonly MattermostNotificationService _mattermost;
    private readonly MatrixNotificationService _matrix;
    private readonly ILogger<CompositeNotificationService> _logger;

    public CompositeNotificationService(
        MattermostNotificationService mattermost,
        MatrixNotificationService matrix,
        ILogger<CompositeNotificationService> logger)
    {
        _mattermost = mattermost;
        _matrix = matrix;
        _logger = logger;
    }

    public async Task SendAsync(NotificationEvent evt)
    {
        var tasks = new List<Task>();

        tasks.Add(SafeSend("Mattermost", () => _mattermost.SendAsync(evt)));
        tasks.Add(SafeSend("Matrix", () => _matrix.SendAsync(evt)));

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

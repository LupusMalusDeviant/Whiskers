using Microsoft.Extensions.Logging;
using Whiskers.Utils;

namespace Whiskers.Components.Shared;

/// <summary>
/// The last catch of a page handler (review 1.0.0, U1): an exception that leaves a Blazor event handler ends the
/// circuit, and the app has no ErrorBoundary. A handler that catches it hands it here, shows the returned text and
/// stays usable. The message is redacted (<see cref="SinkRedaction.Text"/>) before it is logged or shown; the
/// exception object itself is never logged, so no inner message or stack text carries a secret into the log.
/// </summary>
public static class UiFailure
{
    /// <summary>Logs the failure of <paramref name="action"/> without secrets and returns the redacted message for the
    /// snackbar.</summary>
    public static string Report(ILogger logger, string action, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ex);
        var message = SinkRedaction.Text(ex.Message) ?? "";
        logger.LogWarning("UI action {Action} failed: {ErrorType}: {Error}", action, ex.GetType().Name, message);
        return message;
    }
}

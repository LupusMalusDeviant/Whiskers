using System.Diagnostics;

namespace Whiskers.Services.Observability.Correlation;

/// <summary>
/// Gives every HTTP request its correlation id (R1-53, see <see cref="RequestCorrelation"/>): chosen before anything
/// else runs, echoed as <c>X-Request-Id</c> (also on an error page, which clears the headers it found) and opened as
/// the log scope <c>HttpRequestId</c>. It reads one header and the current activity only — never the path, the query,
/// the body or any other header — and it decides nothing: it is registered first in the pipeline, ahead of the
/// forwarded headers and the authentication chain, whose order it does not change.
/// </summary>
public sealed class RequestCorrelationMiddleware(RequestDelegate next, ILogger<RequestCorrelationMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<RequestCorrelationMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var id = RequestCorrelation.Choose(context.Request.Headers[RequestCorrelation.HeaderName], Activity.Current);
        context.Response.Headers[RequestCorrelation.HeaderName] = id;
        // An exception handler clears the response headers before it renders the error page: set it again on start.
        context.Response.OnStarting(static state =>
        {
            var (response, value) = ((HttpResponse, string))state;
            response.Headers[RequestCorrelation.HeaderName] = value;
            return Task.CompletedTask;
        }, (context.Response, id));

        using var ambient = RequestCorrelation.Enter(id);
        // Through the logger factory, so the redaction (R1-25) sees the scope like every other value.
        using var scope = _logger.BeginScope("Request {HttpRequestId}", id);
        await _next(context);
    }
}

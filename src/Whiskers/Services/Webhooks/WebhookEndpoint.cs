using Microsoft.Extensions.Options;

namespace Whiskers.Services.Webhooks;

/// <summary>
/// The inbound endpoint <c>POST /api/webhooks/{webhookId}</c> (R1-10, INV-42), mapped in the pipeline (Core) and
/// anonymous: the provider's HMAC authenticates a delivery. It reads at most <see cref="WebhookOptions.MaxBodyBytes"/>
/// with the request's cancellation, hands the delivery to <see cref="IWebhookService.AcceptAsync"/> and answers at
/// once — the run itself happens later in a bounded queue:
/// <list type="bullet">
/// <item>202 <c>{"status":"queued","runId":…}</c> — accepted;</item>
/// <item>200 <c>{"status":"duplicate","runId":…,"state":…}</c> — delivered before; the first run's id and state;</item>
/// <item>400 <c>{"status":"rejected"}</c> — any invalid request, without saying which check failed;</item>
/// <item>429 / 503 with <c>Retry-After</c> — too many requests, or the queue is full.</item>
/// </list>
/// </summary>
public static class WebhookEndpoint
{
    public static async Task<IResult> HandleAsync(string webhookId, HttpContext ctx)
    {
        var options = ctx.RequestServices.GetService<IOptions<WebhookOptions>>()?.Value ?? new WebhookOptions();
        var body = await ReadBoundedAsync(ctx.Request, options.MaxBodyBytes, ctx.RequestAborted);
        if (body is null) return Rejected();

        var webhooks = ctx.RequestServices.GetRequiredService<IWebhookService>();
        var headers = ctx.Request.Headers;
        var reply = await webhooks.AcceptAsync(webhookId, new WebhookRequest(body,
            GitHubSignature: headers["X-Hub-Signature-256"].FirstOrDefault(),
            WhiskersSignature: headers["X-Whiskers-Signature"].FirstOrDefault(),
            WhiskersTimestamp: headers["X-Whiskers-Timestamp"].FirstOrDefault(),
            WhiskersDelivery: headers["X-Whiskers-Delivery"].FirstOrDefault(),
            SourceIp: ctx.Connection.RemoteIpAddress?.ToString()), ctx.RequestAborted);

        switch (reply.Reply)
        {
            case WebhookReply.Accepted:
                return Results.Json(new { status = "queued", runId = reply.RunId }, statusCode: StatusCodes.Status202Accepted);
            case WebhookReply.Duplicate:
                return Results.Ok(new { status = "duplicate", runId = reply.RunId, state = reply.State });
            case WebhookReply.RateLimited:
                ctx.Response.Headers.RetryAfter = "60";
                return Results.Json(new { status = "rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests);
            case WebhookReply.Busy:
                ctx.Response.Headers.RetryAfter = "30";
                return Results.Json(new { status = "busy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            default:
                return Rejected();
        }
    }

    private static IResult Rejected() => Results.BadRequest(new { status = "rejected" });

    /// <summary>The body, or <c>null</c> when it is larger than <paramref name="max"/> bytes (then it is not read on).</summary>
    internal static async Task<byte[]?> ReadBoundedAsync(HttpRequest request, int max, CancellationToken ct)
    {
        if (request.ContentLength > max) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

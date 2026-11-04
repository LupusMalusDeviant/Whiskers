# Services/Observability/Correlation

The request correlation id (R1-53) that ties log lines, audit events and gateway decisions of one HTTP request
together.

| File | What |
|---|---|
| `RequestCorrelation.cs` | The id itself: the `X-Request-Id` header name, `IsAcceptable` for an incoming value, `Choose` (client value or the activity's id) and the ambient `Current` id with `Enter`. |
| `RequestCorrelationMiddleware.cs` | Registered first in the pipeline, ahead of forwarded headers and authentication, whose order it does not change. Picks the id, echoes it as `X-Request-Id` (also on error pages) and opens the log scope `HttpRequestId`. Reads one header and the current activity only and decides nothing. |

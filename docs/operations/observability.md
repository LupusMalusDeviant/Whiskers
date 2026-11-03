# Logs, correlation and alerts

This guide covers where Whiskers writes its logs, how to switch them to structured JSON, how to follow one request
from the client to the audit row, and which signals to alert on when Whiskers itself stops working. A single-instance
installation needs no tracing backend for any of it.

## Where the logs are

Whiskers writes its logs to the console (standard output) of its process and nowhere else. It writes no log files.

| Installation | Read the log with |
|---|---|
| Docker Compose | `docker compose logs -f serverwatch` (the service keeps its pre-rebrand name), or `docker logs -f <container>` |
| Helm / Kubernetes | `kubectl logs -f deploy/<release name of the chart>` |
| `dotnet Whiskers.dll` | the terminal or the service manager (`journalctl -u <unit>`) |

Retention and rotation belong to the container runtime (the Docker `json-file` driver's `max-size`/`max-file`, or the
cluster's log collector). The records Whiskers keeps on its own are in its database, not in the log:

| Record | Where |
|---|---|
| Audit trail (every mutation: intent, outcome, decision, approval) | **Audit log** page; `/audit/export` (Admin) holds every column, the operation id included |
| Approvals | **Approvals** page |
| MCP and agent calls | **Agent history** page |
| Whether Whiskers' own loops are working | **Self status** page (`/self-status`), `whiskers_self_*` on `/metrics` |

Every log line passes the log redaction first (R1-25): secrets, credentials, capability URLs and values logged under a
secret's name are written as `***`, whatever the format.

## Structured (JSON) logs

The console uses the plain text format by default. JSON is switched on with the standard .NET logging settings, as
environment variables:

```bash
Logging__Console__FormatterName=json
Logging__Console__FormatterOptions__IncludeScopes=true      # the correlation ids below are scopes
Logging__Console__FormatterOptions__UseUtcTimestamp=true    # optional
Logging__Console__FormatterOptions__TimestampFormat=yyyy-MM-ddTHH:mm:ss.fffZ   # optional
```

In Compose put them under `environment:`; in Helm under `extraEnv`. Each log entry is then one JSON object per line
with `Timestamp`, `LogLevel`, `Category`, `Message`, `State` (the named values of the message, redacted) and `Scopes`
(the correlation ids, redacted the same way). The text format shows the scopes too with
`Logging__Console__FormatterOptions__IncludeScopes=true` alone.

With scopes switched on, ASP.NET Core adds its own request scope as well (its connection-based `RequestId` and the
request path, never the query string).

Log levels are set the same way, for example `Logging__LogLevel__Default=Warning` or
`Logging__LogLevel__Whiskers.Services.Mutations=Information`. The outbound HTTP client log
(`System.Net.Http.HttpClient`) stays at `Warning` unless a more specific rule switches it on: its request lines can
carry a channel's capability URL, which the redaction then has to catch.

## Following one request

| Id | Where it comes from | Where it appears |
|---|---|---|
| `HttpRequestId` | The client's `X-Request-Id` header when it is safe (1 to 64 characters of `A-Z a-z 0-9 . _ -`); otherwise the W3C trace id of an incoming `traceparent`; otherwise a new id | The `X-Request-Id` response header; the log scope `HttpRequestId`; the gateway's decision and outcome lines |
| `OperationId` | The Mutation Gateway: the caller's own id (an agent tool call) or a new one; an approve-then-retry continues the operation that raised the approval | The log scope `OperationId` around the whole execution, also in the lines of the services it calls; the policy decision, the approval (its correlation id), the operation journal and the audit row |

To follow a request:

1. Take the `X-Request-Id` the client received, or send your own.
2. Find the gateway line that names it:
   `Operation 5f0c... (container.restart, Mcp) decided Confirm: decision 91ab..., request req-42`.
3. Follow the `OperationId`. With scopes switched on, every line of the run carries it: the gateway's
   `... not run: PendingApproval, approval 7d2e...` and
   `... Executed: verification Verified, approval 7d2e..., audit 1234, request req-43` lines, and the lines of the
   services the execution called.
4. The audit row with that `OperationId` (or the `audit` number of the outcome line) holds the decision id, the
   approval, who approved it, the outcome and the verification.

A call that waited for an approval and was then retried shows two requests (the ask and the approved retry) under one
operation id. A Blazor page action runs on the browser's connection, so its request id names that connection rather
than the single click. Background work (the scheduler, AutoUpdate, AutoPatch) has no request: its lines say
`request none` and are followed by the operation id alone.

Whiskers never logs request headers, query strings or bodies for this; it reads the one header and echoes only a value
it accepted. An id it did not accept is replaced, not repeated. Whether an id is accepted never depends on
Whiskers' secrets; if a secret is sent as the id, it goes back only to its sender, and every log line writes it as
`***`, like any other value the log redaction catches.

## Signals to alert on

Scrape `/metrics` with the scrape token (`Metrics__ScrapeToken`); the `whiskers_self_*` series are computed in memory
and are still served when the fleet does not answer.

| Signal | What it means | Watch | Act |
|---|---|---|---|
| **Loop stall** | A background loop has not completed a cycle for a server | `whiskers_self_loop_last_success_age_seconds` well above the loop's interval (`-1`: never completed since the start); notification `monitoring_stalled` (after three intervals); the **Self status** page | Open **Self status**: a skipped server says why. A circuit that is open (`whiskers_self_circuit_open`) or a paused server (`whiskers_loops_paused`) explains it; otherwise read the loop's warnings in the log |
| **Operation incomplete** | An operation's outcome is not on record: it was running when Whiskers stopped, or its outcome could not be written | `whiskers_self_audit_intents_unsettled` above 0 for more than a few minutes; at start the warning `Whiskers stopped while N operation(s) ran; marked as interrupted` and the notification `operations_interrupted`; a `Failed` outcome line whose audit reason says *its effect is unknown* | Check the named targets by hand before retrying: Whiskers never replays an interrupted operation, and a retry under the same operation id is refused |
| **Audit failure** | An audit event could not be written | `whiskers_self_audit_events_pending` above 0 for more than a few minutes, `whiskers_self_audit_legacy_write_failed` = 1; `/readyz` answers `Degraded`; error lines `Audit intent for ... could not be written; operation ... refused` | Check the database (disk space, the PostgreSQL connection). While no intent can be written every mutation is refused; kept outcomes are written again once a minute and the gauges fall back to 0 |
| Budget saturation | Docker calls wait for a slot | `whiskers_self_call_wait_seconds_max` rising, `whiskers_self_calls_in_flight` at the lane limit | Usually a slow host; see the loop's timeouts |
| Notification delivery | A channel fails critical notifications | `whiskers_self_notification_outbox_pending` staying up, `whiskers_self_notification_outbox_total{result="exhausted"}` or `{result="dropped"}` growing | Check the channel's settings and its warnings in the log |

Example Prometheus rules (adjust the thresholds to your intervals):

```yaml
groups:
  - name: whiskers-self
    rules:
      - alert: WhiskersLoopStalled
        expr: whiskers_self_loop_last_success_age_seconds > 1800
        for: 10m
      - alert: WhiskersOperationOutcomeMissing
        expr: whiskers_self_audit_intents_unsettled > 0
        for: 10m
      - alert: WhiskersAuditWriteFailing
        expr: whiskers_self_audit_events_pending > 0 or whiskers_self_audit_legacy_write_failed == 1
        for: 5m
      - alert: WhiskersCircuitOpen
        expr: whiskers_self_circuit_open == 1
        for: 15m
```

`whiskers_self_audit_intents_unsettled` is 1 right after a start until the first recovery pass has looked (unknown
is not complete); the `for:` clause covers that minute.

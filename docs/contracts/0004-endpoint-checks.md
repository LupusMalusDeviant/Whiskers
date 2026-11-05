# Contract 0004 — Endpoint checks from the managed host (R1-57)

Status: **Accepted** · 5 November 2026 by the maintainer, including the decisions listed at the end (all six as
recommended). To be implemented in R1-57. Addendum A (signatures as built, A1–A7) accepted 5 November 2026.

## Purpose

A managed host checks whether a service it can reach answers: HTTP status and latency, TCP connect, TLS certificate
expiry. Each check runs **on the host** it is bound to, through the existing host command executor, from that host's
side of the network. Whiskers decides which addresses may be contacted and never opens a connection to the target
itself (WORK_PACKAGES R1-57; INV-04, INV-41, INV-60; R1-11, R1-18, R1-22, R1-41, R1-54, R1-56).

## Current state (verified at `0611f336`)

- `IHostCommandExecutor` (`Services/Server/IHostCommandExecutor.cs`):
  - `ExecuteAsync(string serverId, string command, TimeSpan? timeout = null, CancellationToken ct = default,
    int? maxOutputChars = null)` for reads.
  - `[MutationPrimitive] ExecuteMutationAsync(...)` with the same parameters, which only runs inside a gateway decision.
  - Both return `CommandResult` (`ExitCode`, `Output`, `Error`, `OutputTruncated`).
  - A Kubernetes server without `SshHost` returns exit −1 ("No shell transport").
  - The executor-deadline prerequisite is merged (#140): every transport ends at most timeout + 5 s after the call. The
    mTLS path has no output cap.
- Validators: `CommandOperands.IsSafeHost` / `IsValidPort` (R1-42), `ShellUtils.Quote`.
  - `OutboundAddressPolicy.DeniedRange(IPAddress)`, `Canonicalize` and `TryParseLiteral` hold the R1-54 ranges.
  - `OutboundException` (literal or name + allowed addresses, port) with `OutboundExceptionList.Allows(host, port,
    address)` holds the exceptions.
  - `IOutboundTargetGuard.CheckAsync` resolves inside Whiskers' process, so it is **not** usable here: the resolution
    happens on the host.
- Automation: `IAutomationMutationRunner.RunAsync<T>(AutomationActor, capabilityId, arguments, action, ct)` re-reads the
  owner's level on every run (INV-04). Kill switch, audit intent and operation id are enforced by the `MutationGateway`.
  - `LostAuthority(actor, capabilityId)` names an owner who lost authority. `SchedulerService` / `TaskExecutor` is the
    model; an owner without authority suspends the item.
- Alerting: `BreachTracker.Consider(atUtc, serverId, serverName, kind, value, threshold, describeBreach,
  describeRecovery, BreachRules?)` with elapsed-time durations (R1-56). It persists across restarts (`AlertBreachStore`).
  - A kind must be listed in `NotificationEventTypes.RecoverableKinds` and classified as suppressible or not.
- Loops derive from `FleetBackgroundService` (R1-41: background lane, startup offset, per-cycle failure isolation).
  Intervals go through `IntervalValidation`.
- Data: typed entities in `Whiskers.Data`, paired SQLite/PostgreSQL migrations (ADR-0004).
- There is no server detail page. Per-server tools live at `/firewall/{ServerId}`, `/ssl/{ServerId}` and similar.

## Boundaries (fixed)

- **The host transport is not changed** (protected mTLS layer). The checks are ordinary commands handed to the executor.
- **No new agent, no listening port, no connection from Whiskers to the target.**
- **The certificate monitor (`Services/Certificates/`) is not touched.**
- **Every run is a privileged execution:** `ExecuteMutationAsync` inside a gateway capability, never `ExecuteAsync`.

## Types and interfaces

### Definition (entity `EndpointCheckEntity`, table `EndpointChecks`)

| Field | Type | Rule |
|---|---|---|
| `Id` | `string` (GUID) | key |
| `ServerId` | `string` | an existing server with a shell transport; otherwise refused on save |
| `Name` | `string` | 1–80 characters, display only, never in a command |
| `Kind` | `EndpointCheckKind` | `Http`, `Tcp`, `TlsExpiry` |
| `Target` | `string` | HTTP: an absolute `http`/`https` URL. TCP/TLS: a host name (RFC 1123, `IsSafeHost`) or a canonical IP literal (dotted quad or RFC 5952; decimal, octal, hex and bracketless zone forms refused) |
| `Port` | `int?` | TCP/TLS: required, 1–65535. HTTP: optional; if set it must equal the URL's effective port (80/443 if the URL names none), otherwise refused |
| `ExpectedStatus` | `int?` | HTTP only, 100–599. Default: 2xx. A 3xx is an answer, not followed (no `-L`) |
| `TlsWarnDays` | `int?` | TLS only, 1–365 (default 14). Fewer days left = failed |
| `IntervalSeconds` | `int` | ≥ 60 (validated), ≤ 86 400 |
| `TimeoutSeconds` | `int` | 1–30 (default 10). One attempt; the whole run has one deadline: `timeout × 5 + 10 s` |
| `Exceptions` | owned list | Admin-only internal-target exceptions in R1-54's shape (literal + port, or name + port + allowed addresses) |
| `Enabled` | `bool` | |
| `OwnerId` | `string` | `PrincipalIds.User(email)` of the Admin who saved it last |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` | |

### Result (entity `EndpointCheckResultEntity`, table `EndpointCheckResults`)

- `Id`, `CheckId`, `AtUtc`, `OperationId` (R1-18).
- `Outcome`: `Passed`, `Failed` or `Refused`. Refused means a policy refusal before anything ran.
- `Detail`: one bounded line, at most 300 characters, with secrets redacted.
- `LatencyMs`, `HttpStatus`, `TlsNotAfterUtc`.
- `AnsweredAddress`, `AddressesTried`, `AddressesNotTried`.

Retention is per check and time-based (see decision 4).

### Services

```csharp
public enum EndpointCheckKind { Http, Tcp, TlsExpiry }
public enum EndpointCheckOutcome { Passed, Failed, Refused }

public interface IEndpointCheckStore            // definitions; every write is [MutatingOperation]
{
    IReadOnlyList<EndpointCheckDefinition> List(string? serverId = null);
    EndpointCheckDefinition? Get(string id);
    [MutatingOperation("endpointcheck.save")]   Task<EndpointCheckDefinition> SaveAsync(EndpointCheckDraft draft, string editorId, CancellationToken ct); // A1
    [MutatingOperation("endpointcheck.delete")] Task DeleteAsync(string id, CancellationToken ct);
}

public static class EndpointCheckValidator      // pure; used on save AND again before every run
{
    public static IReadOnlyList<string> Validate(EndpointCheckDraft draft, ServerConfig? server);
}

public static class EndpointCheckCommands       // pure: fixed templates, operands single-quoted
{
    public static string Resolve(string name);                                     // getent ahosts '<name>'
    public static string Http(string url, string host, IPAddress address, int port, int timeoutSeconds); // A2
    public static string Tcp(IPAddress address, int port, int timeoutSeconds);
    public static string Tls(string serverName, IPAddress address, int port);
    public const int OutputCap = 16_384;                                           // head -c bound, in every template
}

public static class EndpointCheckParser         // pure; requires positive evidence
{
    public static ResolveAnswer ParseResolve(string? output);  // complete + well-formed, or Unreadable (A5)
    public static ProbeResult ParseHttp(string? output, int? expectedStatus = null); // "__rc=0" AND a 3-digit status (A4)
    public static ProbeResult ParseTcp(string? output);        // "__rc=0"
    public static ProbeResult ParseTls(string? output, DateTime nowUtc, int warnDays); // s_client rc 0 AND notAfter=
}

public sealed record ResolvedAddress(IPAddress Answered, IPAddress Canonical);              // A5
public sealed record ResolveAnswer(IReadOnlyList<ResolvedAddress> Entries, string? Unreadable);

public interface IEndpointCheckRunner           // one run of one check, through the gateway
{
    Task<EndpointCheckRun> RunAsync(EndpointCheckDefinition check, CancellationToken ct);
    Task<EndpointCheckRun> RunInDecisionAsync(string checkId, CancellationToken ct);         // A3
}

public interface IEndpointCheckSlots            // decision 5, shared by the monitor and "Run now" (A6)
{
    bool TryAcquire(string checkId, string serverId);   // false: check running, server busy, or 4 in flight
    void Release(string checkId);
    IReadOnlyDictionary<string, string> Running { get; }
}
```

`EndpointCheckMonitor : FleetBackgroundService` runs the checks.

### Run

Each run follows R1-57's three steps, inside **one** `IAutomationMutationRunner.RunAsync(actor, "endpointcheck.run",
new { serverId, checkId }, ...)` (A7). That makes it one gateway event with its own operation id.

1. Re-validate the definition against the current server. A failure is `Refused` and nothing runs.
2. For a name, run `getent ahosts '<name>'` on the host. An IP literal skips this step.
3. Parse the **complete** answer. An empty, truncated or unreadable answer is `Failed`.
   - Every unique address must pass `OutboundAddressPolicy.DeniedRange` or one of the check's exceptions. Otherwise the
     outcome is `Refused`, naming the address and the range.
4. Try at most the first **4** unique allowed addresses, in answer order, until one answers. Each try uses the probe
   template pinned to that address.
5. Record the answering address, or that none answered, plus the number of addresses not tried.

Bounds per run:

- **Executor calls:** at most 1 + 4.
- **Each attempt:** limited by `TimeoutSeconds`.
- **Whole run:** limited by one deadline.
- **Output:** limited by `head -c 16384` inside the template, with the exit marker `__rc=` inside the bounded stream.

### Templates (fixed; `<…>` = single-quoted, pre-validated operand)

```sh
# HTTP
( curl -q -s -S -o /dev/null -w '%{http_code} %{time_total}' --proto '=http,https' --max-time <t> \
  --resolve '<name>:<port>:<ip>' --noproxy '*' '<url>' 2>&1; echo "__rc=$?" ) | head -c 16384
# TCP (OpenBSD netcat)
( nc -z -w <t> '<ip>' '<port>' 2>&1; echo "__rc=$?" ) | head -c 16384
# TLS expiry: s_client output and its own status; the certificate is parsed in Whiskers (notAfter only)
( openssl s_client -connect '<ip>:<port>' -servername '<name>' -showcerts </dev/null 2>&1; echo "__rc=$?" ) | head -c 16384
```

- IPv6 addresses are bracketed where the tool needs it: `'<name>:<port>:[<ip>]'` and `'[<ip>]:<port>'`.
- A missing tool, a non-zero marker, a missing marker or unparsable output is `Failed`, never `Passed`.
- A TCP check on a host whose `nc` is not OpenBSD netcat reports "unsupported nc variant". It is not guessed at.

### Capabilities (CapabilityCatalog + VerificationContracts)

| Id | Level | Target | Risk | Deadline | Caller |
|---|---|---|---|---|---|
| `endpointcheck.save` | Admin | `server` | Reversible | Short | UI (definition create/edit, incl. exceptions) |
| `endpointcheck.delete` | Admin | `server` | Reversible | Short | UI |
| `endpointcheck.run` | Admin | `server` | RootEquivalent (it runs on the host as `host.exec` does) | Normal | the monitor only, as the check's owner; also "Run now" in the UI |

- The kill switch refuses all three.
- An audit failure refuses before anything is written or run (R1-22).
- Viewer, Operator and a demoted Admin are refused on save and delete.
- An owner who is no longer Admin **suspends** the check (`Enabled = false`, with the reason shown) instead of running it
  (INV-04, as `ScheduledTaskSuspender` does).

### Alerting (R1-56)

- New recoverable kind `endpoint_check_failed`, classified as suppressible.
- It is fed to `BreachTracker.Consider` with value 1 for a failed or refused run and 0 for a passed one, against
  threshold 1.
- Default rules: open after the check has failed for **5 minutes** (but at least two consecutive runs). Clear after it
  has passed for 5 minutes.
- A TLS check that has fewer days left than `TlsWarnDays` counts as failed.

### Loop (R1-41)

`EndpointCheckMonitor : FleetBackgroundService`:

- **Tick:** every 15 s. It runs the checks that are due, at most **4** at a time across the fleet and at most **1** per
  server.
- **Failures:** a failing run is logged and recorded and never ends the loop.
- **Audit volume:** the 60 s minimum interval bounds it to at most 1 440 audit rows per check per day (retention R1-23).

### UI and read access

- **New page `/checks`:** a fleet-wide list with filters by server, last outcome, latency and expiry. All signed-in roles
  can read it.
- **For Admins only:** create, edit, delete, "Run now" and exceptions.
- **Dashboard:** each server's expanded panel shows a link with the count of failing checks.
- **No new MCP write tool.** A Read-level MCP tool `endpoint_checks_list` returns definitions without exceptions'
  addresses, plus the last result. It is optional; see decision 6.

### Documentation

- `docs/operations/endpoint-checks.md` lists the required tools per host: `getent`, `curl`, `openssl` and OpenBSD
  `nc`. It also covers how to install them on Debian/Ubuntu/Alpine, the exceptions and the bounds.
- The page also explains that every run appears in the audit trail.

## Tests (acceptance)

| Area | Test (class `EndpointCheckTests` unless noted) |
|---|---|
| Templates | each kind's command string. Operands quoted; `-…`, metacharacters, quotes and whitespace refused **before** anything runs; `file:`, `gopher:`, `dict:`, `ftp:` refused on save and absent from the template (`--proto`) |
| Port | URL with and without an explicit port; a disagreeing definition port refused |
| Literals | canonical IPv4/IPv6 accepted; decimal, octal and hex refused; IPv6 bracketed in curl and s_client |
| Resolve | complete answer; truncated or unreadable → Failed; a denied 5th answer refuses; 5 allowed answers → 4 tried, 1 not tried; repeated addresses de-duplicated; a name answering `169.254.169.254`, an internal address, IPv4-mapped forms |
| Exceptions | an internal target is allowed only by a matching exception; exceptions are Admin-only |
| Parsers | curl exit 28/6/7 → Failed; a 3xx without a matching expected status → Failed; openssl connect failure; certificate printed followed by `__rc=1` → Failed; missing tool (`127`) → Failed |
| Bounds | a large response through the TCP transport is cut at the cap; every attempt and the whole run end within their deadlines (fake executor stalling) |
| Gateway (`EndpointCheckGatingTests`) | save and delete refused for Viewer, Operator and a demoted Admin, by the kill switch and by audit failure (nothing published); a run is one audited event with an operation id; an owner who lost Admin suspends the check; a Kubernetes server without SSH is refused on save |
| Loop | interval minimum 60 s; at most 4 concurrent, 1 per server; a failing run does not stop the loop |
| Alerting | sustained failure opens `endpoint_check_failed` after 5 min, recovery clears it; a refused run counts as failed |
| Migrations | paired SQLite/PostgreSQL migration; `MigrationModelDriftTests` stays green |

## Plan triage

- **[Claude-prüfbar]:** everything in the table above. It runs in-process with a fake executor and recorded tool output.
- **[braucht Nutzer/Hardware]:** one live check per transport against a real host:
  - Local `nsenter`.
  - mTLS one-shot container.
  - SSH.
  - An HTTPS target behind an internal-address exception.

## Addendum A — signatures as built (5 November 2026)

Accepted by the maintainer on 5 November 2026. The implementation (R1-57, review round 1) changed six signatures in
**Services** above. Rights, bounds, templates and behaviour stay as accepted; every change below either passes a value
the contract already required, or makes an accepted rule enforceable.

- **A1 `SaveAsync(draft, editorId, ct)`.** The owner (decision 2) is the Admin who saves. The store takes that principal
  id explicitly instead of reading ambient state. It refuses a blank or non-user id.
- **A2 `Http(string url, string host, …)`.** The validated URL is passed as the exact string that was checked, not
  re-serialised by `Uri`, which can change it. `host` decides between `--resolve` for a name and no pinning for an IP
  literal. A literal URL is already fixed to its address.
- **A3 `RunInDecisionAsync(checkId, ct)`.** "Run now" runs inside the UI's own `endpointcheck.run` decision for the
  signed-in Admin, and the result records that decision's operation id. It re-reads the check from the store inside the
  decision, so a stale copy loaded earlier by a page is never run. A missing, disabled or invalid check is `Refused`.
- **A4 `ParseHttp(output, expectedStatus)`.** `ExpectedStatus` (Definition table) has to reach the parser. A null value
  keeps the default of any 2xx status.
- **A5 `ResolvedAddress(Answered, Canonical)`.** An address is judged by its canonical form, where IPv4-mapped, NAT64
  and 6to4 addresses count as IPv4. It is probed and recorded as the host answered it, so an IPv6-only host behind
  DNS64 still reaches its target. The parsers take `string?`, and a null input is not a complete answer.
- **A6 `IEndpointCheckSlots`.** Decision 5 (at most 4 runs in flight, 1 per server) now holds on every path, with one
  extra rule: a check never runs twice at once. The monitor and "Run now" take the same slot. When no slot is free,
  "Run now" answers "busy" and does not wait.
- **A7 gateway arguments `new { serverId, checkId }`** (added 5 November 2026, review round 2; accepted 5 November 2026).
  The Run section first named them `new { check = id, server = serverId }`. The gateway resolves the `server` target of
  `endpointcheck.run` (and of `save` and `delete`) from the argument `serverId`, so every path — monitor, "Run now",
  save, delete and the refusals — passes `serverId` and `checkId`.

## Decisions for the maintainer

1. **Who may define checks:** Admin only, as R1-57 says, or also Operator for targets that need no exception?
   *Recommended:* Admin only (a run is a privileged command on the host).
2. **Run identity:** the check's owner is the Admin who last saved it, re-checked on every run, and the check is
   suspended when that owner loses Admin. *Recommended:* as described.
3. **Alert duration:** fail for 5 min (at least 2 runs) to open, pass for 5 min to clear. *Recommended:* yes, overridable
   per check later.
4. **Result retention:** keep results for 7 days per check, plus the last result indefinitely. *Recommended:* 7 days
   (≈10 000 rows per check at the 60 s minimum).
5. **Concurrency:** at most 4 runs at a time across the fleet, 1 per server. *Recommended:* yes.
6. **MCP:** add the Read-level tool `endpoint_checks_list` now, or leave MCP out of R1-57? *Recommended:* add it
   (Read only; agents can see failing checks).

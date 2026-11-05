# Services/EndpointChecks

R1-57 ([contract 0004](../../../../docs/contracts/0004-endpoint-checks.md)): **endpoint checks from the managed host**.
A check asks one host whether a service it can reach answers — HTTP status and latency, TCP connect, TLS certificate
expiry. It runs on that host through the host command executor; Whiskers never connects to the target itself, it
decides which addresses the host may contact. Operator guide: [`docs/operations/endpoint-checks.md`](../../../../docs/operations/endpoint-checks.md).

## Files

| File | Purpose |
|---|---|
| `EndpointCheckModels.cs` | `EndpointCheckDraft` (what an Admin hands over), `EndpointCheckDefinition` (a stored check, with owner, suspension and last outcome), `ResolveAnswer`, `ProbeResult`, `EndpointCheckRun` (one run as it is recorded). Stored exceptions that cannot be read except nothing (fail closed). |
| `EndpointCheckValidator.cs` | Pure; used on save **and** again before every run. Canonical IP literals only (decimal, octal, hex, shortened, zones, brackets refused), RFC 1123 names, absolute lower-case `http`/`https` URLs without user info, quotes, whitespace, shell or glob characters (brackets only around an IPv6 host); the definition port must equal the URL's effective port; interval 60–86 400 s, timeout 1–30 s; the server needs a shell transport (a Kubernetes server needs an SSH host). `RunDeadline` = timeout × 5 + 10 s. |
| `EndpointCheckCommands.cs` | Pure: the fixed templates (`getent ahosts`, `curl` with `-q`, `--proto '=http,https'`, `--resolve`, `--noproxy '*'` and no `-L`; OpenBSD `nc -z`, another variant answers `__nc=unsupported`; `openssl s_client` with SNI for a name). Operands single-quoted, IPv6 bracketed where the tool needs it; every template is `( … 2>&1; echo "__rc=$?" ) \| head -c 16384`. |
| `EndpointCheckParser.cs` | Pure, positive evidence only: the exit marker at the very end of the output plus the probe's answer (a three-digit status, a completed connect, a readable server certificate whose `notAfter` is far enough away). A resolve answer must be complete and well formed; addresses are canonicalised and de-duplicated in answer order. |
| `EndpointCheckRunner.cs` | `IEndpointCheckRunner`: one run inside one `endpointcheck.run` decision as the check's owner (owner and provenance, so the gateway applies the person's rules). Re-validates (a failure is one Refused event, nothing runs), resolves on the host, refuses the run when any answered address is internal and not excepted, tries at most 4 addresses (1 + 4 executor calls, each under the timeout, the whole run under one deadline). `RunInDecisionAsync` is "Run now" inside the UI's decision. The detail is one line of at most 300 characters, secrets redacted. |
| `EndpointCheckStore.cs` | `IEndpointCheckStore`: definitions and results in `MetricsDbContext` (`EndpointChecks`, `EndpointCheckResults`). `SaveAsync` (`endpointcheck.save`) makes the saving Admin the owner and ends a suspension; `DeleteAsync` (`endpointcheck.delete`) removes the check and its results; `SuspendAsync` switches a check off only while it still has the owner that lost authority; `PruneAsync` keeps each check's newest result. |
| `EndpointCheckMonitor.cs` | `FleetBackgroundService`, 15 s tick: starts due checks (at least 60 s apart, at most 4 at a time, 1 per server, longest overdue first), suspends a check whose owner lost Admin (`automation_suspended`), records each run, feeds `endpoint_check_failed` through `BreachTracker` (`RulesFor`: open after 5 min failing, clear after 5 min passing; Refused counts as failed), closes the alert of a check switched off or deleted, keeps open alerts in `endpoint-check-breaches.json`, prunes results older than 7 days once an hour. A failing run is logged and recorded and never ends the loop. |

## Gateway

| Capability | Level | Risk | Caller |
|---|---|---|---|
| `endpointcheck.save` | Admin | Reversible | `/checks` (create, edit, exceptions) |
| `endpointcheck.delete` | Admin | Reversible | `/checks` |
| `endpointcheck.run` | Admin | RootEquivalent | the monitor as the owner; "Run now" in `/checks` as the signed-in Admin |

`endpointcheck.run` is one of `MutationCompositions.RawHostCommands`: the probes go through
`IHostCommandExecutor.ExecuteMutationAsync`, which the scope guard refuses outside that decision.

## Tests

`EndpointCheckTests` (templates, ports, literals, resolution, exceptions, parsers, bounds, loop, alerting) and
`EndpointCheckGatingTests` (save/delete refusals, one audited run with its operation id, suspension, Kubernetes
without SSH), with the shared doubles in `EndpointCheckTestDoubles`.

# Services/EndpointChecks

R1-57 ([contract 0004](../../../../docs/contracts/0004-endpoint-checks.md)): **endpoint checks from the managed host**.
A check asks one host whether a service it can reach answers — HTTP status and latency, TCP connect, TLS certificate
expiry. It runs on that host through the host command executor; Whiskers never connects to the target itself, it
decides which addresses the host may contact. Operator guide: [`docs/operations/endpoint-checks.md`](../../../../docs/operations/endpoint-checks.md).

## Files

| File | Purpose |
|---|---|
| `EndpointCheckModels.cs` | `EndpointCheckDraft` (what an Admin hands over), `EndpointCheckDefinition` (a stored check, with owner, suspension and last outcome), `ResolveAnswer` of `ResolvedAddress` pairs (as answered, as judged), `ProbeResult`, `EndpointCheckRun` (one run as it is recorded). Stored exceptions that cannot be read except nothing (fail closed). |
| `EndpointCheckValidator.cs` | Pure; used on save **and** again before every run. Canonical IP literals only (decimal, octal, hex, shortened, zones, brackets refused), RFC 1123 names, absolute lower-case `http`/`https` URLs without user info, quotes, whitespace, shell or glob characters (brackets only around an IPv6 host); the definition port must equal the URL's effective port; interval 60–86 400 s, timeout 1–30 s; the server needs a shell transport (a Kubernetes server needs an SSH host). `RunDeadline` = timeout × 5 + 10 s. |
| `EndpointCheckCommands.cs` | Pure: the fixed templates (`getent ahosts`, `curl` with `-q`, `--proto '=http,https'`, `--resolve`, `--noproxy '*'` and no `-L`; OpenBSD `nc -z`, another variant answers `__nc=unsupported`; `openssl s_client` with SNI for a name). Operands single-quoted, IPv6 bracketed where the tool needs it; `curl` also runs with `-g` (no globbing); every template is `( … 2>&1; echo "__rc=$?" ) \| head -c 16384`. |
| `EndpointCheckParser.cs` | Pure, positive evidence only: the exit marker at the very end of an output that stayed under the 16 384-byte bound (one that reached it is cut off, whatever it ends with) plus the probe's answer (a three-digit status, a completed connect, a readable server certificate whose `notAfter` is far enough away). A resolve answer must be complete and well formed; addresses are de-duplicated by their canonical form in answer order and kept as answered. |
| `EndpointCheckRunner.cs` | `IEndpointCheckRunner`: one run inside one `endpointcheck.run` decision as the check's owner (owner and provenance, so the gateway applies the person's rules). Re-validates (a failure is one Refused event, nothing runs), resolves on the host, refuses the run when any answered address is internal and not excepted, refuses a check without an owner (at read level), judges each address canonically but probes it as answered (a literal as written), tries at most 4 addresses (1 + 4 executor calls, each under the timeout + 2 s, the whole run under one deadline). `RunInDecisionAsync(checkId)` is "Run now": it reads the check from the store inside the UI's decision (gone or off = Refused, nothing runs, marked `NothingRan` and never recorded) and stamps that decision's operation id on the result, marked `OnDemand`. A probe starts only when a whole attempt still fits before the run deadline ("Run deadline reached after N addresses" otherwise). The detail is one line of at most 300 characters, secrets redacted; `MaskInternalAddresses` replaces addresses in R1-54's denied ranges for readers below Admin. |
| `EndpointCheckStore.cs` | `IEndpointCheckStore`: definitions and results in `MetricsDbContext` (`EndpointChecks`, `EndpointCheckResults`). `SaveAsync` (`endpointcheck.save`) makes the saving Admin the owner and ends a suspension; `DeleteAsync` (`endpointcheck.delete`) removes the check and its results; `SuspendAsync` switches a check off only while it still has the owner that lost authority; `RecordAsync` skips a check deleted meanwhile and a `NothingRan` answer, and moves the schedule (`LastRunUtc`) only for a monitor run, never for an `OnDemand` one; `PruneAsync` keeps each check's newest result and removes the results of checks that are gone. |
| `EndpointCheckSlots.cs` | `IEndpointCheckSlots` (singleton): the run slots the monitor and "Run now" share — under one lock, at most 4 runs across the fleet, 1 per server, 1 per check. |
| `EndpointCheckMonitor.cs` | `FleetBackgroundService`, 15 s tick: starts due checks (at least 60 s apart, longest overdue first) when it gets a slot from `IEndpointCheckSlots`, suspends a check without an owner or whose owner lost Admin (`automation_suspended`), records each run, feeds `endpoint_check_failed` through `BreachTracker` (`RulesFor`: open after 5 min failing, clear after 5 min passing; Refused counts as failed), closes the alert of a check switched off or deleted, keeps open alerts in `endpoint-check-breaches.json`, prunes results older than 7 days once an hour. A failing run is logged and recorded and never ends the loop. |

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
without SSH), with the shared doubles in `EndpointCheckTestDoubles`; `ComponentTests/EndpointChecksPageTests` (masked
detail below Admin, no "Run now" for a check that is off); `BootSettings.AssertEndpointCheckSlotsShared` (the monitor and
the page share the one `IEndpointCheckSlots` singleton, checked on every boot-matrix boot).

# Contract 0017 — Security fixes for 1.0.1

Status: **Accepted** · 15 November 2026 by the maintainer, parts A–G as written, F2 included. Parts H–J were added
with the decisions of the same day (see "Decisions" at the end).

Scope:
- **Protected areas, approved:** the maintainer approved two changes on 2026-11-15:
  - the mTLS layer, part E;
  - the OAuth whitelist, part H, and only the two seeding and adding points described there.
- **Protected areas, unchanged:** the auth middleware and its order, roles, and the docker-socket-proxy verb
  whitelist.

## Purpose

The full review of 1.0.0 (15 November 2026; every finding reproduced by a failing test) found defects that break five
invariants marked ✅:

| Invariant | What is broken |
|---|---|
| INV-51 | Two sinks are never redacted. |
| INV-59 | A shell command can mount a directory above the control plane. |
| INV-62 | Two compose fields grant host access without a decision. |
| INV-16 / INV-24 | An MCP read key sees secrets that the UI masks. |
| Contract 0007 | A managed host can inject lines into a root script on the CA host. |

This contract fixes what each part must do. The detection table of contract 0006 (with contract 0013) gains rows; every
other part of contracts 0006, 0007 and 0013 stays binding.

## Current state (verified at `139dd5b3`)

Each row has a failing repro test; the test names are in the PRs.

| # | Where | Defect |
|---|---|---|
| A1 | `Mutations/ComposeConfigEffects.cs:160-207`, `ComposePlan.cs:773-848`, `ComposeExposure.cs:415-470` | `use_api_socket: true` bind-mounts the Docker socket. Checked with Compose v5.6.1: `Mounts` shows `/var/run/docker.sock` as `bind`, `rw`. The plan has no mount and no exposure for it, and the re-check reads no mounts. |
| A2 | `ComposeExposure.cs:330-382`, `ComposeModelExposures.cs:97-212` | A `post_start` / `pre_stop` hook with `privileged: true` is not an exposure. |
| B | `Guardrails/ProtectedResourceMatcher.cs:97-116` | In the shell branch, a `-v` / `--volume` / `--mount` source above the control plane is judged only when a word is a modifying command. Example: `docker run -v /var/lib/docker/volumes:/v alpine find /v -name vault.json -delete` is allowed for an MCP key. |
| C1 | `Persistence/SinkRedactionInterceptor.cs:38-80` | `WebhookLogs.Output/Error` and `UpdateHistory.Error` are stored raw. |
| C2 | `Utils/SecretRedactor.cs:48-50` | `Authorization: Basic <credential>` keeps the credential. |
| D | `Mcp/Tools/ContainerTools.cs:388-418` | `get_container_env` masks by its own key list. It does not use `EnvMasking.ShouldMask`, the rule the UI uses. |
| E1 | `Certificates/MtlsCertificateService.cs:217-220`, `MtlsCertificateCommands.cs:42-47` | The host's CSR answer is pasted verbatim into the root script on the CA host. Base64 decoding skips line breaks, so lines in between run as commands. |
| E2 | `Certificates/MtlsCertificateValidator.cs` (`RequireExactSans`) | Only DNS and IP SANs are compared, so URI and e-mail SANs pass, both in `ValidateCsr` and in `ValidateIssued`. |
| F | `Utils/MarkdownSanitizer.cs:14`, CSP `WhiskersPipelineExtensions.cs:85` | Markdig's generic attributes let model output carry `on*` attributes. The CSP has no `script-src`. |
| G | `Notifications/NotificationThrottler.cs:59` | The throttle key is `ServerId/ContainerId` plus the event type. A second `agent_approval` within the window reaches no channel. |

## Changes

### A — Compose: socket and privileged hooks (INV-59, INV-62)

- **A1 — the socket counts as a mount.**
  - `use_api_socket: true`, in the YAML model or in `docker compose config`, counts as a **bind mount of
    `/var/run/docker.sock`, read-write**, for that service.
  - It enters `plan.Mounts()` like a `volumes:` entry. The nested mount decision and the INV-59 control-plane rule
    therefore see it, and for keys, agents, automations and Operators it is refused as every socket mount is.
  - The re-check after `up` reads each created container's `Mounts`. A bind of the Docker socket that the plan did not
    decide counts as an undecided exposure of class `host`: it is stopped under the agent rule set, and
    `exposure_violation` is raised always.
- **A2 — a privileged hook is an exposure.**
  - A `post_start` or `pre_stop` entry with `privileged: true` is a `Privileged` exposure of its service.
  - Comparison key: `hook:<post_start|pre_stop>:<index>`. Value: `true`.
  - Detected in the model and in `FromConfig`.
  - **Known limit:** a hook is an exec, not container state, so the re-check cannot see it. It is decided at planning
    only, and the docs say so.
- **A3 — not part of this contract:** `macvlan` / `ipvlan` networks. That is a new row in the detection table and a
  decision of its own (1.1.0).

### B — Mount sources in shell commands (INV-59)

In the shell branch of `ProtectedResourceMatcher`, a mount source is checked against the control-plane list with
`AncestorHit`, whatever the command word is.

Mount sources are:
- the word after `-v` or `--volume`, and the `--volume=` form, up to the first `:`;
- the `source=` / `src=` value of a `--mount` word;
- in any word of the form `<absolute path>:<path>`, the part before the first `:`.

A hit is refused under the same rule as today: interactive Admin with an approval, everyone else refused. Words that
cannot be analysed stay as INV-24 has them.

### C — Sinks (INV-51)

- **C1 — two more sinks, and every free-text column decided.**
  - `SinkRedactionInterceptor` also redacts `WebhookLogs.Output`, `WebhookLogs.Error` and `UpdateHistory.Error`, with
    the same rules as `TaskRunHistory.Output`.
  - A structural test lists every `string` property of every entity in `MetricsDbContext`. Each one is either redacted
    by the interceptor or on an explicit exemption list with a reason. A new free-text column without a decision fails
    the test.
- **C2 — the whole `Authorization` value.**
  - For the key `authorization`, the redactor removes the whole value: scheme plus credential, up to the end of the
    header value.
  - The scheme word may stay readable (`Authorization: Basic ***`).

### D — One masking rule for environment variables (INV-16, INV-24)

`get_container_env` decides with `EnvMasking.ShouldMask(key, value)`, the rule the UI uses. Its own key list is
removed. The tool description keeps the promise "sensitive values are masked", and it is true again.

### E — The CSR is not host text (contract 0007; mTLS layer, approved)

- **E1 — Whiskers re-encodes the CSR.**
  - The host's answer is decoded and parsed (`ValidateCsr`, as today).
  - The script on the CA host receives only `Convert.ToBase64String` of the **parsed CSR's DER**, produced by
    Whiskers.
  - An answer whose non-whitespace characters are not all base64 characters is refused before parsing.
  - No byte of host output reaches a command line on the CA host.
- **E2 — exactly the identity, also by SAN count.**
  - `RequireExactSans` also requires that the number of general names in the SAN extension equals the number of
    expected DNS names plus IP addresses.
  - This is the rule `CheckIssuedClient` already applies to client certificates. It holds for the CSR and for the
    issued certificate.

### F — Model output renders no script (agent chat)

- **F1 — the Markdown pipeline.**
  - The pipeline used for model output is built **without** the generic-attributes extension.
  - `MarkdownSanitizer` additionally removes every attribute whose name starts with `on`, and every `style` attribute,
    from the rendered HTML.
- **F2 — the CSP.**
  - The two inline `<script>` blocks of `App.razor` move into files under `wwwroot/js/`.
  - The CSP becomes `frame-ancestors 'none'; script-src 'self'`.
  - The PR verifies in the browser that the app, the terminal, the graph and the theme switch still work, with no CSP
    violation in the console.

### G — Approvals are never throttled

- Events in `UnsuppressibleEvents` are never throttled.
- For every other event, the throttle key also includes the event's own id where it carries one (`ApprovalId`,
  `DeliveryId`, `TaskId`, the loop name).
- Two distinct events of one type therefore no longer share a throttle slot.

### H — Sign-in traps of the whitelist (OAuth whitelist; approved)

- **H1 — the admin email is seeded.**
  - When the whitelist is seeded enabled, `WHISKERS_ADMIN_EMAIL` is seeded with `GOOGLE_ADMIN_EMAIL`, when it is set.
  - The local admin that the README advertises can then sign in.
- **H2 — an account an Admin creates is added.**
  - `LocalAccountService.CreateAsync` adds the address to an **enabled** whitelist, through the gate, in the same
    audited operation, as the setup wizard's `AddFirstAdminAsync` does.
  - A disabled whitelist is not touched.
- **Unchanged:** nothing else in whitelist enforcement changes; a sign-in without a whitelist entry is still refused.

### I — No Python on the Whiskers host or in the repository

- **I1 — onboarding step 8.**
  - The step currently pipes a script into `python3 -` on the Whiskers host. It becomes a YAML edit in C#: read the
    scrape config, change the target entry, write it through the existing base64 file writer.
  - The server id reaches no program text.
- **I2 — the enrolment script.** `deploy/borg-fleet/borg-enroll` is rewritten in POSIX `sh`, with the same arguments,
  outputs and exit codes.
- **I3 — guard test.** A test fails when a tracked file is a Python file, or a C# string starts a `python`/`python3`
  process or pipe.

### J — The deadline is cooperative (INV-40)

- The capability deadline cancels the token. A delegate that ignores it runs to its end.
- `README.md` (Mutations, lines 445-446) and the `IUiMutationRunner` doc say exactly that.
- INV-40 is qualified the same way. The review counted 122 call sites that discard the token; the INV-40 row names this
  limit.
- A hard abort is not part of 1.0.1.

## Tests (acceptance)

- Every repro test of the review for parts A–G passes. It moves into the matching existing test class and keeps its
  name.
- **A:**
  - A plan with `use_api_socket: true` has a mount of `/var/run/docker.sock`.
  - An MCP key's deploy of that plan is refused.
  - A re-check sees an undecided socket bind and reports it.
- **B:**
  - Both review commands are refused.
  - `docker run -v /srv/app:/app …` is still allowed.
- **C:**
  - The structural column test.
  - A Basic header and a Bearer header are both redacted down to the scheme.
- **E:**
  - An answer with an extra line is refused.
  - The script on the CA host contains only Whiskers' own base64.
  - A CSR or certificate with a URI SAN is refused.
- **F:**
  - Model output carrying `{onmouseover=…}` renders without any `on*` attribute.
  - The response carries `script-src 'self'`.
- **G:** two approvals within the throttle window are both delivered.

## Documentation (same PRs)

- `SECURITY_INVARIANTS.md`:
  - the rows INV-51, INV-59 and INV-62 name the fix and the new tests;
  - INV-62 names the hook limit (A2).
- Contract 0006 gets a line pointing to this contract.
- `CHANGELOG.md` gets a 1.0.1 section.
- `SECURITY.md`:
  - the two contradictions are corrected (rotation at line 133, plaintext at line 208);
  - `Auth__LocalLogin__Enabled` is described as what it does: it hides the form. This text also changes in
    `docs/configuration.md` and `.env.example`.

## Plan triage

- Every part is `[Claude-prüfbar]`.
- The browser check of F2 runs against the local dev server.
- The CA path of part E is checked only against the test fake; a real renewal on a host is `[braucht Nutzer]` after
  the 1.0.1 deploy.

## Decisions (maintainer, 15 November 2026)

1. Parts A–G are accepted as written.
2. F2 is included: the inline scripts move into files, and the CSP becomes `script-src 'self'`.
3. The OAuth whitelist may change for part H.
4. Python is replaced in 1.0.1 already (part I).
5. The deadline stays cooperative; the docs and INV-40 are corrected (part J).

## Amendment 1 — review round 1 (15 November 2026, accepted by the maintainer the same day)

This amendment answers the review of the draft and records four decisions the maintainer took on it. Where it differs
from the parts above, this amendment wins.

### Corrections to "Current state"

- **B.** The review command passed for two reasons:
  - its `find` is in `ModifyingCommands`, so the gate was open;
  - `AncestorHit` got the unsplit word `/var/lib/docker/volumes:/v`.

  Both are fixed: the split at `:`, and no command gate for mount sources.
- **A1.** The re-check reads `Mounts` only for third-party tools (`ComposeThirdParty.cs:131`), never as exposures.
- **D.** No invariant row describes env masking. The defect is a contradiction between the tool and the UI, and
  between the tool and its own description. INV-16 and INV-24 are not affected.
- **E1.** Only lines made of base64-alphabet characters and blanks pass the decoder, for example `reboot` or
  `docker kill …`. That is still an injection.
- **F.** The pipeline is built in `Components/Pages/AgentView.razor:269-272` and in `Help.razor:121`.
  `MarkdownSanitizer.cs:14` is the href neutraliser.
- **G.** `NotificationEvent` carries `ApprovalId`, `DeliveryId` and `CorrelationId` only.
- **I1.** The step is ⑧ in the UI and step 7 in the code comment (`OnboardingService.cs:244-247`).
- **J.** The file is `src/Whiskers/Services/Mutations/README.md`. Lines 445-446 already describe the case; the
  promise to correct sits in the `IUiMutationRunner` doc (`UiMutationRunner.cs:15`).

### A — socket mounts as an exposure kind

- **New kind.** A new exposure kind `SocketMount`: class `host`, key `mount:<destination>`, value the normalised
  source. The closed enum of contract 0006 Amendment 8 gains it.
- **What counts as a socket mount.** A container mount is a socket mount when its type is `bind` and its normalised
  source is either:
  - a socket path of `ControlPlaneResources`, or
  - a path whose file name is `docker.sock`. This covers rootless `/run/user/<uid>/docker.sock`.
- **When it is decided.** Per service: only when `plan.Mounts()` of that service holds the same normalised source.
  Mounts that arrive through `volumes_from` count for the receiving service.
- **Unreadable mounts.** A container whose `Mounts` cannot be read counts as undecided.
- **How the fields are read.**
  - `use_api_socket`, and `privileged` inside a hook, are read through `<<` merges and `extends` like `privileged`.
  - A value from an unset variable refuses the plan.
  - A hook key that differs between the YAML model and `docker compose config` counts as undecided.
- **Registry credentials.** The docs and the approval text say that `use_api_socket` may also hand the caller's
  registry credentials to the container. Where this is verified for Compose v5.6.1, they say so without "may".

### B — every mount source, wherever it stands

A mount source is, in any word of a shell command:

1. **Flag forms.** Each up to the first `:`:
   - the text after a leading `-v`, `-v=`, `--volume=` or `--mount=`;
   - the text after a short-flag group ending in `v` (`-itv/…`);
   - the word after `-v`, `--volume`, `--mount`, or after a short-flag group ending in `v`.
2. **Key=value forms.** The value of every `source=`, `src=` and `device=` key in a comma-separated key=value word,
   also after `volume-opt=`, and after `-o` / `--opt` (`docker volume create -o device=…`).
3. **Path pairs.** The part before the first `:` of any word `<path>:<path>`.

A source that starts with `.` or `~` cannot be resolved and counts as unanalysable free text (INV-24).

**Accepted limit.** Another tool's `-v` followed by a protected path is refused for a key (for example
`grep -v /var/run file`). This is fail-closed and deliberate.

### C — redaction

- **C2 — the `Authorization` value.**
  - The value is removed up to the end of the line, or up to the closing quote of the argument that holds it.
  - A scheme stays readable only when it is one of `Basic`, `Bearer`, `Digest`, `Negotiate` or `NTLM` **and** more
    text follows.
  - A one-word value is removed whole.
  - `Proxy-Authorization` is treated the same.
  - Redacting twice changes nothing.
- **C1 — writes that bypass the interceptor.** A source test fails when an `ExecuteUpdate` sets a non-exempt string
  column without redacting it explicitly. String collections and owned types are covered by the structural test.

### D — one masking rule, made stronger

- `EnvMasking` also masks:
  - keys containing `PASS` or `PWD`;
  - values containing `Password=` or `Pwd=` (connection strings).
- A parity test checks that the tool masks exactly when `EnvMasking.ShouldMask` is true.

### E — the CSR bytes (mTLS approval covers both callers)

- **E1 — the attack the tests must catch.**
  - The acceptance test inserts a line of base64-alphabet characters (`reboot`). The answer either fails, or the CA
    script's only variable text is base64 that Whiskers produced; the inserted line appears nowhere.
  - A non-base64 answer was already refused in 1.0.0. That test alone proves nothing.
- **E1 — the bytes.**
  - `SignCsr` takes the DER bytes and encodes them itself.
  - The DER is the content of the single `CERTIFICATE REQUEST` block that `ValidateCsr` verified. A second PEM block
    or trailing data is refused.
  - The CA still receives a PEM file, which Whiskers writes from the DER.
- **The second caller.** `SignCsr` is also used by the client path of contract 0007
  (`MtlsClientCredentialStore.cs:303`). It is adapted to the new signature with no other change. **The maintainer's
  mTLS approval covers it (decision 7).**
- **NIT.** The identity regex in `MtlsIdentity.cs:67` ends in `\z`, not `$`.

### F — rendering and CSP

- **F1 — the Markdown pipeline.**
  - Model output uses an explicit list of Markdown extensions, not `UseAdvancedExtensions()`. The list excludes
    generic attributes, media links and custom containers.
  - The sanitizer removes `on*` and `style` attributes from tags only, never from text or code.
  - It limits `img src` like `href`: relative or `data:` only.
- **F2 — the header.**
  - The CSP is exactly `frame-ancestors 'none'; script-src 'self'; object-src 'none'; base-uri 'self'`, and the test
    asserts the exact value.
  - The middleware stays where it is; only this header value changes.
- **F2 — inline script removed.**
  - The `eval` interop call in `AiChat.razor:407` becomes a named function in `wwwroot/js`.
  - The inline `onmouseover` / `onmouseout` in `AiChat.razor:35` become CSS `:hover`.
  - A source test fails on `Invoke…Async("eval"` and on a literal `on<word>="` attribute in a `.razor` file.
  - The theme script stays a blocking script in `<head>`.
- **F2 — browser check.** It also covers the chat widget, the agent page, and sign-in and sign-out.

### G — what is never throttled

- An event that carries an `ApprovalId` is never throttled.
- Every other event keeps the key `ServerId/ContainerId` plus type.
- `DeliveryId` is never part of the key, because every event gets a fresh one.
- `UnsuppressibleEvents` are not exempt as a group. `automation_refused` fires once per refused call and could flood
  the channels.
- Tests:
  - two `unhealthy` events for one container inside the window give one delivery;
  - two approvals inside the window give two deliveries.

### H — whitelist (decision 6)

- **H1 — seeding.** When the whitelist is seeded enabled from `GoogleAuth:AllowedEmails`, a set
  `WHISKERS_ADMIN_EMAIL` is added to the seeded list. `WHISKERS_ADMIN_EMAIL` alone seeds no whitelist.
- **H2 — adding.**
  - The entry admits the address for every sign-in method; the docs say so.
  - The addition first prunes orphan bindings (contract 0003, amendment c).
  - A failed whitelist write undoes the account, as a failed role write does.
  - Lock order: `AdminGate`, then `AccessControlWriteGate`.
  - The account records that H2 added the entry.
- **H3 — removing (third write point, approved).** `LocalAccountService.DeleteAsync` removes an entry that H2 added,
  through the gate, with the binding prune of contract 0003 rule 5. An address that was listed before the account was
  created stays.
- **Existing installs.** A 1.0.0 install that already has a `whitelist.json` is not changed by H1; the CHANGELOG
  tells the operator to add the address. `LocalAdminSeeder` does not go through `CreateAsync`.
- **Tests.**
  - The seeded local admin signs in.
  - `WHISKERS_ADMIN_EMAIL` alone enables no whitelist.
  - An unlisted address is refused on local and federated sign-in.
  - A disabled whitelist is byte-identical after `CreateAsync`.
  - A refused `CreateAsync` adds nothing.
  - `DeleteAsync` removes only its own entry.

### I — no Python (decision 8)

- **Every Python call is replaced in 1.0.1.** Besides the two named in I1 and I2:
  - `deploy/borg-fleet/borg-fleet-client:150-151`
  - `deploy/borg-fleet/borg-fleet-prune:36-37`
  - `deploy/tailnet-guard.sh:39, 55`
- **Tools.** The replacements use `sh` with `jq`, or `awk`. Each script names its tools at the top and refuses to
  start without them.
- **Onboarding step 8 (I1).** The server id and the address are emitted as YAML scalars by the YAML writer. An entry
  already present for the address changes nothing.
- **Guard test (I3).** It fails when a tracked file:
  - has a `.py` extension;
  - has a python shebang; or
  - is a script or C# source outside the tests and the matcher's interpreter list that invokes `python`/`python3`.
- **Required tests.** `.github/required-tests/in-process.txt:1243` (`AddScrapeTarget_ships_python_base64_encoded`)
  changes in the same PR (contract 0008).
- **`borg-enroll` tests.** One test per exit code, run against stubs.

### J — INV-40 (decision 9)

- INV-40 stays ✅, with precise wording: every execution has a deadline that cancels its token; an action that ignores
  the token runs to its end, and its slot stays held until then.
- The `IUiMutationRunner` doc says the same.

### Documentation, additionally

- The INV-40 row.
- The Mutations and Guardrails READMEs.
- The `get_container_env` description.
- A pointer in the detection table of contract 0013.
- `docs/operations/local-accounts.md` (H2, H3).

### Decisions (maintainer, 15 November 2026)

6. H3 is approved as a third write point of the whitelist, limited to entries H2 added.
7. The mTLS approval covers the adaptation of the contract 0007 client path to the new `SignCsr` signature.
8. Every Python call named above is replaced in 1.0.1.
9. INV-40 stays ✅ with the precise wording of part J.

## Amendment 2 — review round 2 (15 November 2026)

Wording only: no new decision and no new protected area. Where it differs, this amendment wins over Amendment 1.

### I — tools, and what happens when one is missing

- **Tools.** The scripts need these tools, and each script names them at the top:

  | Job | Tool | Notes |
  |---|---|---|
  | JSON | `jq` | |
  | SQLite snapshot | `sqlite3` | `.timeout`, `.backup`; each copy under `timeout 300`. The header check and size limits stay as today, and no half copies are left. |
  | Probe in `tailnet-guard.sh` | `nc -z -w5`, or bash `/dev/tcp` under `timeout 5` | |

- **Missing tool.** Each function keeps today's behaviour:
  - Without `sqlite3`, the SQLite snapshot is skipped with a WARN that the run status reports, and the backup itself
    still runs.
  - Only a tool that the whole script needs refuses the start.
- **Rollout.** The tools are installed on every fleet server before the new client is rolled out. This step is
  `[braucht Nutzer]`.
- **Test.** The client without `sqlite3` still completes a backup and reports the skipped snapshot.

### A — socket path equivalence

- The socket paths of `ControlPlaneResources` count as one source.
- For `use_api_socket`, the decided source is the engine's socket, whatever its path.

An Admin-approved deploy therefore raises no violation because the inspect reports `/run/docker.sock` where the plan
said `/var/run/docker.sock`.

### F2 — the source test

The test fails on an `on<word>="` attribute that is **not** preceded by `@`. Blazor's `@onclick` stays allowed.

### H2/H3 — the record and failures

- **The record.** The record that H2 added an entry is a sibling map in `whitelist.json`: address → account id.
  - `Emails` stays a list of strings.
  - `SaveWhitelistAsync` keeps the record of every address that stays listed, and drops it only for an address the
    save removes.
  - No database migration.
  - Test: an unrelated whitelist save, then `DeleteAsync`, still removes the entry.
- **Failed removal.** A failed whitelist removal undoes the deletion, as a failed role removal does
  (`RestoreRoleAsync`).
- **Manual change.** When an Admin removed or re-added the entry by hand in between, the record is cleared and H3
  leaves the entry.

### D — shell variables

The exact names `PWD` and `OLDPWD` are not masked. The masking rule is shared with the UI, so the CHANGELOG names
the change for both.

### NITs

- **B.** "Up to the first `:`" does not apply to `--mount=`; form 2 reads its value.
- **E1.** Non-PEM text outside the `CERTIFICATE REQUEST` block is ignored and never forwarded. A second PEM block is
  still refused.
- **J.** The INV-40 row drops "cannot hold a slot indefinitely".

## Amendment 3 (2026-11-15, from the S2 review, rounds 2 to 4)

The reviews of the B implementation found bypasses through forms Docker accepts. Rounds 3 and 4 close them by class,
not by instance, and fail closed. Where it differs, this amendment wins over Amendments 1 and 2 for part B.

### B — where a mount source stands

- **Short syntax**, cut at its first `:`:
  - the word after `-v` or `--volume`, in any command;
  - the `--volume=` form;
  - the value attached to `-v` or to a short-flag cluster ending in `v` (`-v/src:dst`, `-v=src:dst`, `-itv/src:dst`);
  - any `<absolute path>:<path>` word;
  - the word after a cluster `-<letters>v` (`-itv`, `-dv`), when it starts with `.`, `~` or `/` and holds a `:`, in
    any command. No program name decides this (round 4): a name is chosen by whoever writes the command (a copy named
    `rsync`, `alias cp=docker`, `ssh host docker …`, `find -exec docker …`, an option value such as `sudo -u tar` read
    as the command word). Round 3's verbose-tool exception is withdrawn.
- **Keyed values**: the value of a `source=`, `src=` or `device=` key, also after `volume-opt=`.
  - Keys match in any case inside a `--mount` / `--mount-add` value and a `-o` / `--opt` value (their `=` forms and an
    attached `-o<value>` included), and anywhere in a command that names a container runtime. Docker lower-cases the
    keys. This reverses the lower-case-only narrowing of Amendment 1.
  - Elsewhere, keys count only in lower case at the start of a comma-separated part, so `echo SRC=./build` is none.
  - A keyed value is a path. It is checked whole, and also cut at its first `:`, so `source=/var/run:x` and
    `source=/tmp/a:b/../../var/run` are refused.
  - A `--mount` / `--mount-add` value that holds a `"` (a CSV-quoted field, where a comma may sit inside a source) is
    unanalysable.

### B — what a mount source may be (allow-list)

- Only a plain absolute path is compared with the control plane: `^/[A-Za-z0-9._@+/:-]*$`, normalised (no `..` left),
  and not under `/proc`, `/sys` or `/dev`.
- Any other source that starts with `.`, `~` or `/` cannot be resolved and makes the command unanalysable free text
  (INV-24). This covers quotes, commas, globs, backslashes, `$`, backticks, spaces, and `/proc/<pid>/root`,
  `/proc/<pid>/task/<tid>/cwd` and the like.
- A source that does not start with `/` is decided by how it is written (round 4):
  - **host path**: a `device=` value inside a `-o` / `--opt` / `--mount` value (`volume-opt=device=` included), and a
    `source=` / `src=` value of a `--mount` value with `type=bind`. The local volume driver binds a relative `device=`
    against the daemon's working directory (`/`), so `device=var/run` is `/var/run` (verified on Docker); a relative
    bind source is made absolute against the client's. Every non-empty value that is not a plain absolute path is
    unanalysable, `device=var` included;
  - **keyed** (any other `source=` / `src=` value): one that contains `/` is unanalysable; one without `/` is a named
    volume;
  - **short syntax** (the word after `-v` and the other short forms): one without `/` is a named volume; one with `/`
    that starts with neither `/`, `.`, `~` nor a quote is not a source Docker accepts (it refuses `var/run:/r` as a
    volume name), so it stays allowed (`awk -v x=./y`).

### B — reading the command

- A line continuation (`\` and a newline) is removed, outside quotes and inside double quotes (round 4:
  `"whis\<newline>kers"` is `whiskers`). It starts no word of its own.
- A backslash-escaped character counts as quoted text (round 4). A word built with escapes that looks like a command
  (`sh -c docker\ run\ -v\ /var/run:/r`, `ssh host docker\ stop\ whiskers`, `tar --checkpoint-action=exec=…`) is read
  again whole. A word that still holds whitespace and was not read again is unanalysable.
- A command word the shell expands as a glob (`/usr/bin/docke?`, `[d]ocker`, `nerdct[l]`) is unanalysable. A bare `*`,
  as in re-read SQL `count(*)`, and the test command `[` are not.
- A command longer than 64 Ki characters is unanalysable. The per-command checks read only the words of their own
  command, so the time grows linearly with the command. Above the cap the helpers that look for container ids
  (`MentionWords`, `RuntimeIdWords`, `InvokesContainerRuntime`) see no words; the request is unanalysable all the same.

### Accepted overreach

The rule ignores the command word, so these are refused for a key too. This is fail-closed and deliberate:
- another tool's plain `-v` followed by `./`, `~`, a non-plain path or a directory above a protected path, for example
  `grep -v . file`, `mv -v ./a ./b`, `ls -v ~/x`, `grep -v /var /etc/fstab`;
- any tool's `v` cluster followed by a word that starts with `.`, `~` or `/` and holds a `:`, for example
  `rsync -av ./a:b /srv/c`, `scp -rv ./a:b host:/c`, `tar -czv ./a:b`, `curl -sv ./x:/r`; it needs an approval;
- a lower-case `src=./…` word, for example `curl -d src=./x`, and a lower-case `device=` value with a relative path in
  a `-o` / `--opt` value of any tool. A `device=` value that is not a plain absolute path is refused too, so the
  standard NFS and tmpfs volumes (`-o device=:/export/data`, `-o device=tmpfs`) need an approval for a key;
- any mount of `/proc`, `/sys` or `/dev`, for example `-v /sys/fs/cgroup:/sys/fs/cgroup:ro`;
- a word with escaped whitespace that, read again, names something protected, for example `echo docker\ stop\ whiskers`;
- a word with an escaped quote or `$`, for example `echo it\'s fine`, `echo \$HOME`, `grep price\$ /etc/hosts`: it is
  read again, and a `$` or an open quote there makes it unanalysable;
- a word with quoted and unquoted parts that looks like a command is read again whole (round 5, B13), for example
  `sh -c 'docker run -v '/var/run:/r`; a protected name or path anywhere in it is a hit;
- a quoted or escaped word with an expanding brace or a redirection is read again too (round 6, B14), so
  `bash -c '{docker,stop,whiskers}'` is unanalysable; a quoted or escaped glob is tested as a glob
  (`ssh host rm '/var/lib/docker/volumes/whisk*'`);
- rounds 7 to 10: a brace is decided as bash decides it: by depth, skipping escaped and quoted characters, and a `}`
  that brings it back to its own level closes it only after a `,` or `..` at that level, otherwise bash reads on. So
  `whi{{},s}kers`, `whi{\},s}kers`, `whi{'}',s}kers`, `whi{x},s}kers` and `whi{},s}kers` are unanalysable, and a brace
  still open when the word ends is a letter (`--format '{{.Names}},{{.Status}}'`). Every brace of a text is decided in
  one pass with the words' own lexer (a comment starts only outside a word, `\"` does not end a double quote), so the
  time stays linear. A bracket expression ends at its first `]` after a leading `]` (`docker.soc[]k]`) and never
  crosses `/`; a segment with `[[` or a `]` left over once its brackets are read (`[[.o.]]`, `[a]o]`) is read from its
  first `[` on as `*`, which in an absolute path can only find more and in a relative one may stand for any letter
  (`grep '[0-9][0-9]*'`, `[a-z]*[0-9].log` and `[[ -f … ]]` stay allowed). A glob
  in a path is tested against each protected path and everything below it as a real intersection of the two patterns
  (the glob's `*` and `?` stay within a segment, the pattern's `*` does not), in any segment and under a compose
  prefix (`…/whis?ers_data/_data/vault.key`, `…/proj_whis[k]ers_data`); for each glob the time is the product of its
  length and the pattern's. Repeated slashes and `.` segments are collapsed first; a segment that is `..` or can expand
  to `.` or `..` (`.*`, `.?` in dash or an older bash) counts as a hit; one whose bracket excludes the dot
  (`.[!.]*`) does not (rounds 8 to 10). In a command that changes or removes what it names (`rm`, `mv`, `chmod`, `chown`, `find …
  -delete`), a glob that could name a directory above a protected path is a hit (`rm -r /var/lib/dock?r`,
  `chmod -R 000 /var/lib/docker/v*`). A relative glob is a hit when a segment could be a protected directory
  or volume name with its `?` and `[…]` standing for letters (`cd /app/data && cat mtl?/client.key`, `rm -rf mtl?`,
  `cd /var/lib/docker/volumes && cat whisk?rs_data/…`); its `*` stands only for what the name leaves open, so `ls logs/*.log` and `rm -f build/*.o` stay allowed (round 9). A quoted word
  that is read again is not also tested as one glob. A glob
  without `/` (a name) hits when it could spell a protected name with its `?` and `[…]` standing for letters, or when
  it matches the name's stem (`whis*`); its `*` does not stand for the protected letters, so `*.log` hits nothing.
  `fish -c` is unanalysable (fish substitutes commands with plain parentheses). A remote shell reached through `ssh` is
  assumed to be a POSIX shell;
- a quote inside double quotes (`echo "it's fine"`), a mount source with a space (`-v '/srv/my dir':/d`) and
  `docker compose -f … up` are refused for a key too, and so are quoted globs and programs that could reach a
  protected name or path the same way an unquoted one does: `find /etc -name 's*'`, `systemctl status 'nginx*'`,
  `du -sh '/var/log/*'`, `ls '/srv/*/config.yml'`, `jq '{a:.x,b:.y}' f`, `ls /var/lib/docker/volumes/a*` (it could be
  `a_whiskers…`), and an absolute glob segment that can be `..` (`ls /srv/app/../logs/*.log`, `ls /root/.*`). Such a
  hit names no control-plane pattern; an interactive Admin is still asked to approve it.

These stay allowed: `rsync -av ./a host:/b`, `tar -czvf ./a.tgz ./dir`, `cp -rv ./a ./b`, `awk -v x=./y`, `grep -v foo`,
`scp ./a host:/b`, `docker exec app cp -rv ./a ./b`, `-v /srv/app:/app:ro` and named volumes.

### Gaps left for 1.1.0

- `docker cp` into or out of a protected path;
- `cp -s` / `cp -as`;
- `bwrap --bind` and `systemd-nspawn --bind`;
- `podman --rootfs`;
- `--volumes-from`;
- relative paths outside a mount (`cat ../var/run/docker.sock`, `cat ~/../var/run/docker.sock`): the working directory
  is unknown, so only mount sources are decided by how they are written;
- a name glob whose `*` stands for protected letters under a compose prefix (`proj_whis*` in the working directory),
  and a relative glob whose `*` does (`cd /app/data && rm -rf mt*`);
- reading or copying a directory above a protected path (`tar czf /tmp/x.tgz /var/lib/docker`, `cp -a /var/lib/docker
  /tmp/x`): only changing commands are decided on the directories above;
- the cost of words with very many path segments (about 1.5 s at the cap): the number of segments per word is not
  limited yet;
- a relative glob of one segment in a reading command (`cd /var/run && curl --unix-socket docker.s?ck …`), like
  relative paths in general;
- zsh globs and extglob forms (`whis(k)ers`, `@(k)`, `zsh -c 'cat /var/run/docker.s(o)ck'`), a non-POSIX login shell behind `ssh` or `su -c`, and other
  non-POSIX shells run with `-c` (`nu`, `xonsh`, `rc`, `es`);
- the classes that need a real shell parser rather than a word reader: command words hidden behind option values of
  prefixes (`sudo -u`, `env -u`, `exec -a`, `flock`, `chroot`, `timeout -s`), functions and aliases defined in the
  same text, redirections between words, and nested shells beyond four levels. Round 4 does not rely on reading
  them right: no decision of part B depends on which word is the command word any more, except the glob rule.

## Amendment 4 — the throttle subject (16 November 2026)

Status:
- **Original decision**, accepted by the maintainer on 16 November 2026 (recorded in the release plan, T1): a new
  optional field `ThrottleSubject` on `NotificationEvent`; the four emitters set it to the webhook id, the task or
  automation id, stack plus rule, and the loop name; when set, it is part of the throttle key; the same subject stays
  throttled, distinct subjects are delivered once each; `ApprovalId` events, state transitions, `DeliveryId` and
  events without a subject are unchanged.
- **Deviations found while building, accepted by the maintainer on 18 November 2026:**
  - a. `exposure_violation`: "stack plus rule" is a hash of the stack, the rule set and the violations — project
    names and violation lines come from compose files, and a violation found after the first report (a failed
    restore) must not be swallowed by it.
  - b. `monitoring_resumed` carries the loop subject too — it is built by the same call as `monitoring_stalled` and
    is not a state transition, so the second loop's resumption would be swallowed while its stall was delivered.
  - c. `webhooks:<ids>` for a notice that names several webhooks (the startup migration, the reconcile of unsettled
    deliveries) — one notice, one subject over the distinct, sorted ids.
  - d. The webhook's database id, not its `WebhookId` — the `WebhookId` is part of the webhook's URL.

Where it differs from part G of Amendment 1, this amendment wins.

### G2 — events without a container name their subject

- **The gap.** The throttle key is `ServerId/ContainerId` plus type. Four event types carry no container, so all their
  events of one server shared one slot, and a second distinct subject inside the window (default 5 minutes) was
  silently swallowed — a second webhook disabled, a second task refused, a second loop stalled.
- **The field.** `NotificationEvent` gets a new optional field `ThrottleSubject` (`string?`). Additive: every other
  producer leaves it unset.
- **The key.** When the subject is set, it is part of the key: `ServerId/ContainerId` + subject + type. Distinct subjects
  are delivered once each; the same subject stays throttled (flood protection: `automation_refused` fires once per
  refused call).
- **Unchanged.**
  - An event that carries an `ApprovalId` is never throttled.
  - State transitions (`NotificationEventTypes.IsStateTransition`) are exempt.
  - `DeliveryId` is never part of the key.
  - An event without a subject (null or empty) keeps exactly today's key.
- **The emitters and their subjects.** Built only by `NotificationThrottleSubjects`:

  | Event type | Emitter | Subject |
  |---|---|---|
  | `webhook_disabled` | `WebhooksModule` (startup migration of secret-less webhooks, one notice for all) | `webhooks:<ids>` — the database ids, distinct and sorted |
  | `automation_refused`, `automation_suspended` | `WebhookService` | `webhook:<id>` (database id, not the `WebhookId` of the URL); the reconcile notice that names several: `webhooks:<ids>` |
  | `automation_refused`, `automation_suspended` | `TaskExecutor` | `task:<id>` |
  | `automation_suspended` | `AiTriggerAuthority` | `ai-trigger:<id>` |
  | `automation_suspended` | `EndpointCheckMonitor` | `endpoint-check:<id>` |
  | `automation_suspended` | `AutoPatchService`, `AutoUpdateService` | `automation:autopatch`, `automation:autoupdate` |
  | `exposure_violation` | `ComposeRunner` | `exposure:<hash>` — SHA-256 (first 16 bytes, hex) of the stack (project name), the rule set (agent or person) and the distinct, sorted violations; a violation found after the first report (a restore that failed) is a new subject |
  | `monitoring_stalled` (and its `monitoring_resumed`) | `ScanSupervisor` | `loop:<loop name>` |

- **What a subject may hold.** Ids and fixed names only — never free text, a secret or a URL token. The exposure subject
  is a hash because project names and violation lines come from compose files. The subject is a key only: no channel,
  formatter, payload, feed or log shows it. It is not redacted, like `ServerId`, because it holds no text.
- **Persisted in the outbox.** The outbox stores the whole event (`NotificationOutbox`, `notification-outbox.json`
  and its `.bak`), so the subject is persisted with it. Harmless: it holds ids, GUIDs or a hash only; `Copy()` and
  the composite's `Redacted()` keep it; and a redelivery skips the throttle. It is deliberately not `[JsonIgnore]`:
  a redelivery from the outbox stays identical to the event first sent.
- **Tests** (`NotificationThrottleSubjectTests`, and one assertion per emitter in its own test class):
  - two distinct subjects inside the window give two deliveries, for each of the four types (theory);
  - the same subject twice gives one delivery, for each of the four types (theory);
  - an event without a subject keeps the key `ServerId/ContainerId`;
  - approvals and state transitions stay exempt whatever the subject;
  - `DeliveryId` is still not part of the key;
  - subjects are ids or a hash, never the project name;
  - no channel payload or formatter shows the subject; only the throttler reads it and only
    `NotificationThrottleSubjects` builds it (source scan);
  - two loops stalled on one server carry their loop as subject and both pass one channel's throttle
    (`MonitoringAlarmLogicTests`).

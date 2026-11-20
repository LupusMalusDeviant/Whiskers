# Contract 0017 — Trusted script paths per server

Status: **Accepted** · 20 November 2026 by the maintainer, with the answers below on the two open questions
(hash pinning: yes; redirection protection: trusted script paths only). The interfaces section was added after the
approval without changing behaviour. **D-1** (second approver, below) was approved as drafted on 20 November 2026.
With a single Admin and *Require a second approver* on, an addition stays pending. The Admin can switch the setting off
on their own, as for every approval today, and both saves are on record.

## Purpose

INV-24 refuses a command that runs code its text does not show. That includes a script called by its path
(`/usr/local/sbin/burgcloud-db-dump.sh`), another interpreter (`python3 -c …`), a shell without `-c`, a build tool and
a changing compose command. The guardrail cannot check what such code touches, so the refusal is intended. Since
`fix/guardrail-script-reason` its reason says so ("runs a script, an interpreter or a recipe whose content is not
shown here").

While the policy protects anything, a scheduled `CustomCommand` that runs an ops script never runs. Examples are the
BurgCloud DB dump and the disk usage check. Automation (the scheduler, an MCP key) is refused outright, and only an
interactive web Admin is asked to confirm. This contract gives an Admin a narrow, hash-pinned way to vouch for one
script on one server.

## Behaviour

1. **List per server.** Each guardrail policy (per preset) may hold `TrustedScripts`, a list of
   `{ serverId, path, sha256 }` entries.
   - `path` is absolute and made only of `A–Z a–z 0–9 . _ - + /`. It has no `..` segment, no `//` and no trailing `/`.
   - Its file name is not a shell, an interpreter, a build or scheduling tool, or a command that builds its arguments
     from data. `/bin/bash` or `/usr/bin/python3` can never be trusted.
   - `sha256` is 64 lowercase hex characters: the SHA-256 of the file when it was approved.
   - Within a policy, `(serverId, path)` is unique. An invalid entry makes the configuration invalid, and the store
     fails closed (INV-25).
2. **Who may change it.** The list is part of the guardrail policy. Only `guardrails.save` changes the policy: a
   capability of the web UI that only an Admin may use, so no MCP key, automation or agent can reach it. The page reads
   the hash from the server itself; an Admin cannot type one in. Every save that changes the list writes an audit row
   `guardrails.trusted_scripts`, naming server, path and hash for each entry added or removed, never the content.
3. **What becomes analysable.** Only a *trusted call*: the whole command is the listed `path`, optionally followed by
   literal arguments made only of `A–Z a–z 0–9 . _ - + / , : = @ %` and separated by blanks. That rules out quotes,
   `$`, backticks, braces, globs, `;`, `|`, `&`, redirection, `#`, `~` and line breaks. Its argument words are still
   read as a command of their own and matched against protected names and paths. A shell, an interpreter or a
   removing command among them stays a finding, exactly as without the list. Anything else is unanalysable as before:
   `sudo`/`env` in front, a second command, a run-time construct, or the listed path on another server.
4. **Pin the content.** When the gateway resolves a `host.exec` whose command is a trusted call for the resolved
   server, it reads the file's hash on that server with the fixed `sha256sum -- '<path>'`, over the host executor's
   read path. The hash is part of the target's fingerprint (contract 0012). The gateway resolves again right before the
   run (`ConfirmAsync`), so the hash is read twice: at the decision and just before execution.
   - If the hash differs from the approved one, the call is refused before policy: "The trusted script `<path>` on
     `<server>` changed since it was approved; nothing ran."
   - If the hash cannot be read, the call is refused the same way.
   - If the hash changes between decision and run, nothing runs (target changed).
5. **Protect the path.** For a changing request, every listed path (on any server) counts as a protected path, in
   every argument and in every word of a command, the target of a redirection included. Only the command word of a
   trusted call is exempt. `rm`, `mv`, `cp`, `tee`, `sed -i` or `> path` on a listed script is therefore a finding. A
   removing command on a directory above it is one too. An interactive web Admin may confirm such a finding, for
   everyone else it is refused. A script updated this way has a new hash and must be approved again.
6. **No exception for interpreters.** `python3 -c`, `bash -c "$X"` and the like are not covered (see point 1).

## Interfaces

- `Whiskers.Configuration.TrustedScript` — `ServerId`, `Path`, `Sha256` (strings).
- `GuardrailPolicy.TrustedScripts` — `List<TrustedScript>?`. It is optional in `guardrails.json` and left out of the
  stored and hashed policy while empty (it reads as `null` then), so existing policies keep their version. It is copied
  by `Clone()` and by `GuardrailsView.ClonePolicy`, and is part of `GuardrailStore.OptionalPolicyFields`.
- `GuardrailConfigProblemKind.InvalidTrustedScript` — `Value` names `serverId:path` (or `(empty)`).
- `Whiskers.Services.Guardrails.TrustedScripts` (static):
  - `string? CallPath(string command)` — the path of a trusted call shape (point 3), else `null`.
  - `bool IsValid(TrustedScript entry)` — point 1.
  - `string HashCommand(string path)` — `sha256sum -- '<path>'`.
  - `string? ParseHash(CommandResult result)` — the 64-hex hash of a successful run, else `null`.
  - `const string ResolvedKey = "__resolvedTrustedScript"`.
- `MutationTarget.TrustedScript` (`string?`, init) — the path the resolver verified. `MutationPolicy.WithResolvedTarget`
  passes it to the rules as `ResolvedKey`; a caller's own `__…` keys are dropped there, as before.
- `ProtectedResourceRule` — before any matching, a request whose `ResolvedKey` is listed in the policy and whose
  command is that trusted call is judged with the command's argument words in place of the command. Every request is
  then checked against the listed paths (point 5).

## D-1 — Second approver for additions (approved)

The draft said: "With *Require a second approver* switched on, a second Admin must approve the change." The first
build left this out; the maintainer asked for it on 20 November 2026. It reuses the existing approval flow instead of
a new one:

1. **What needs it.** A `guardrails.save` that **adds** an entry or **changes** an entry's hash, in any preset,
   compared with the stored configuration. Removing an entry narrows trust and needs no approval.
2. **When.** Only while the **stored, active** policy has *Require a second approver* on. With it off, a single Admin
   saves as today (single-admin installs), and the audit row records who did it.
3. **How.** The page passes the additions as the argument `trustedScriptsAdded` of `guardrails.save`: one line per
   entry, `preset | server | path | sha256`, sorted. A new rule `TrustedScriptApprovalRule`, in the human engine and
   the agent engine, answers `Confirm` when that argument is not empty and the policy requires a second approver.
   - The gateway raises an approval bound to the call's arguments, so to exactly these additions. The approver sees
     server, path and hash on the Approvals page.
   - With *Require a second approver* on, the requesting Admin cannot decide it (`ApprovalEligibility`). A second
     Admin must.
   - The first Admin then saves again with the same additions. The approved request is used once, and the save runs.
     Different additions raise a new request.
4. **Binding at run time.** The save recomputes the additions from the stored configuration and what it is about to
   write. If they differ from the approved argument (someone saved in between), nothing is written.
5. **No path finding on the argument itself.** For `guardrails.save` only, `ProtectedResourceRule` leaves
   `trustedScriptsAdded` out of its matching. Its paths are what the list protects, and they are not acted on. Every
   other argument and every other capability is matched as before.

Tests (once approved):
- With the setting on: adding an entry is pending approval, the requester cannot approve it, and after a second
  Admin approves, the retry saves it.
- Changing a hash also needs approval. Removing an entry does not.
- With the setting off, saving works as before.
- Other additions than the approved ones raise a new request.

## Tests

- A trusted call with the right hash runs, for an MCP key and for a scheduled task.
- A different hash refuses it ("changed since it was approved"). So does an unreadable hash, the listed path on
  another server, and a hash that changes between decision and run.
- A trusted call plus `$(…)`, `$VAR`, backticks, `; other-command` or `> file` is refused.
- `echo x > <listed path>`, `rm <listed path>` and `cp x <listed path>` are refused for a key and need confirmation from
  an interactive Admin.
- Invalid entries (relative path, `..`, an interpreter such as `/usr/bin/python3`, a bad hash, a duplicate) make the
  configuration invalid.
- An empty list leaves the stored policy, and its hash, unchanged.

# Contract 0017 — Trusted script paths per server

Status: **Draft**, not approved. Nothing in this contract is built yet. It needs the maintainer's approval first,
because it adds an exception to INV-24.

## Purpose

INV-24 refuses a command that runs code its text does not show. That includes a script called by its path
(`/usr/local/sbin/burgcloud-db-dump.sh`), another interpreter (`python3 -c …`), a shell without `-c`, a build tool and
a changing compose command. The guardrail cannot check what such code touches. The refusal is intended. Its reason
used to be wrong, though: it said "builds what it acts on at run time". Since `fix/guardrail-script-reason` the reason
reads "runs a script, an interpreter or a recipe whose content is not shown here".

The rule has a cost. A scheduled `CustomCommand` that runs an ops script never runs while the policy protects
anything. Examples are the BurgCloud DB dump and the disk usage check. Automation (the scheduler, an MCP key) gets
`Deny`, and only an interactive web Admin gets `Confirm`. Today the only workarounds are to give up the script or to
empty the policy. Both are worse.

## Proposal (to approve or reject)

1. **List per server.** The guardrail policy gets `TrustedScripts`: a list of `{ serverId, path, sha256 }` entries.
   `path` is absolute and normalised, with no glob and no `..`. `sha256` is the hash of the file's content at the time
   of approval.
2. **Who may change it.** Only an interactive web Admin (`IsInteractiveAdmin`). No MCP key, no automation and no
   agent may change it. Every change goes to the audit, with the path and hash, never the content. With *Require a
   second approver* switched on, a second Admin must approve the change.
3. **What becomes analysable.** Only this exact shape counts as analysable: a command whose only command word is a
   listed `path` on the request's `serverId`, followed by literal arguments only. "Literal" uses today's rules: no
   `$`, no backticks, no brace expansion, no glob, and no `;`, `|`, `&&` or redirection to another command. All words
   are still matched against protected names and paths as before. Anything else stays unanalysable: a second command,
   `sudo`/`env` prefixes the list does not name, or a run-time construct.
4. **Pin the content.** Right before the run, Whiskers computes the file's hash on the target with a fixed,
   non-configurable `sha256sum -- '<path>'` over the same executor. If the hash differs from the approved one, or
   cannot be read, the command is refused ("trusted script changed since approval"). This stops a script that was
   swapped through another path from slipping through.
5. **Protect the path.** Every listed path counts as a protected path for **writing**. A changing command (`rm`, `mv`,
   `chmod`, `tee`, `cp`, or a redirection `>`/`>>`) on it is a hit. Running it as the command word is not.
6. **No exception for interpreters.** `python3 -c`, `bash -c "$X"` and similar are not covered by this contract.

## Open questions for the maintainer

- Hash pinning (point 4) costs one extra call per run and requires re-approval after every script update. Accept that
  cost, or rely on point 5 alone?
- Should the redirection protection (point 5) also cover `>` in general? Today the target of a redirection is only a
  word, not a write.

## Tests (once approved)

- A listed path with the right hash and literal arguments runs. With a different hash it is refused. On another
  server it is refused.
- A listed path plus `$(…)`, `$VAR`, backticks or `; other-command` is refused.
- `echo x > <listed path>` and `rm <listed path>` are refused.
- Changing the list over MCP or as automation is refused.

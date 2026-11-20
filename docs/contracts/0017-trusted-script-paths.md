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

## Amendment 1 — review findings (approved 20 November 2026)

From the review of 20 November 2026 (findings M1, M2, L1, L2, L4). All changes make the contract stricter or make it
hold as written. None of them widens what can run.

- **A1 (M1, point 1 and point 4): only a real script can be trusted.**
  - A name denylist does not pin the code a general-purpose program runs from its arguments (`awk -f`, `tar
    --checkpoint-action=exec`, `rsync -e`, `java -jar`, `sed -f`, `nsenter`), nor does it stop a symlink with a
    harmless name.
  - The fixed read of point 4 therefore checks what the file is in the same call as the hash:
    `p='<path>'; [ -f "$p" ] && [ ! -L "$p" ] && [ "$(realpath -e -- "$p")" = "$p" ] && [ "$(head -c 2 -- "$p")" = '#!' ] && sha256sum -- "$p"`.
    That means a regular file, no symlink anywhere in its path, and starting with `#!`, so not an ELF program. If any
    part fails, there is no hash: the page cannot add the entry, and the gateway refuses the call (fail closed).
  - The same read is used when the page adds an entry, at the decision, and right before the run.
  - The name denylist stays as defence in depth and grows by `awk`, `csh`, `tcsh`, `rbash`, `nsenter`, `unshare`,
    `runuser`, `setpriv`, `java`, `dotnet`, `tar`, `rsync`, `sed`, `strace` and `gdb`.
- **A2 (M2, point 4): the resolver uses the deciding policy.** The resolver reads the list from
  `EffectivePolicy.Of(request.PinnedPolicy, store)`, the same policy the rules decide with (INV-23), never
  `IGuardrailStore.Current`. A trusted call under an AI trigger's pinned preset then works, and its write protection
  follows that preset's list.
- **A3 (L4, interfaces): the verified server must match.** `TrustedCallOf` honours the resolved key only when its
  server equals the request's resolved server (`__resolvedTargetId`); otherwise there is no trusted call.
- **A4 (L1, D-1 point 4): binding under the store's lock.**
  - `IGuardrailStore.SaveConfigAsync` takes the expected additions (`string? expectedTrustedAdditions`).
  - Under its lock, the store recomputes the additions against the configuration it holds. It refuses the save when
    they differ from the expected ones (when given).
  - It also refuses any save with additions while the held active policy requires a second approver and the
    additions were not passed in. So a save path that skips the gateway argument (e.g. preset activation) cannot add
    entries.
  - The page's own check before the save goes away.
- **A5 (L2, point 2): the audit row is written by the store.**
  - Under its lock, before it writes the file, the store writes the `guardrails.trusted_scripts` row with every entry
    added or removed (preset, server, path, hash) and the editor.
  - If the row cannot be written, the save is refused and nothing changes (fail closed).
  - This covers every save path, preset activation included. The page no longer writes the row.
- **Correction (point 3, interfaces).** The text said a trusted call is judged "with its argument words read as a
  command of their own". The build does it more strictly: the whole command is analysed as usual, and only the hidden-
  code verdict on its command word (and that word's own matching) is left out. Points 3 and the
  `ProtectedResourceRule` line mean that.

Tests for the amendment:
- A symlink, an ELF program, a file without `#!` and an unreadable file yield no hash, both on the page and at the
  gateway.
- A trusted call under a pinned preset that lists the entry runs. Under one that does not list it, the call is refused.
- A resolved key for another server is ignored.
- A save whose additions changed under the lock writes nothing. A save with additions and no expected additions,
  while the second approver is required, writes nothing.
- An audit row that cannot be written refuses the save. Preset activation that drops entries writes the row.

## Amendment 2 — second review (approved 20 November 2026)

From the review of 20 November 2026 on `139dd5b3...83cc6de6` (findings M1, L1, L2). All three make the contract
stricter. None of them widens what can run.

- **B1 (M1, point 5): the write protection follows every spelling of a listed script, not only its full path.**
  - Today a listed path is a finding only where the command spells it out in full, e.g. `rm /usr/local/sbin/x.sh`.
    These spellings get through:
    - a relative one: `cd /usr/local/sbin && cp /tmp/e x.sh`, also with `>`, `tee` or `sed -i`;
    - a directory as the target: `cp /tmp/x.sh /usr/local/sbin/`, `tar -xf a.tar -C /usr/local/sbin`;
    - an alias: `/proc/self/root/usr/local/sbin/x.sh`.
  - The hash pin still refuses the next call. But the protection is meant to keep the accepted window between the last
    hash read and the start hard to use.
  - New, for a changing request and only for the listed paths (built-in protected paths are judged as before):
    1. **File name.** A word whose last segment is a listed script's file name is a finding, whatever its directory,
       unless the line is read-only (see 2). This covers relative operands, `./x.sh`, `sbin/x.sh` and a source
       copied under its own name into the directory. The full path stays a finding for every command, as before.
    2. **Directory.** A word that names a listed script's directory, or any directory above it, is a finding. That
       covers an absolute word (`/usr/local/sbin`, `/usr`, `/`) and a relative one read against each of those directories
       (`sbin`, `local/sbin`, `../sbin`). So do `.` and `..`, since the working directory is not known.
       - The exception is a line whose commands are all read-only: `ls`, `cat`, `stat`, `head`, `tail`, `wc`, `du`,
         `df`, `file`, `sha256sum`, `md5sum`, `test`, `[`, `realpath`, `readlink` and `grep`. A plain look into the
         directory stays possible; `cd`, `cp`, `tar`, `unzip`, `install` and every other command do not.
    3. **Aliases.** A word starting with `/proc/<anything>/root`, `/proc/<anything>/cwd` or `/proc/<anything>/fd`
       makes the command unanalysable. This applies to every protected path, not only the listed ones, because the
       same alias reaches the Docker socket.
  - Known limit: a command that picks its target names itself without naming them is not caught. An example is
    `tar -xf a.tar` with no `-C` while the start directory happens to be `/`. The hash pin catches such a change before
    the next run, and closing the window entirely is the separate topic of the private copy.
  - Clarification after the third review (20 November 2026; the maintainer asked for all three fixes):
    - A line with an output redirection (`>`, `>>`, `>|`, `&>`) is never read-only, whatever its commands are. So
      `cat /tmp/x > ../usr/local/sbin/x.sh` is a finding.
    - An option value is read on its own in both forms: after `=` (`--target-directory=/usr/local/sbin`) and attached
      to a short option (`-C/usr/local/sbin`, `-d/usr/local/sbin`, which means the word without its first two
      characters).
    - A trusted call is not a finding of a policy pattern merely because the script's own path matches it (for
      example `*backup*` for `/opt/backup.sh`). The whole command is matched without its command word. Every argument
      word is still matched as before.
- **B2 (L1, D-1 point 4): the store checks that the second Admin really decided.**
  - The gateway decides with the setting *Require a second approver* as it is at decision time. Someone can switch
    the setting on between the decision and the store's write. A save decided without approval would then still go
    through.
  - New: the gateway's scope records which rules of its decision were met by a used approval. Under its lock, the
    store refuses a save with additions while the held active policy requires a second approver, unless the open
    scope is a `guardrails.save` whose approval covered `trusted-script-approval`.
  - The refusal text is "The second-approver setting changed while this save waited; save again." It counts as
    `TrustedScriptsNeedSecondApprover`.
- **B3 (L2, interfaces): only the resolver sets `MutationTarget.TrustedScript`.** At the start of `HostAsync`, the
  resolver clears a `TrustedScript` the caller set, before anything else reads the target.
- **Correction (A5).** The audit row covers every save path through the store. Restoring a self-backup replaces
  `guardrails.json` outside the store and writes no `guardrails.trusted_scripts` row. That has been true before this
  contract, and the restore has its own audit entry.

Tests for the amendment:
- With a listed script, these are findings for an MCP key:
  - the relative operand after `cd`, with `cp`, `>`, `tee` and `sed -i`;
  - `cp` into its directory;
  - `tar -C` and `unzip -d` on that directory;
  - `cd` into the directory;
  - `.` with a changing command.
- `ls` and `cat` on the directory are not findings.
- `/proc/self/root/…` makes the command unanalysable.
- Turning the setting on between the decision and the save writes nothing. With the approval used, the save writes.
- A `TrustedScript` that the caller set on the target is ignored.

## Amendment 3 — fourth review (approved 20 November 2026)

From the review of 20 November 2026 on `139dd5b3...a9b447b8`. All changes make the contract stricter. None of them
widens what can run.

The write protection (point 5, B1) matches spellings in free shell text. That list can never be complete. The guarantee
remains the hash pin: a changed script does not run. B1 and C1 are defence in depth for the window between the last
hash read and the start. That window is closed only by running a verified private copy, which is a separate topic.

- **C1 (M, B1): more spellings.**
  1. **`/proc` aliases.** The alias check reads every option value as well (after `=`, and after the first two
     characters of a short option). It also covers a thread's alias (`/proc/<x>/task/<y>/root|cwd|fd`).
     - A `cd` or `pushd` into `/proc`, or below it, makes the command unanalysable. This covers a relative operand
       whose normalised form starts with `proc` too.
  2. **`~`.** A word starting with `~` is a finding, like `.`, while a script is listed and the line is not
     read-only. Its directory is not known.
  3. **Globs.** A glob word whose last segment could match a listed script's file name (`x.s?`, `*.sh`) is a finding
     while the line is not read-only.
  4. **Renaming tools.** `rename`, `prename`, `perl-rename` and `file-rename` as a command make the line a finding
     while a script is listed. Their targets come from an expression.
- **C2 (L1, A1): the fixed read refuses a hard link and a writable file.** The read of point 4 also requires one link
  (`stat -c %h` = 1), owner root (`stat -c %u` = 0), and no write bit for group or others. Otherwise there is no hash,
  and the call fails closed. The BurgCloud scripts (root, 700) meet this.
- **C3 (L2, point 1): no line break at the end.** The path, hash and call patterns are anchored at the true end of the
  text (`\z`). So an entry with a trailing line break is invalid.
- **C4 (L3, D-1 point 4): the store compares the approved argument too.** Within a `guardrails.save` scope, the call's
  `trustedScriptsAdded` (empty when absent) must equal the additions the store computes under its lock. If it does
  not, the save is refused as `TrustedScriptsChangedMeanwhile`.
- **C5 (L4, page): a stale page cannot add back what someone else removed.**
  - The Guardrails page keeps the configuration it loaded. It computes its additions against that, not against the
    store's current one: what the Admin added on this page.
  - The store compares them with its own additions (A4). An entry another Admin removed meanwhile is then an addition
    for the store only. The save is refused as `TrustedScriptsChangedMeanwhile`, and the page asks to reload.
  - After a successful save, the page takes the stored configuration as its new starting point.
- **C6 (L5): documentation.** The misplaced XML summary goes back to `RecordChange`.

Tests for the amendment:
- For an MCP key, these are findings or unanalysable:
  - `-C/proc/1/root/…` and `--directory=/proc/self/root/…`;
  - `/proc/1/task/1/root/…`;
  - `cd /proc && … self/root/…`;
  - `-C ~/scripts`;
  - `cp /tmp/e x.s?`;
  - `rename .new '' /usr/local/sbin/x.sh.new`.
- A hard link, a file not owned by root, and a group- or world-writable file yield no hash. The real-shell test runs in
  Linux CI.
- An entry with a trailing line break is invalid.
- An approved save whose argument differs from the store's additions writes nothing.
- A page loaded before another Admin removed an entry cannot save it back.
- On the page, a failed hash read adds no entry and shows the error.

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

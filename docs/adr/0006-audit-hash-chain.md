# ADR-0006: Tamper-evident audit trail — SHA-256 chain with HMAC checkpoints

- **Status:** Accepted (2026-10-30) — chain + keyed checkpoints with a key derived from `VAULT_KEY`, chosen by the
  maintainer for R1-23
- **Date:** 2026-10-30
- **Author:** LupusMalusDeviant
- **Context documents:** [`docs/v1/SECURITY_INVARIANTS.md`](../v1/SECURITY_INVARIANTS.md) (INV-49, INV-50),
  [`docs/v1/WORK_PACKAGES.md`](../v1/WORK_PACKAGES.md) (R1-23), [ADR-0005](0005-mutation-gateway.md)

## Context and problem

The audit trail (`AuditLog`) is a plain table. Anyone with write access to the database — a leaked PostgreSQL
password, a copied SQLite file that is put back, a careless manual fix — can change, add or remove rows, and nothing
notices. Retention hard-deletes rows older than 90 days without a trace (TA-35, B-20).

Two properties of the table shape the design:

- **Rows change after they are written.** A gateway event is written as its *intent* before the mutation runs and is
  turned into its *outcome* afterwards (ADR-0005, R1-19); `AuditRecovery` settles intents whose outcome was lost.
- **Several writers.** The gateway sink, `AuditLogService` (sign-ins, downloads, legacy rows), recovery, retention.

**Core question:** How does a verifier tell, from the database plus something the database alone cannot forge, that
no event was modified, inserted, removed from the middle or cut off at the end — while rows legitimately change from
intent to outcome and old rows are legitimately pruned?

## Requirements

- Detect modification, insertion, deletion in the middle and truncation of the end (INV-49).
- Standard primitives only (SHA-256, HMAC-SHA-256, HKDF); no own cryptography.
- Retention stays possible, is configurable, is itself recorded, and leaves the remaining chain verifiable (INV-50).
- Works on SQLite and PostgreSQL; no extra service.
- A writer that fails must not leave a row without its chain entry (no false alarms after a crash).

## Options

**A — Hash column on each row.** `Hash = SHA256(prevHash ‖ row)`. Breaks on every intent → outcome update (the hash
of a later row would no longer match), and an unkeyed chain can simply be recomputed by whoever edits the table.

**B — Append-only chain of row versions + keyed checkpoints (chosen).** A separate table `AuditChain` gets one entry
per written *version* of an audit row: `RowHash = SHA256(canonical row)`, `Hash = SHA256(prevHash ‖ seq ‖ auditId ‖
rowHash)`. The entry is appended in the same transaction as the row write, under one process-wide lock, so the
chain is a single sequence. Checkpoints `(seq, hash, time)` are signed with HMAC-SHA-256 under a key derived with
HKDF from `VAULT_KEY`, stored in the database **and** in a file next to it (`audit-checkpoint.json` in the data
directory).

**C — External anchoring** (a transparency log, an SIEM, WORM storage). Strongest, but needs a service the
self-hosted product does not have; out of scope for R1 (WORK_PACKAGES R1-23).

## Decision

Option B.

- **Canonical row.** Every column of `AuditLogEntity` by name in ordinal order, as compact JSON; null columns are left
  out (a *nullable* column added later does not change the hash of rows that never had it — a new column must be
  nullable without a default, and no computed property may be added; `AuditHashChainTests` pins the column list); timestamps as whole microseconds
  since 0001-01-01 UTC (both providers store at least that). `RowHash` is the lower-case hex SHA-256 of that JSON;
  `Hash = SHA256("{prevHash}|{seq}|{auditId}|{rowHash}")`, the first `prevHash` being 64 zeros.
- **Signed links.** With `VAULT_KEY` set, every link also carries `Mac = HMAC-SHA-256(key, "link|{hash}")`. A
  database writer can compute a link's SHA-256 but not this, so a link appended outside Whiskers — for instance a
  forged "new version" of an old row, which the verifier would otherwise take as that row's latest state — is
  reported at once and is never signed over by the next checkpoint. Links written before the key was adopted are
  covered by the genesis checkpoint written at adoption (the moment of trust); every link after it must be signed.
- **Changing a row.** A row is changed only through `UpdateAsync`, which locks it (`FOR UPDATE` on PostgreSQL; the
  SQLite write transaction holds the database lock) and checks it against its newest link — the same row hash and,
  with the key, a valid link signature — before the change. A row changed in the database is refused, so the
  application never turns someone else's edit into an authenticated next version (completion, recovery). A plain
  `WriteAsync` that changes a row with a link fails.
- **Verification** walks the chain from its anchor: each entry's `PrevHash` must be the previous entry's `Hash`, and
  each `Hash` must recompute. Every audit row must have a latest chain entry whose `RowHash` matches the row now;
  every chain entry must name a row that exists, unless that row was pruned by a recorded retention. The newest
  trusted checkpoint (valid MAC; the later of the database's and the file's) must lie on the chain with the same
  hash, and the chain must not end before it. Verification reads one database snapshot (a row and its link are always
  both in it or both out); the file is read first, so it never names a link the snapshot cannot have.
- **Retention** (configurable, default 365 days, minimum 30; run daily): one transaction first verifies the whole
  trail and removes nothing when it is not intact, incomplete, or key adoption is pending (else a tampered or
  backdated prefix would be cut and its end signed as the new start); then it writes an `audit.retention` event
  (counts, boundary), deletes the rows up to an id boundary and the chain prefix that only names such rows, and writes
  a signed *retention checkpoint* holding the new anchor (seq, hash) and the pruned id boundary. Rows past the
  boundary keep their entries; entries after the anchor that name pruned rows are allowed. The MCP call history
  (`McpToolCalls`) shares the retention period and is removed in the same recorded transaction — never by a plain
  delete elsewhere.
- **Upgrade.** Rows written before R1-23 have no chain. On the first start the existing rows are sealed in id order
  and a checkpoint marks this genesis: they are protected from then on, not proven untouched before.
- **Readers.** Audit data is Admin only (INV-50): the Audit log and Agent History pages, and `GET /audit/export`,
  which records an `audit.export` event before it reads anything and returns the verification result, the checkpoint
  file as read, the checkpoints, chain and rows from one snapshot — enough to re-check the chain with SHA-256 alone
  (and its signatures, given the key). Findings carry a code and arguments, so the page shows them in the UI's
  language; the English sentences stay in logs and the export.
- **PostgreSQL migration.** The SQLite→PostgreSQL copy renumbers audit rows, so the chain is not copied: PostgreSQL
  seals the copied rows as a new genesis, and the old checkpoint file is kept under another name.
- **Without `VAULT_KEY`** the chain still works, but checkpoints cannot be signed: the verifier reports
  "checkpoints unsigned", trusts no checkpoint, and the trail is **not tamper-evident against a database writer** —
  rows and links can be rewritten consistently, an unsigned retention checkpoint can hide deletions, and a truncated
  end goes unnoticed. The chain then only catches inconsistent edits.

## Consequences

- **Detects:** any change of a sealed row, a row added without its entry, an entry removed from the middle (broken
  link), and — up to the newest trusted checkpoint — a cut-off end.
- **Limitation (host compromise):** whoever controls the host running Whiskers has `VAULT_KEY`, the checkpoint file
  and the database together and can rewrite all three consistently. The design protects against database-only
  tampering (a leaked database credential, a restored or edited database file), not against a compromised host. An
  external anchor (option C) is the later answer.
- **Window:** events after the newest checkpoint can be cut off undetected by whoever can write the database. With
  `VAULT_KEY` they cannot be altered or added there (links are signed); without it they can, and the next checkpoint
  (every minute and at shutdown) signs what is there then.
- **Key stretching.** The signed checkpoints sit in the database, so the checkpoint key is the passphrase stretched
  with PBKDF2-SHA-256 (600,000 rounds, as the vault) and then separated with HKDF: a leaked database tests VAULT_KEY
  guesses no faster than the vault does. VAULT_KEY must still be a strong, generated secret.
- **Findings expire with retention.** Once retention moves the start past a cut, that cut is no longer reported:
  verify at least once per retention period.
- **Restore.** A self-backup restore rolls the trail back to the archive's state, and the restore's own event lived
  in the database it replaced. The restore therefore leaves a note (who, which archive, when) that the next start
  writes as a chained `selfbackup.restored` event; the events after the archive was taken survive only in the
  pre-restore safety backup.
- Each audit write takes the chain lock and a transaction: audit writes are serialized. Their rate is low (human and
  automation actions), and the gateway already writes one row at a time per operation.
- A database restored from an older backup verifies as truncated against the checkpoint file: restoring self-backup
  also restores the checkpoint file (both live in the data directory). The backup takes the file *before* the database
  copy, so the restored database always reaches the restored checkpoint.
- Rows of a chain emptied while its checkpoint file remains are not sealed again (that would launder the trail): new
  events start a fresh chain, and verification keeps reporting the old rows and the cut-off end. A restore of an
  archive without a checkpoint (from before the chain) moves the live checkpoint file aside, so a legitimate restore
  is sealed as a new genesis.
- Checkpoints written while `VAULT_KEY` was not set carry no signature; once a key is set they are not trusted, but
  not reported as tampering either. At the first start with a key, when no checkpoint anywhere (database or file)
  carries a signature, the newest unsigned retention anchor is signed once — the same moment of trust as a genesis —
  so a trail pruned before the key was set still reaches its start. The signed anchor and the genesis boundary are
  written in one transaction, so an interrupted start never leaves one without the other. Never again once any
  signature exists.
- **Changing `VAULT_KEY`** invalidates every existing checkpoint signature: the trail then reports invalid signatures
  until a rotation path exists (R1-28).
- A checkpoint file that cannot be read makes a verification *incomplete* (reported, not counted as tampering).
- Pruning also removes the head checkpoints before the new start; retention and genesis checkpoints are kept. Only the
  newest 60 head checkpoints stay in the database (an older one adds nothing: a rewritten middle changes every later
  hash). A retention run removes at most 10,000 rows, so the chain lock is held briefly; the next daily run continues.
- **The file is never signed over.** A checkpoint replaces the file only while the database trail still reaches what
  the file names (the same hash at its link, or a trusted retention start at or past it). A database cut short, wiped
  or rebuilt below the file keeps the file — and the finding — for good; an error is logged.
- A trail that was signed is not pruned while `VAULT_KEY` is missing (its new start could not be signed).
  Likewise a process without the key never thins signed head checkpoints nor replaces a signed checkpoint file.
- **Fail closed without the key.** On a trail that was signed (a signed checkpoint in the database or the file), a
  process without `VAULT_KEY` refuses to append links: an unsigned link there could not be told apart from a forged
  one. The audit write fails, so the mutation gateway refuses mutations until the key is set again (INV-01/INV-49);
  reads keep working.
- The canonical JSON uses System.Text.Json's default escaping (non-ASCII and HTML-sensitive characters as upper-case
  `\uXXXX`), pinned in code.

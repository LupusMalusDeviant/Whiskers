# Rotating `VAULT_KEY`

`VAULT_KEY` (or the file `VAULT_KEY_FILE` names) protects everything Whiskers keeps secret: the vault, the protected
database fields (webhook secrets, rollback snapshots), the data-protection key ring, the sealed SSH and mTLS client keys
and the audit trail's signatures. Contract 0015 Part B lets you replace it in place. Whiskers re-seals and re-signs
everything at the next start, all or nothing, and keeps a journal so a crash at any point is safe.

## Before you start

- **Test first.** Rotate a test instance before production. Do not start with Badwolf.
- **Keep the old key.** Store it in the password manager. Keep it there until a self-backup taken **after** the
  rotation has been restored once. Older self-backups stay sealed with the old key (see below).
- **One process.** Run one Whiskers process on the data directory, as always (INV-45). The rotation runs at start,
  before anything else, so nothing races it.
- **The audit trail must verify.** The rotation refuses when the trail does not verify completely with the current key
  (Audit page → Verify). Find the cause first: a rotation would otherwise re-sign what cannot be proven.
- **The schema must be current.** After an upgrade, start once with the current key alone, then rotate.

## Rotate

1. Write the new key to a new secret file, for example `/opt/whiskers/secrets/vault_key_new` (mode 0600, owned by
   the user that runs Whiskers). Use a long random value, e.g. `openssl rand -base64 48`.
2. Leave `VAULT_KEY_FILE` as it is (the current key) and set `VAULT_KEY_NEW_FILE` to the new file, e.g. in the compose
   file or `.env`: `VAULT_KEY_NEW_FILE=/run/secrets/vault_key_new` (and mount the secret).
3. Restart Whiskers. The start log shows `[vault-rotation]` lines:
   - a `pre-rotation` self-backup sealed with the **current** key,
   - the files re-sealed beside their originals (`*.rotating`) and read back,
   - one database transaction (protected fields re-sealed, audit links and checkpoints re-signed),
   - the switch, then a `post-rotation` self-backup under the **new** key,
   - `VAULT_KEY rotated: … Replace VAULT_KEY_FILE with the new key and remove VAULT_KEY_NEW_FILE.`
4. Check:
   - the Audit page verifies the trail (it is re-signed under the new key) and shows one `vault.key_rotated` row
     (`system:startup`, counts only);
   - the servers answer (mTLS hosts are green, an SSH host's terminal opens);
   - the readiness check shows **"VAULT_KEY-Rotation abschließen"**: the next step is still open.
5. Replace the content of the `VAULT_KEY_FILE` file with the new key (or point `VAULT_KEY_FILE` at the new file).
   Then remove `VAULT_KEY_NEW_FILE` and restart once more. The readiness line goes away.
6. Restore the `post-rotation` self-backup once on a test instance. After that the old key may leave the password
   manager.

A start with both keys still set after a completed rotation changes nothing. So does a start where `VAULT_KEY_FILE`
already holds the new key and `VAULT_KEY_NEW_FILE` is still set.

## If something goes wrong

The journal `vault-rotation.json` in the data directory records the phase. It holds no secret.

| Where it stopped | What the next start does |
|---|---|
| Before the database commit (check, safety copy, prepared files, inside the transaction) | Nothing was switched. The old key still opens everything. The prepared `*.rotating` files are discarded and the rotation starts over while `VAULT_KEY_NEW_FILE` is set. Without it Whiskers runs on the old key as before. |
| After the commit (or during the switch) | The database is under the new key. Whiskers **refuses to start without `VAULT_KEY_NEW_FILE`**. Set it again and restart: the switch finishes. |
| Between the commit and its journal entry | Decided by the database itself: anything that still opens under the old key means "not committed" (start over), otherwise the switch finishes. |

A refusal names its reason in the start log and changes nothing: a wrong current key, a trail that does not verify, a
pending migration, a vault entry or sealed key that does not open with the current key.

## The way back

- **Before you replace `VAULT_KEY_FILE`:** the rotation is complete; the old key no longer opens the data directory.
  To go back, restore the `pre-rotation` self-backup with the **old** key (`VAULT_KEY_FILE` = old key,
  `VAULT_KEY_NEW_FILE` removed). See [backup-and-recovery.md](backup-and-recovery.md).
- **A start with the old key alone after the rotation:** the vault reports the key as wrong (INV-54), the readiness check
  names the rotation, and every mTLS host is unreachable because the client keys are sealed (D10). Set the new key.
- **The new key is lost:** restore the `pre-rotation` backup with the old key. Everything changed since then is lost. If
  no backup can be restored, the sealed client keys cannot be opened. Use the break-glass re-onboarding per host with a
  transient SSH password ([per-host-mtls-migration.md](per-host-mtls-migration.md), "Undo"). It needs no stored key.

## Older self-backups

Self-backups taken before the rotation are **not** re-sealed (D9). The backup list marks them **"earlier key"**. To
restore one, set `VAULT_KEY_FILE` to the key it was sealed with for that restore. The restored data directory is then
under that key.

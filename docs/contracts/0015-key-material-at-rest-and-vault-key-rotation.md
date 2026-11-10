# Contract 0015 — Private keys at rest and `VAULT_KEY` rotation (INV-52)

Status: **Accepted** · 10 November 2026 by the maintainer, with the decisions D1–D10 as recommended (see
*Decisions of the maintainer* at the end, Q1–Q8).

**Protected layer.** Part A changes how Whiskers stores and loads the **mTLS client keys** — part of the mTLS layer that
only an explicit approval may change (contract 0007, `common` rules). With Q1 the maintainer **approved** plan steps 2
and 4 in that layer, limited to how the client key is stored and loaded (`IMtlsClientKeyLoader`,
`DockerConnectionManager.BuildMtlsCredentials`, `MtlsClientProbe`, `MtlsClientCredentialStore`, the start migration).
Every step that touches the layer is marked **[mTLS layer]** below and in the plan; nothing on a managed host, in
ghostunnel's flags, in the server-certificate validation, in the rollout's phases or in the socket-proxy verbs changes.

**Correction of contract 0007 D3.** Contract 0007 D3 kept the per-host client keys as plain files on the assumption that
"the key must be written out for the Docker client". That assumption is wrong (see *Current state*: the Docker client
takes an in-memory `X509Certificate2`); this contract replaces it. The rest of 0007 D3 (one slot per issue, file names,
modes, the record as the authority) stands.

## Purpose

INV-52 (`docs/v1/SECURITY_INVARIANTS.md` §9):

> Every credential for managed infrastructure (cloud, DNS, SMTP, chat, AI provider, git tokens, webhook secrets) is
> encrypted at rest by the vault.

R1-29, R1-30 and R1-31 moved cloud keys, the DNS token, notification and AI credentials and webhook secrets into the vault
(or under a vault-derived key). INV-52 is ⚠️ for two reasons (`docs/v1/RELEASE_GATE.md`, *Open invariants*): "SSH private
keys and mTLS client keys are stored in plaintext; no in-place `VAULT_KEY` rotation". This contract closes both:

- **Part A — keys at rest.** The SSH private keys and every mTLS client key Whiskers keeps in its data directory are stored
  sealed with a vault-derived key and only ever exist in plaintext in memory (mTLS) or for the duration of one use in a
  memory-backed file (SSH).
- **Part B — rotation.** `VAULT_KEY` can be replaced in place: everything sealed with the old key is re-sealed with the new
  one, all or nothing, without losing a credential or the audit trail's signatures.

Threat model (unchanged from R1-29–31): the protection is against a copy of the data directory or of an unencrypted
self-backup without the key (`VAULT_KEY_FILE` lives outside the data directory, `deploy/install.sh:446`). It is not
protection against a compromised Whiskers process or a host root that can read the key file.

## Current state (verified at `9a12f7a3`)

### SSH private keys

- Uploaded on the Servers page (`Components/Pages/Servers.razor:705`, `:722`) and written as given by
  `ServerConfigService.SaveSshKeyAsync` to `ssh-keys/{serverId}/{fileName}` with mode 0600 on Unix
  (`Services/ServerConfig/ServerConfigService.cs:265-296`; `DataPathOptions.SshKeysDir`, `Configuration/DataPathOptions.cs:113`).
  No vault is involved; nothing refuses an upload without a vault.
- Read only as a **path** handed to the external `ssh` process (`-i <path>`): the Docker tunnel
  (`Services/Docker/SshTunnelManager.cs:31`, `:133-140`), host commands (`Services/Server/HostCommandExecutor.cs:145`, with a
  ControlMaster socket under `Path.GetTempPath()/whiskers-ssh`, `:37-38`) and the web terminal
  (`Services/Terminal/TerminalSessionManager.cs:68`, `:94`). `GetSshKeyPath` returns the file in the data directory
  (`ServerConfigService.cs:311-318`).
- Removed by `DeleteSshKeyAsync` (`:320-340`), e.g. after onboarding moved a host to mTLS (`Services/Onboarding/OnboardingService.cs:260`).

### mTLS client keys

- **Per host** (R1-44, contract 0007): generated in-process and written as PKCS#8 PEM to
  `mtls/hosts/{id}/g{n}-s{k}/client.key`, 0600 in 0700 directories (`Services/Certificates/MtlsClientCredentialStore.cs:184`,
  `WriteAtomic` `:420-441`). `KeyFiles()` lists them for the readiness check (`:344`).
- **Shared** `mtls/client.key` (CN `serverwatch-client`), made by hand with step-ca and never written by Whiskers
  (`MtlsClientCredentialStore.cs:52-53`, contract 0007 "Current state").
- Read by exactly two places, both as PEM files: the Docker connection (`Services/Docker/DockerConnectionManager.cs:363-366`,
  `X509Certificate2.CreateFromPemFile` then a PKCS#12 round trip) and the rollout's probe
  (`Services/Certificates/MtlsClientProbe.cs:46`, a static class without access to the vault). Everything else only
  compares paths or checks `File.Exists` (`MtlsClientCredentialStore.cs:84-105`, `MtlsClientRollout.cs:461`, `:702-703`,
  `:718`, `OnboardingService.cs:196-200`, `:233`, `:299`).
- `ServerConfig.TcpClientKeyPath` can name any path; contract 0007 keeps operator-supplied paths supported unchanged
  (`Models/ServerConfig.cs:47-49`).
- Contract 0007 D3 chose plain files and named encryption as the alternative, with the remark "the key must be written
  out for the Docker client". The code above shows that it need not: the Docker client takes an in-memory
  `X509Certificate2`.

### Backups and the readiness check

- Both directories are in every self-backup (`Services/Backup/SelfBackupLayout.cs:68-69`) and restored with owner-only
  modes (`:112`, `RestoreBootHandler.cs:223`). With `VAULT_KEY` the archive is sealed; without it, it holds the keys in
  plaintext.
- The readiness check's vault line names "the private SSH keys (ssh-keys/) and all mTLS client keys" as stored
  unencrypted (`Services/Setup/ProductionReadinessService.cs:93-102`); a separate line checks the mTLS key modes (`:501-514`).
- `SecretsAtRestTests` scans every file of the data directory for configured credentials (`src/Whiskers.Tests/SecretsAtRestTests.cs:87`);
  it does not cover key files.

### What depends on `VAULT_KEY` today

| Consumer | How | Where |
|---|---|---|
| `vault.json` entries and key check | PBKDF2-SHA256 (600 000) over a per-vault salt → AES-256-GCM | `Services/Vault/VaultService.cs:24`, `:79-142` |
| Protected fields (`f1:`): webhook secrets, rollback snapshots in the database | HKDF from the vault's master key, AES-GCM bound to a purpose | `VaultService.cs:123`, `:395-432`; `Services/Vault/ProtectedFields.cs:13-30` |
| Data-protection key ring (`keys/`) | each key's secret sealed by the vault | `Services/Vault/VaultKeyRing.cs:14-20` |
| Audit chain signatures (links and checkpoint) | PBKDF2 over a fixed salt, then HKDF → HMAC-SHA256 | `Services/AuditLog/AuditChain.cs:122-155`, `:213-238`; a signed trail refuses writes without the key (`:376`) and names a key that does not match (`:605-606`) |
| Self-backup archives | PBKDF2 over a per-archive salt → framed AES-GCM | `Services/Backup/BackupArchiveCipher.cs:9-15`, `:44-45` |

All read the passphrase through `SecretSource.VaultKey` (`Configuration/SecretSource.cs:41`). A changed key is detected
and refused at start (INV-54, `VaultKeyState.Wrong`), but nothing can move the data from one key to another: the README
tells the operator to delete an *empty* vault to re-key it (`Services/Vault/README.md`, "An empty vault").

## Boundaries (fixed)

- **No crypto of our own.** Sealing uses the existing `IVaultService.ProtectField` / `TryUnprotectField` (AES-256-GCM,
  purpose-bound, ADR-0001); rotation re-derives with the existing KDFs. No new algorithm, no new key format.
- **The mTLS layer changes only where Whiskers stores and loads its client keys** [mTLS layer]. Validation of the server
  certificate (`DockerConnectionManager.cs:390-405`), the CA, ghostunnel's flags, the rollout's phases and path checks,
  the socket-proxy verbs and every host-side file stay as they are. `PerHostCredentialTests` and
  `PerHostCredentialShellTests` pass unchanged.
- **Fail closed.** A key that cannot be unsealed (vault off for a sealed key, wrong key, moved or altered file) is not
  used: the connection, tunnel or terminal fails with a reason that names it; nothing falls back to another key.
- **No secret is logged**, not even as a prefix; a plaintext key never reaches a file in the data directory again, and
  never a host.
- **Storage is additive.** No schema change. Re-sealing existing values is a data rewrite of the same columns, as
  `ProtectedFieldMigration` already does (R1-31).
- **Single instance**, as documented for webhooks (INV-45): rotation assumes one Whiskers process on its data directory.

## Types and interfaces

### Sealed key files (Part A)

A sealed key file keeps its **name and place** (`ssh-keys/{id}/{fileName}`, `mtls/…/client.key`); only its content
changes from PEM to one line `f1:<base64>` (the existing `ProtectedFields.Prefix`). Purpose binding:

| File | Purpose |
|---|---|
| SSH key | `ssh-key:{serverId}/{fileName}` |
| mTLS client key (per host or shared) | `mtls-client-key:{path relative to MtlsDir, '/' separated}` |

Because name and place stay, every path comparison and `File.Exists` of the rollout and onboarding is untouched (D3).

```csharp
// Services/Vault/SealedKeyFiles.cs (new)
public interface ISealedKeyFiles
{
    /// <summary>Seals <paramref name="pem"/> for <paramref name="purpose"/> and writes it atomically (tmp + rename, 0600).
    /// Refuses (SealedKeyException, nothing written) when the vault is not usable (D4).</summary>
    void Write(string path, string purpose, ReadOnlySpan<char> pem);

    /// <summary>The key's PEM in memory. A sealed file is opened with its purpose; a plaintext PEM inside the data
    /// directory is read and reported (until the start migration seals it); a plaintext PEM outside the data directory
    /// is an operator-managed key (D6). Throws SealedKeyException with a reason for anything else.</summary>
    SealedKeyMaterial Read(string path, string purpose);
}

/// <summary>Plaintext key in a pinned buffer, zeroed on Dispose.</summary>
public sealed class SealedKeyMaterial : IDisposable { public ReadOnlySpan<char> Pem { get; } public KeySource Source { get; } }
public enum KeySource { Sealed, PlaintextInDataDirectory, OperatorManaged }
```

### mTLS loading [mTLS layer]

```csharp
// Services/Certificates/IMtlsClientKeyLoader.cs (new)
public interface IMtlsClientKeyLoader
{
    /// <summary>The client certificate with its private key, built in memory from the server's paths (cert PEM as today,
    /// key through ISealedKeyFiles), as the PKCS#12 round trip DockerConnectionManager does now. Throws with a reason.</summary>
    X509Certificate2 Load(Models.ServerConfig server);
}
```

`DockerConnectionManager.BuildMtlsCredentials` and `MtlsClientProbe.ProbeAsync` take the loader instead of calling
`CreateFromPemFile`; the probe gets it as a parameter (it is static today). `MtlsClientCredentialStore.IssueCoreAsync`
writes the key through `ISealedKeyFiles.Write` instead of `WriteAtomic(…, secret: true)`. Nothing else in the store, the
rollout or onboarding changes.

### SSH use (Part A)

```csharp
// Services/Server/ISshKeyLease.cs (new)
public interface ISshKeyLeases
{
    /// <summary>Unseals the server's key into a fresh file in a memory-backed directory (D2) and returns its path; the
    /// file is deleted when the lease is disposed. Null when the server has no key.</summary>
    Task<SshKeyLease?> AcquireAsync(Models.ServerConfig server, CancellationToken ct);
}
public sealed class SshKeyLease : IAsyncDisposable { public string Path { get; } }
```

- Lease directory: `/dev/shm/whiskers-ssh-<process id>/` (0700), file 0600, random name. At start the leases of earlier
  processes are removed. The directory must be on `tmpfs`/`ramfs` (read from `/proc/self/mountinfo`); otherwise SSH
  with a stored key is refused and the readiness check names it (D2).
- Lifetime: tunnel — until `WaitForPortAsync` reports the forward up (ssh has authenticated) or the process ended;
  host command — until the process exits (with ControlMaster, the master's start); terminal — until the session ends.
- `GetSshKeyPath` stays for "is there a key" checks; the three consumers above use a lease instead of the path.

### Start-up migration (Part A)

`KeyMaterialMigration : IInitializable` (order 56, right after `CredentialVaultMigration` 55, `Services/Vault/CredentialVaultMigration.cs:48`):
for each plaintext key file in `ssh-keys/` and `mtls/` (shared and per host): seal, write `*.sealing`, read it back and
open it, compare with the original in memory, then rename over the original. All or nothing per file; an interrupted run
finishes at the next start. Without a usable vault it changes nothing and counts the files for the readiness check (as
R1-29). The `.tmp`/`.sealing` leftovers are removed. SQLite/PostgreSQL are not involved.

### Rotation (Part B)

```csharp
// Services/Vault/VaultKeyRotation.cs (new) — runs before the host is built (Program.cs:34, after a pending restore)
public static class VaultKeyRotationBootHandler
{
    /// <summary>When VAULT_KEY_NEW_FILE (or VAULT_KEY_NEW) is set: verifies the current key opens the vault, then
    /// re-seals every consumer of the inventory with the new key (see "Rotation steps"). Returns the key to run with.</summary>
    public static RotationResult Apply(DataPathOptions paths, IConfiguration configuration, TextWriter log);
}

/// <summary>Every reader of SecretSource.VaultKey and what it seals; a test pins that the list matches the code.</summary>
public static class VaultKeyConsumers { public static IReadOnlyList<VaultKeyConsumer> All { get; } }
```

Rotation steps (journal `vault-rotation.json` in the data directory, no secret in it):

1. **Check.** The current key is `Verified` against `vault.json`; the new key differs; the schema is current (no pending
   migration — otherwise: start once with the current key first); the audit trail verifies completely with the current
   key. Any failure: nothing changes, the start stops with the reason (D8).
2. **Safety copy.** A `pre-rotation-…` self-backup sealed with the **current** key, as the pre-upgrade backup (S-09).
3. **Prepare files.** Every file consumer is written re-sealed beside its original as `*.rotating`: `vault.json` (new salt,
   new key check, every entry), the key ring files in `keys/`, the sealed key files of Part A, the audit checkpoint file.
   Each is read back and opened with the new key.
4. **Database, one transaction.** Every `f1:` field (webhook secrets, rollback snapshots) re-sealed, every audit link and
   checkpoint re-signed with the new key, and one audit row `vault.key_rotated` (principal `system:startup`, contract
   0014) naming counts, not values. SQLite then `secure_delete` + WAL checkpoint, PostgreSQL `VACUUM FULL` on the touched
   tables (as `ProtectedFieldMigration`).
5. **Switch files.** Each `*.rotating` is renamed over its original; previous generations (`vault.json.bak` and the like)
   are replaced by the new generation, never left sealed with the old key.
6. **Done.** The journal records completion; the process runs on with the new key; the log and the readiness check say:
   "replace `VAULT_KEY_FILE` with the new key and remove `VAULT_KEY_NEW_FILE`". A later start with both set and the vault
   already under the new key does nothing.

A crash before step 4's commit leaves every original intact (the `*.rotating` files are discarded at the next start); a
crash after it resumes at step 5 from the journal. A start with the old key alone after a completed rotation finds the
vault `Wrong` (INV-54) and the readiness check names the rotation.

Self-backups made before the rotation stay sealed with the old key (D9).

## Decisions (D1–D10, recommendation first — all accepted as recommended)

- **D1 — Scope of INV-52.** Keep both parts in INV-52 (the gate row names both); INV-52 becomes ✅ only after Part B.
  Parts A and B are separate packages and pull requests. *Alternative:* INV-52 ✅ after Part A, and rotation becomes a new
  invariant INV-63 ("`VAULT_KEY` can be replaced in place without losing a credential or a signature"), ⚠️ until Part B —
  the record is more precise, but the highest pinned ID rises and 1.0 needs INV-63 as well.
- **D2 — SSH keys in use.** A lease file in a memory-backed directory (`/dev/shm`), refused when that directory is not
  `tmpfs`/`ramfs`. *Alternative:* an in-process `ssh-agent` per Whiskers process, keys added from memory
  (`ssh-add -` from standard input, to be verified against the image's OpenSSH) and selected by public key with
  `IdentitiesOnly=yes` — no plaintext file at all, but a long-running helper process, an agent socket in every ssh child's
  environment (`SecretEnvironment.Scrub` must keep it) and more moving parts in the terminal path.
- **D3 — Sealed file format.** Same name and place, content `f1:<base64>` with a purpose bound to the file's place, so the
  rollout, onboarding and path checks of contract 0007 stay untouched [mTLS layer: content only]. *Alternative:* a new
  file name (`client.key.sealed`) and new paths in `servers.json` — clearer on disk, but every path comparison of the
  rollout changes.
- **D4 — New keys without a usable vault.** Refused, as every R1-29–31 save is: an SSH key upload, onboarding, a client
  migration or renewal that would write a key needs `VAULT_KEY` (the default install sets `VAULT_KEY_FILE`,
  `deploy/install.sh:446`). *Alternative:* write plaintext when no key is configured (`NotConfigured`) and refuse only
  when the key is `Wrong`; the readiness check reports it. Onboarding then keeps working on installs without a key, and
  INV-52 holds only where a key is set.
- **D5 — Legacy plaintext keys.** Sealed at start when the vault is usable (all files, shared key included); without a
  usable vault they stay usable and are counted by the readiness check (as R1-29). *Alternative:* refuse to use any
  plaintext key in the data directory once a vault is configured — stricter, but a vault outage would cut every mTLS host.
- **D6 — Keys outside the data directory.** A `TcpClientKeyPath` or SSH path outside the data directory is
  operator-managed (contract 0007 keeps such paths supported): used as today, never moved, named by the readiness check as
  "operator-managed, not sealed by Whiskers", and INV-52's evidence says the invariant covers the keys Whiskers stores.
  *Alternative:* refuse such paths once a vault is configured.
- **D7 — Rotation trigger.** At start, before the host is built, when `VAULT_KEY_NEW_FILE` (or `VAULT_KEY_NEW`) is set
  next to the current key — no CLI, works with compose secrets, one process by construction (nothing else runs yet).
  *Alternative:* a host CLI `--rotate-vault-key --new-key-file <path>` run with Whiskers stopped (a one-shot container);
  simpler to reason about, but "stopped" cannot be checked from inside and a second process could race it.
- **D8 — Audit trail before rotation.** Rotation refuses unless the whole trail verifies with the current key; it then
  re-signs every link and the checkpoint with the new key, and the `vault.key_rotated` row is signed with the new key.
  *Alternative:* key epochs — old links stay signed with the old key, which must then be kept (as a secret) to verify
  them, defeating the rotation.
- **D9 — Older self-backups.** Not re-sealed; they stay restorable with the key they were sealed with (set
  `VAULT_KEY_FILE` to it for the restore). The backup list marks archives sealed with another key, and a fresh
  self-backup under the new key is made right after the rotation. *Alternative:* re-seal every kept archive during the
  rotation — longer, more disk, and a failure halfway leaves a mixed set.
- **D10 — Wrong key and mTLS.** With sealed client keys, a wrong or missing `VAULT_KEY` makes every mTLS host
  unreachable (today it only locks the vault's secrets). Accepted as fail-closed; the start log, the readiness check and
  the server pages name "client keys sealed, vault locked" as the cause, and `docs/operations/per-host-mtls-migration.md`
  plus the vault README describe the way back (the right key; else break-glass re-onboarding per host with a transient
  SSH password, which needs no stored key). *Alternative:* keep the mTLS client keys outside the vault (Part A for SSH
  keys only) and record INV-52 for mTLS keys as an accepted exception.

## Tests (acceptance)

- **`KeyMaterialAtRestTests`** (new): after an SSH upload, an onboarding, a per-host migration, a renewal and a rotation
  of a client credential, and after the start migration, no file of the data directory (`.bak`, `.tmp`, `.sealing`,
  `.rotating` included) and no unencrypted self-backup taken with `VAULT_KEY` contains a PEM or OpenSSH private-key
  header or any 24-character window of the probe keys' base64 body.
- **`SealedKeyFilesTests`** (new): round trip; a file moved to another slot or server (other purpose) is refused; an altered
  file is refused; a locked or wrong vault refuses with a named reason; a write without a usable vault writes nothing
  (D4); plaintext inside the data directory is read as `PlaintextInDataDirectory`, outside as `OperatorManaged` (D6); the
  buffer is zeroed on dispose.
- **`MtlsClientKeyLoaderTests`** (new) [mTLS layer]: the loaded certificate presents the right key (TLS handshake against
  the `TestCa` fixture); the existing `PerHostCredentialTests` and `PerHostCredentialShellTests` pass with sealed keys and
  unchanged assertions (host A's credential refused at host B, old generation refused after rotation, shared CN refused
  after narrowing).
- **`SshKeyLeaseTests`** (new): the lease file is 0600 in a 0700 directory on `tmpfs`, gone after dispose, after a failed
  start of ssh and after a crash (removed at the next start); a non-memory directory refuses (D2). Linux only — a
  *Platform skips* row with "Runs unskipped in `ci.yml build-test / Test (in-process)`".
- **`SshKeyLeaseImageTests`** (new, image runtime suite): in the built image, `ssh -i <lease>` against a test sshd
  authenticates and the lease is removed afterwards (`image-runtime.txt`).
- **`KeyMaterialMigrationTests`** (new): legacy plaintext SSH keys, per-host and shared mTLS keys are sealed at start, each
  read back before it replaces the original; an interrupted run finishes; without a usable vault nothing changes and the
  readiness check counts them; a wrong key changes nothing.
- **`VaultKeyRotationTests`** (new): every consumer of `VaultKeyConsumers.All` opens with the new key and no longer with the
  old; a fault injected after each step leaves either the old state complete (before the commit) or resumes to the new
  state (after it); a trail that does not verify refuses and changes nothing (D8); a start with the old key alone after
  rotation is `Wrong` and names the rotation; previous generations are not left under the old key.
- **`VaultKeyConsumersTests`** (new): reads the source — every reader of `SecretSource.VaultKey` and every use of the
  vault's master or field key is in `VaultKeyConsumers.All`.
- **`PostgresVaultRotationTests`** (new, Docker suite): step 4 on real PostgreSQL, including the rollback on a fault and
  `VACUUM FULL` (`docker.txt`).
- `ProductionReadinessServiceTests`: the vault line no longer names SSH or mTLS keys as plaintext once they are sealed,
  names counts while legacy plaintext remains, names operator-managed paths (D6) and a pending or completed rotation.
- `SecretsAtRestTests` keeps passing; its data-directory scan is extended to the key directories.

## Gate change

- `SECURITY_INVARIANTS.md` INV-52: with D1 as recommended, ✅ after Part B; the evidence drops "Still open" and names this
  contract, D6's scope and D10's consequence; the test column adds `KeyMaterialAtRestTests`, `SealedKeyFilesTests`,
  `MtlsClientKeyLoaderTests`, `SshKeyLeaseTests`, `KeyMaterialMigrationTests`, `VaultKeyRotationTests`,
  `VaultKeyConsumersTests`, `PostgresVaultRotationTests`, `SshKeyLeaseImageTests`. Until Part B lands, the row stays ⚠️
  and the *Open invariants* reason reads "no in-place `VAULT_KEY` rotation".
- §11 counts: ✅ 55 → 56, ⚠️ 7 → 6. With D1's alternative: INV-52 ✅ after Part A, a new INV-63 ⚠️ (highest pinned ID
  62 → 63 in `RELEASE_GATE.md`, a row under *Open invariants*), counts ✅ 56, ⚠️ 7, total 63.
- `RELEASE_GATE.md`: the INV-52 row leaves *Open invariants*; one *Platform skips* row for `SshKeyLeaseTests` on Windows.
- `.github/required-tests/in-process.txt`, `docker.txt`, `image-runtime.txt`: generated blocks regenerated from the
  `.actual` files.
- Documentation: `SECURITY.md` (`:111` no longer "plaintext, protected only by file permissions"), ADR-0001 (rotation),
  `Services/Vault/README.md` (sealed key files, rotation, the empty-vault re-key note replaced), `Services/Certificates/README.md`,
  `docs/operations/per-host-mtls-migration.md` (D10's way back), a new `docs/operations/vault-key-rotation.md`,
  `docs/operations/backup-and-recovery.md` (D9), CHANGELOG (Unreleased): behaviour changes D4 and D10.
- INV-56 (self-reports) is not claimed by this contract; its readiness lines change here and stay German until INV-56's
  own package.

## Plan (steps and triage)

Part A — keys at rest

1. [Claude-prüfbar] `ISealedKeyFiles` with purposes; `SealedKeyFilesTests`.
2. [Claude-prüfbar, **mTLS layer** — needs the maintainer's go] `IMtlsClientKeyLoader`; `DockerConnectionManager` and
   `MtlsClientProbe` load through it; `MtlsClientCredentialStore` writes sealed keys; `MtlsClientKeyLoaderTests`, the
   per-host suites unchanged.
3. [Claude-prüfbar] `ISshKeyLeases` and the three SSH consumers; upload refused without a usable vault (D4);
   `SshKeyLeaseTests`.
4. [Claude-prüfbar, **mTLS layer** for the client keys] `KeyMaterialMigration` at start; `KeyMaterialMigrationTests`,
   `KeyMaterialAtRestTests`; readiness lines.
5. [Claude-prüfbar] `SshKeyLeaseImageTests` in the image runtime suite (runs in CI on amd64 and arm64).
6. [braucht Nutzer] Deploy Part A: check in the start log that every key of Badwolf's data directory was sealed, that all
   mTLS hosts still answer (server pages green, readiness "mTLS-Proxy läuft") and that an SSH host's terminal opens.

Part B — rotation

7. [Claude-prüfbar] `VaultKeyConsumers` and `VaultKeyConsumersTests`.
8. [Claude-prüfbar] `VaultKeyRotationBootHandler` with journal, safety copy, file preparation, database transaction and
   switch; `VaultKeyRotationTests`, `PostgresVaultRotationTests`.
9. [Claude-prüfbar] Readiness, backup list marks (D9), documentation, gate records, required-test manifests; full test
   run with the lock.
10. [braucht Nutzer] A real rotation on a test instance (not Badwolf first): new key file, restart, check the audit trail
    verifies, the hosts answer, a new self-backup restores; then replace the key file and restart once more. Only then on
    Badwolf, with the old key kept in the password manager until a self-backup under the new key has been restored once.

## Decisions of the maintainer (10 November 2026)

All eight questions were decided as recommended.

- **Q1 — Implementation in the protected mTLS layer (steps 2 and 4): approved**, limited to how the client key is stored
  and loaded (`IMtlsClientKeyLoader`, `DockerConnectionManager.BuildMtlsCredentials`, `MtlsClientProbe`,
  `MtlsClientCredentialStore`, the start migration). The host side, the server-certificate validation, ghostunnel's flags,
  the rollout's phases and the socket-proxy verbs stay unchanged.
- **Q2 — Rotation inside INV-52 (D1):** both parts in INV-52; ✅ only after both.
- **Q3 — SSH keys in use (D2):** lease file in `/dev/shm`, refused without a memory-backed directory.
- **Q4 — New keys without `VAULT_KEY` (D4):** refused (SSH upload, onboarding and client migration need the vault).
- **Q5 — Keys outside the data directory (D6):** still allowed as operator-managed, reported, outside INV-52's scope.
- **Q6 — Rotation trigger (D7):** `VAULT_KEY_NEW_FILE` (or `VAULT_KEY_NEW`) at the next start.
- **Q7 — Older self-backups (D9):** not re-sealed; marked and restorable with their own key, and a fresh backup is made
  right after the rotation.
- **Q8 — Wrong key cuts mTLS (D10):** accepted as fail-closed, with the cause named and the way back documented.

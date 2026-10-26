# Audit D: Secrets, Vault, Persistence, Migrations, Backup/Restore, Update/Rollback, Configuration

Repo: repository @ `498e70d`. Read-only audit; evidence was checked against the source. Anything I could not confirm from code is marked **(uncertain)**.

## Summary

The cryptography is solid. The vault uses AES-256-GCM with PBKDF2 at 600k iterations and a persisted salt. Self-backups use a framed, streaming AES-GCM format with AAD that binds version, frame index and the final flag. MCP keys are stored as SHA-256 hashes and compared in constant time.

The weak point is coverage, not the crypto. Most high-value secrets never reach the vault: cloud provider API tokens, the DNS token, SMTP, Telegram/Matrix/ntfy tokens, webhook URLs and secrets, and the AI/agent API keys. They sit as plaintext JSON in the data dir, next to the unencrypted DataProtection key ring. The vault only protects git tokens, registry credentials, kubeconfigs and user-entered container secrets.

Restore validates archive containment (no zip-slip, no symlinks) but has no entry allowlist. A crafted or foreign archive can therefore replace `backups/`, which wipes every self-backup, including the pre-restore safety backup. A broken restored DB then crash-loops with no automatic way back.

## Secrets inventory

| Secret | Storage location | Protection |
|---|---|---|
| Container/user secrets (Settings → Vault) | `vault.json` | AES-256-GCM, PBKDF2-SHA256 600k over a persisted 16-byte salt (`VaultService.cs:59-66,197-213`) |
| Git deploy tokens | `vault.json` key per app (`GitDeployService.cs:86-88`) | Vault. Fails closed without VAULT_KEY (throws) |
| Registry credentials (password) | `vault.json` (`RegistryConfigService.cs:59-61`). Username/host in `registries.json` | Vault. Fails closed |
| Kubeconfig | `vault.json` `kubeconfig:{id}` (`Servers.razor:475-487`, `KubernetesClientCache.cs:57-60`) | Vault. Fails closed |
| **Hetzner/Hostinger cloud API key** | `servers.json` → `ServerConfig.CloudApiKey` (`ServerConfig.cs:87`, `Servers.razor:491-497`) | **Plaintext JSON** |
| SSH private keys | `ssh-keys/<serverId>/<file>` (`ServerConfigService.cs:201-217`) | Plaintext file, chmod 0600 after write. Filename sanitized with `Path.GetFileName` |
| SSH bootstrap password | Memory only, `[JsonIgnore]` (`ServerConfig.cs:64-68`) | Not persisted (good) |
| mTLS client key (shared) | `mtls/client.key` (`OnboardingService.cs:198-200`) | Plaintext PEM. Permissions are left to the operator |
| **DNS provider API token** | `app-settings.json` section `Dns` (`Settings.razor:875-880`) | **Plaintext JSON** |
| **SMTP password, Telegram bot token, ntfy token, Matrix access token, Slack/Discord/Mattermost webhook URLs, outbound webhook HMAC secret** | `app-settings.json` (`Settings.razor:784-788, 918-947`, via `AppSettingsStore.cs:37-41`) | **Plaintext JSON** |
| **AI chat API key** | `app-settings.json` `AiChat.ApiKey` (`Settings.razor:904-913`) | **Plaintext JSON** |
| **Agent LLM API key** | `agent-settings.json` (`AgentSettingsStore.cs:12,40`; the comment admits it is plaintext) | **Plaintext JSON**, admin-only write |
| Claude Code agent MCP key | env/config `Agent:ClaudeCode:McpKey`. At run time a temp file in `/tmp` (`ClaudeCodeRuntime.cs:138-160`) | Env plaintext. Temp file 0600 and deleted afterwards |
| MCP API keys | `mcp-permissions.json` `KeyHash` (`McpPermissionService.cs:100-131`) | SHA-256 of a 256-bit random key with `FixedTimeEquals`. Legacy plaintext is migrated and the old files purged (`:76-120`) |
| Inbound webhook secrets (HMAC) | DB `Webhooks.Secret` (`UpdatePolicy.cs:56`) | Plaintext column (needed for HMAC) |
| Local user passwords | DB `AspNetUsers` (Identity) | ASP.NET Identity PBKDF2 hash |
| **Container env in rollback snapshots** | DB `UpdateRollbacks.ConfigJson` (`ContainerLifecycleOperations.cs:208-221`) | **Plaintext** (full `Config` incl. `Env`) |
| DataProtection key ring (signs/encrypts auth cookies) | `keys/*.xml` (`WhiskersHostingExtensions.cs:41-45`) | **Unencrypted at rest** (no `ProtectKeysWith*`) |
| Tailscale node state / NetBird config | `tailscale/tailscaled.state`, `netbird/config.json` (`entrypoint.sh:25-28`) | Plaintext (tool-managed) |
| DB connection string (Postgres password) | env `WHISKERS_DB_CONNECTION` or `WHISKERS_DB_CONNECTION_FILE` (`DatabaseRegistration.cs:25-27`) | File secret supported |
| OIDC/Google client secrets, Harbor password, Tailscale/NetBird auth keys | env (`.env.example`) | Env plaintext |
| VAULT_KEY | env only (`VaultService.cs:36`, `BackupService.cs:48`, `docker-compose.yml:36`) | No `_FILE` variant. Visible in `docker inspect` |
| Self-backup archives (contain everything above, incl. `keys/`) | `backups/self/*.tar.gz[.enc]` | AES-GCM only if VAULT_KEY is set. Otherwise plaintext (`BackupService.cs:122-135`) |

## Findings

### D-01: Most high-value secrets are stored as plaintext JSON outside the vault
- **Severity:** High · **Category:** Security · **v1-blocker:** yes
- **Evidence:**
  - Cloud key: `src/Whiskers/Models/ServerConfig.cs:84-87`; `Components/Pages/Servers.razor:491-497`; consumed raw in `Services/Cloud/CloudControlService.cs:50,100,117,131`.
  - Notification, DNS and AI secrets: `Components/Pages/Settings.razor:784-788, 875-880, 904-947` → `Services/Persistence/AppSettingsStore.cs:37-41` (plain `JsonSerializer.SerializeToNode`).
  - Agent key: `Services/Agent/AgentSettingsStore.cs:12, 40`.
  - Misleading readiness text: `Services/Setup/ProductionReadinessService.cs:50-55` ("Secrets werden verschlüsselt gespeichert" whenever VAULT_KEY is set).
- **Impact:**
  - Anyone who can read the data volume, a plaintext backup, or a host snapshot gets: Hetzner project tokens (power off, rebuild, delete servers), the DNS API token (domain takeover), SMTP credentials, bot tokens, webhook secrets and LLM keys.
  - Setting VAULT_KEY does not change this.
  - The architecture rules (ADR-0001) promise encrypted secrets, so operators get a false sense of protection.
- **Recommendation:**
  - Store every secret-typed setting as a vault reference: `CloudApiKey`, `DnsSettings.ApiToken`, `EmailSettings.Password`, `TelegramSettings.BotToken`, `MatrixSettings.AccessToken`, `NtfySettings.Token`, the `Webhook*` URL/Secret, the `AiChat`/`Agent` ApiKey, and the Slack/Discord/Mattermost webhook URLs.
  - Either add a custom `IConfigurationSource` that decrypts `vault:` references, or move these values out of the IOptions layer.
  - Migrate existing plaintext values on first boot with VAULT_KEY set, the same way the MCP hash migration works.
  - When the vault is disabled, refuse to save secrets (fail closed), as registries and git already do.
  - Fix the readiness text.

### D-02: Restore applies arbitrary archive entries; a crafted or foreign archive can wipe all backups, including the pre-restore safety backup
- **Severity:** High · **Category:** Reliability / Security · **v1-blocker:** yes
- **Evidence:**
  - `Services/Backup/BackupArchiver.cs:90-111`: the only checks are entry type and containment, with no allowlist of top-level names.
  - `Services/Backup/RestoreBootHandler.cs:70-71` swaps **every** staged directory. `:102-111` `ReplaceDirectory` runs `Directory.Delete(target, recursive)` and then moves.
  - The class comment claims "NEVER touches `backups/` (never in staging)" (`RestoreBootHandler.cs:18`), but that is only true for archives Whiskers produced itself (`BackupService.cs:450-456`).
  - Unencrypted archives are accepted even when VAULT_KEY is set, with no authenticity check (`BackupService.cs:358-366, 369`).
  - No integrity check of the staged `metrics.db` (no `PRAGMA integrity_check`, no trial open).
  - `manifest.Provider` and the migration lists are self-declared by the archive (`BackupService.cs:405-423`).
  - Crafted `metrics.db-wal` entries can be paired with the restored DB, depending on enumeration order (`RestoreBootHandler.cs:73-87`).
- **Impact:**
  - An archive with a `backups/…` entry deletes `backups/self/*`, including the pre-restore safety backup that was just written (`BackupService.cs:296`).
  - An archive with a corrupt or incompatible `metrics.db` (or JSON) makes the next boot throw in `DatabaseInitializer` or a store `InitializeAsync`. The container then crash-loops.
  - The pre-restore backup can only be restored through the running UI, so recovery means manual file surgery.
  - Restore is admin-only (`SelfBackupPanel.razor:226`), so this is mainly a robustness and social-engineering risk ("restore this backup I sent you").
- **Recommendation:**
  - Allowlist top-level restore targets: the known JSON stores, `metrics.db`, `keys/`, `ssh-keys/`, `mtls/`, `chat/`, `agent-chat/`, `tailscale/`, `netbird/`. Always reject `backups/`, `.restore-*`, `*-wal`/`*-shm` and `*.tmp`.
  - Validate the staged DB (open it and run `PRAGMA integrity_check` and the migration-history check) before committing the marker.
  - Optionally sign or HMAC plaintext archives, or require an explicit "untrusted plaintext archive" confirmation.
  - Add boot-time auto-revert: if startup after a swap fails N times, re-stage the pre-restore backup.

### D-03: A wrong or changed VAULT_KEY is not detected; the vault silently runs with mixed keys
- **Severity:** Medium · **Category:** Security / Reliability · **v1-blocker:** no (strongly recommended)
- **Evidence:**
  - `Services/Vault/VaultService.cs:43`: `IsEnabled` is true whenever a key is set.
  - `:55-66`: no key-check value or canary is stored in `VaultData`.
  - `:150-167`: `GetSecret` logs the error and returns `null`.
  - `:113-147`: `SetSecretAsync` happily encrypts new entries under the new key.
  - Readiness reports green (`ProductionReadinessService.cs:50-55`).
  - Legacy CBC migration with a wrong key: `:73-82` re-encrypts whatever `DecryptLegacyCbc` returns. With a wrong key, PKCS7 padding validates by chance about 1 in 256 times, and the original ciphertext is then overwritten with authenticated garbage (permanent loss).
  - No key-rotation path. The PBKDF2 salt is per vault (`:59-62`).
- **Impact:** After a redeploy with a mistyped key, registry pulls fall back to anonymous, git deploys fail, and the k8s client throws "kein kubeconfig". New secrets then get written under the wrong key, which makes later recovery harder.
- **Recommendation:**
  - Persist a GCM-encrypted verifier, e.g. `KeyCheck = Encrypt("whiskers-vault-v1")`.
  - On mismatch, fail closed: mark the vault unhealthy, refuse `Set`, surface a readiness and health failure, and skip migration.
  - Add a key-rotation command (re-encrypt with a new passphrase and salt).
  - Back up `vault.json` before the CBC migration.

### D-04: DataProtection key ring is unencrypted at rest and is a session-forgery credential
- **Severity:** Medium · **Category:** Security · **v1-blocker:** no
- **Evidence:**
  - `Startup/WhiskersHostingExtensions.cs:41-45`: `PersistKeysToFileSystem` without `ProtectKeysWith*`. On Linux the XML keys are stored in clear.
  - The keys protect the auth cookie (`WhiskersAuthenticationExtensions.cs:57-62`, 24 h).
  - `keys/` is included in every self-backup (`BackupService.cs:450-489`; asserted in `Whiskers.Tests/BackupServiceTests.cs:112`).
- **Impact:** Reading `keys/` (volume access, or a plaintext backup) lets an attacker mint a valid cookie for any email, including the admin. Roles are resolved by email from `roles.json`.
- **Recommendation:**
  - Encrypt the key ring with an `IXmlEncryptor` keyed from a VAULT_KEY-derived subkey (HKDF with a distinct label), or with a certificate.
  - Set directory mode 0700.
  - Document that `keys/` equals admin access.

### D-05: Self-backups are plaintext when VAULT_KEY is unset and live on the same volume as the data
- **Severity:** Medium · **Category:** Security / Operations · **v1-blocker:** no
- **Evidence:**
  - `Services/Backup/BackupService.cs:122-135` (encryption is optional), `:152-153` (warning only).
  - Archives are stored under `backups/self` inside the data root (`Configuration/DataPathOptions.cs:91-94`).
  - Default compose runs as root with no umask hardening. Files get default mode (uncertain: 0644).
  - Download endpoint is admin-gated (`Startup/WhiskersPipelineExtensions.cs:275-290`, OK).
- **Impact:**
  - A plaintext archive holds all D-01 secrets plus `keys/`, `ssh-keys/` and the `vault.json` ciphertext.
  - Keeping backups on the same disk/volume gives no protection against volume loss.
- **Recommendation:**
  - Require VAULT_KEY for self-backup, or an explicit "allow plaintext" switch.
  - Add an off-box target (S3/SFTP/rclone) or at least document copying them off.
  - Write archives with mode 0600.

### D-06: On PostgreSQL, self-backup contains no relational data, so restore produces a config/DB split
- **Severity:** Medium · **Category:** Operations / Commercial readiness · **v1-blocker:** no (yes if Postgres is marketed as production-ready)
- **Evidence:**
  - `Services/Backup/SelfBackupManifest.cs:222-224`; `BackupService.cs:80-95`: only SQLite gets `VACUUM INTO`.
  - Restore swaps files only (`RestoreBootHandler.cs:65-89`).
  - No pg_dump tooling for Whiskers' own DB. The only pg_dump references are fleet scripts under `deploy/borg-fleet/`.
  - Backup README does not mention self-backup at all.
- **Impact:** Local users, webhooks (secrets), schedules, rollback snapshots, audit and update history stay at "now" while JSON config, the vault and DP keys revert. Operators may believe they have a full backup.
- **Recommendation:**
  - Either integrate `pg_dump`/`pg_restore` (or documented external tooling), or block restore on Postgres and show a prominent "files only" warning in the UI and manifest.

### D-07: No pre-migration backup or downgrade path; a failed migration crash-loops
- **Severity:** Medium · **Category:** Reliability · **v1-blocker:** no
- **Evidence:**
  - `Services/Persistence/DatabaseInitializer.cs:38-43` (LogCritical + rethrow).
  - `Startup/WhiskersPipelineExtensions.cs:444-457`: no backup before `MigrateAsync`, and the Identity migrate is not wrapped.
  - `grep pre-upgrade|pre-migrat` → none.
  - The legacy baseline heal → stamp sequence is not transactional (`DatabaseInitializer.cs:51-62`), though each step is idempotent and covered by `DbMigrationBaselineTests` (OK).
- **Impact:** After a bad upgrade the DB may be partially migrated. Rolling the image back yields a schema that is newer than the code. The only recovery is a manual self-backup restore, and that needs a running app.
- **Recommendation:**
  - Take an automatic `VACUUM INTO` snapshot when `GetPendingMigrations()` is non-empty.
  - Print a clear recovery runbook on failure.
  - Consider a `--restore-latest-self-backup` CLI that works without booting.

### D-08: Migration parity and model drift are not guarded by tests
- **Severity:** Low · **Category:** Testing · **v1-blocker:** no
- **Evidence:**
  - The two providers currently have parallel migration sets: Sqlite `src/Whiskers.Migrations.Sqlite/2026*_{InitialCreate,AddUpdateRollback,AddMcpCallCorrelationId,AddSelfMetricSamples,AddActionOutcomes}` and Postgres `src/Whiskers.Migrations.Postgres/Migrations/…` with the same names, plus Identity in both.
  - No `HasPendingModelChanges` test and no name-parity test (grep found none).
  - `PostgresSmokeTests.cs:23-25` skips silently without Docker. CI `ubuntu-latest` has Docker, so it runs there (`.github/workflows/ci.yml:3-4`).
- **Recommendation:** Add tests asserting `!db.Database.HasPendingModelChanges()` for both providers and both contexts, and parity of migration names (minus the timestamp).

### D-09: JSON store robustness (corruption bricks boot; no fsync; some non-atomic writers)
- **Severity:** Medium · **Category:** Reliability · **v1-blocker:** no
- **Evidence:**
  - `Services/Persistence/JsonFileStore.cs:31-32`: `JsonSerializer.Deserialize` throws on a corrupt file. Callers' `InitializeAsync` (vault, servers, roles, whitelist, MCP permissions) are not guarded, and `RunWhiskersStartupAsync` has no catch (`WhiskersPipelineExtensions.cs:437-438`).
  - `:45-48`: atomic tmp+rename, but no `Flush(true)`/fsync of the file or directory, and no `.bak`.
  - Concurrency is a per-instance `SemaphoreSlim` (`:8`). That is fine because each path has one store instance (verified via `grep "new JsonFileStore<"`).
  - Non-atomic direct writes: `Services/ImageUpdate/ImageUpdateStore.cs:65`, `Services/Cve/CveFindingsStore.cs:58`. Their loads are try/catch-tolerant.
  - `Services/Agent/AgentSettingsStore.cs:47-49`: no lock, and a fixed `.tmp` name shared by concurrent saves.
  - Cross-store writes are not transactional (e.g. vault set, then `registries.json`: `RegistryConfigService.cs:59-71`).
- **Impact:**
  - A torn write after power loss (no fsync) or a manual edit makes the app refuse to boot.
  - A crash between the vault write and the JSON write leaves orphan secrets (benign).
- **Recommendation:**
  - fsync the tmp file before rename.
  - Keep `<file>.bak` (the previous generation) and fall back to it on parse failure, with a loud warning.
  - Make `ImageUpdateStore`, `CveFindingsStore` and `AgentSettingsStore` use `JsonFileStore`.

### D-10: The AutoRollback policy is persisted (default ON) but never executed
- **Severity:** Medium · **Category:** Commercial readiness / UX · **v1-blocker:** yes (feature claims must match behavior)
- **Evidence:**
  - `Services/Persistence/DatabaseInitializer.cs:305` (`AutoRollback … DEFAULT 1`); `Services/AutoUpdate/AutoUpdateService.cs:216` (saved).
  - `:162-171`: the failure path contains `// TODO: Rollback would go here — requires storing old image reference`, even though a snapshot is captured at `:137`.
  - Manual rollback exists (`:287-311`). Old images are kept for 24 h (`ImageUpdate/OldImageCleanupPolicy.cs:23-27`).
- **Impact:** A failed auto-update leaves the container on the broken image, even though the policy says to roll back automatically.
- **Recommendation:** When `policy.AutoRollback` is set, call `RollbackAsync` on the captured snapshot in the catch block. Otherwise hide or remove the flag.

### D-11: Rollback snapshots store full container config (incl. Env secrets) in plaintext and are replayed verbatim
- **Severity:** Medium · **Category:** Security · **v1-blocker:** no
- **Evidence:**
  - `Services/Docker/Operations/ContainerLifecycleOperations.cs:208-221` (`Config = inspect.Config`, `HostConfig`).
  - `Whiskers.Data/Entities/UpdatePolicy.cs:45`.
  - Replay in `RollbackContainerAsync` (`:224ff`).
  - Included in self-backups and in the Postgres DB.
- **Impact:**
  - Container environment secrets (DB passwords, API keys) are duplicated into the Whiskers DB in clear text.
  - Anyone who can write to the DB can inject `HostConfig` (Privileged, Binds `/:/host`), which is then executed on the next rollback.
- **Recommendation:**
  - Encrypt `ConfigJson` with the vault, or store only a digest and re-read the env at rollback time.
  - Validate or deny-list dangerous `HostConfig` deltas compared with the live container.

### D-12: SecretRedactor has limited coverage and is not applied at the logging layer
- **Severity:** Low · **Category:** Security · **v1-blocker:** no
- **Evidence:**
  - `src/Whiskers/Utils/SecretRedactor.cs:14-59`. It is called at 10 sinks only: MCP call log `McpCallLogMiddleware.cs:126`, HostCommandExecutor, SshTunnelManager, agent transcript/approvals/guardrail, VPN runner.
  - Patterns it misses:
    - Webhook URLs with path tokens (Slack `hooks.slack.com/services/T/B/x`, Discord `/api/webhooks/<id>/<token>`).
    - Telegram `bot<id>:<token>` in the URL path (`TelegramNotificationService.cs:46`).
    - Unkeyed provider tokens (`ghp_…`, 64-char hcloud tokens).
    - Space-separated `--password x` and `-p x`.
  - `-p\S+` over-redacts (`-port`).
  - Mitigation: `System.Net.Http.HttpClient` logging is at `Warning` (`appsettings.json`). The notification services log only the event type (OK).
- **Recommendation:**
  - Add URL-path token patterns, known token prefixes and space-separated flag forms.
  - Consider a Serilog/MEL enricher or redaction (`Microsoft.Extensions.Compliance.Redaction`) at the logger level.

### D-13: VAULT_KEY handling: env only, no `_FILE`, no strength check
- **Severity:** Low · **Category:** Security / Operations · **v1-blocker:** no
- **Evidence:**
  - `Services/Vault/VaultService.cs:36`, `Services/Backup/BackupService.cs:48`, `docker-compose.yml:36`, `docker-compose.hardened.yml:91`.
  - Compare `WHISKERS_DB_CONNECTION_FILE` (`DatabaseRegistration.cs:25`) and `WHISKERS_ADMIN_PASSWORD_FILE`, which do support files.
  - The placeholder in `.env.example` would be accepted.
  - A missing `WHISKERS_DB_CONNECTION_FILE` silently falls back to the env var or the config value (`DatabaseRegistration.cs:87-90`) instead of failing.
- **Recommendation:**
  - Support `VAULT_KEY_FILE`.
  - Enforce a minimum length (e.g. 16 or more characters) and reject known placeholders.
  - Fail when a `*_FILE` variable is set but the file is unreadable.

### D-14: Restore operational edges (restart dependency, disk-fill, merge semantics)
- **Severity:** Low · **Category:** Reliability / Operations · **v1-blocker:** no
- **Evidence:**
  - Restore relies on an external restart: `_lifetime.StopApplication()` (`BackupService.cs:334`). Without `restart:` or systemd, the app stays down (compose has `unless-stopped`, OK).
  - Upload is capped only at 2 GiB (`SelfBackupPanel.razor:206`). There is no cap on decompressed size in `BackupArchiver.InspectAsync`/`ExtractAsync` (`:41-88`), so a gzip bomb fills the data volume that live SQLite/JSON writes share.
  - Swap only overwrites entries that exist in staging (`RestoreBootHandler.cs:70-88`). Live files absent from the archive survive, so a restore to an older point mixes old and new stores.
  - A crash mid-encrypt leaves a partial `.tar.gz.enc` with no sidecar (`BackupService.cs:126-131`). It is invisible in the list and never pruned.
  - Uncertain: file modes after the swap depend on `TarEntry.ExtractToFileAsync` and `File.Copy` preserving mode. I believe .NET does this on Unix. If not, `ssh-keys` would become 0644 and SSH would refuse them.
- **Recommendation:**
  - Cap the total extracted size and entry count.
  - Clear allowlisted targets that are absent from the archive.
  - Write encrypted output to a `.tmp` file and rename it.
  - Explicitly chmod `ssh-keys/` and `keys/` after the swap.

### D-15: SQLite → Postgres migrator is not transactional
- **Severity:** Low · **Category:** Reliability · **v1-blocker:** no
- **Evidence:** `Services/Persistence/SqliteToPostgresMigrator.cs:129-136` (aborts on a non-empty target) and `:205-214` (per-batch `SaveChanges`, no outer transaction).
- **Impact:** A mid-copy failure leaves a half-filled target, and a re-run aborts. The operator must drop and recreate the DB manually.
- **Recommendation:** Wrap the copy in a single transaction (Postgres handles large transactions), or add `--reset-target`.

### D-16: Vault and backup-restore test gaps
- **Severity:** Low · **Category:** Testing · **v1-blocker:** no
- **Evidence:**
  - Vault tests are limited to `Whiskers.Tests/ConcurrencyCacheAliasingTests.cs:71-110` (set/get/list/concurrency).
  - No tests for: wrong key, tampered ciphertext, legacy CBC migration, salt persistence across restarts, or a disabled-vault `Set` throwing.
  - No restore test with crafted top-level entries (`backups/`, `*-wal`) or a corrupt `metrics.db`. Existing: `BackupArchiverTests.cs:75,92`; `RestoreBootHandlerTests.cs:29-98`.
- **Recommendation:** Add those cases, especially for D-02 and D-03.

### D-17: Documentation drift
- **Severity:** Low · **Category:** Documentation · **v1-blocker:** no
- **Evidence:**
  - `Services/Vault/README.md` links `../ConfigExport/`, which does not exist.
  - `Services/Backup/README.md` documents only volume backups, not self-backup, the cipher or restore.
  - `docs/adr/0002-…md`: the status says "UMGESETZT" (implemented) while the Decision section still says "Option 2 — zurückgestellt" (deferred).
  - The comment in `VaultService.cs:13` hard-codes `/app/data`.
- **Recommendation:** Update the READMEs and ADR-0002. Add an operator doc covering what is and is not encrypted (see the inventory above).

## Specific questions answered

- **VAULT_KEY unset:**
  - The vault is disabled (`VaultService.cs:37-40`).
  - Vault-backed features fail closed: registry credentials (`RegistryConfigService.cs:59-60`), git private repos (`GitDeployService.cs:86-87`) and kubeconfig (`KubernetesClientCache.cs:57-58`, `Servers.razor:475-476`).
  - Self-backups are written in plaintext with a warning (`BackupService.cs:152-153`).
  - All D-01 secrets are saved in plaintext either way.
- **VAULT_KEY lost:** vault entries and encrypted backups are unrecoverable, by design (`.env.example` warns). There is no escrow or rotation.
- **VAULT_KEY wrong:** not detected (D-03). `GetSecret` returns null with an error log. Encrypted-backup restore fails cleanly with "different VAULT_KEY" (`BackupService.cs:383-387`).
- **KDF and nonces:**
  - Vault: PBKDF2-HMAC-SHA256 with 600k iterations, a per-vault 16-byte random salt, 32-byte key, and a random 96-bit nonce per encryption. The format is `g1:`‖nonce‖tag‖ct (`VaultService.cs:197-213`). Random-nonce GCM is fine for this volume.
  - Backup: per-archive salt, giving a key independent of the vault key, plus an 8-byte random prefix and a 4-byte counter nonce. AAD = version‖index‖final flag, which detects truncation and reordering (`BackupArchiveCipher.cs:48-87,137-144`). Bytes after the final frame are ignored (harmless).
- **Zip-slip and symlinks:** handled (`BackupArchiver.cs:90-111`, tests at `BackupArchiverTests.cs:75,92`). The real gap is the missing allowlist (D-02).
- **Version mismatch:** a newer format or unknown migrations are rejected (`BackupService.cs:408-423, 435-443`), as is a cross-provider restore. An older backup is accepted, and its migrations run on boot.
- **Restart during restore:** the marker-gated, idempotent swap is retried on the next boot, and a corrupt marker is set aside (`RestoreBootHandler.cs:26-63`). This is good. Post-swap boot failure has no auto-revert (D-02 and D-07).
- **SQLite concurrency:**
  - WAL plus `synchronous=NORMAL` (`DatabaseInitializer.cs:72`).
  - The transient `MetricsDbContext` and the Identity context share one file.
  - Busy handling relies on Microsoft.Data.Sqlite's default command timeout (uncertain: about 30 s).
  - Self-backup uses `VACUUM INTO` for a consistent online snapshot.
- **PostgreSQL resilience:** `EnableRetryOnFailure(3)` on both contexts (`DatabaseRegistration.cs:65,73`). No user transactions exist, so the retrying strategy is not violated. There is a `DbReadyCheck` health check.
- **Unsafe deserialization:** no `TypeNameHandling`, `BinaryFormatter` or `[JsonPolymorphic]` (grep). All stores use System.Text.Json into concrete types. Kubeconfig loading via `KubernetesClientConfiguration` honours `exec:` credential plugins, so an admin-supplied kubeconfig can run binaries inside the container. This is by design but worth documenting.
- **Config fail-closed:**
  - An unknown DB provider or Postgres without a connection string throws (`DatabaseRegistration.cs:57-61,79-81`).
  - An empty `WHISKERS_DB_PROVIDER` is rejected per `.env.example` (uncertain: `"".Trim()` → "" hits `default:` and throws, which is consistent).
  - A missing `*_FILE` silently falls back (D-13).

## Positive controls
- AES-256-GCM vault with PBKDF2 600k and a persisted salt. Automatic, transparent CBC→GCM migration. Values are never returned by `ListSecrets` (`VaultService.cs:92-111`). Reads and writes share one lock.
- Streaming authenticated backup encryption with truncation and reorder protection, and a key that is never written to the archive.
- Hardened tar extraction (entry-type allowlist, canonical containment). The marker-committed, crash-safe deferred restore swap clears stale WAL/SHM. An automatic pre-restore safety backup is taken. Maintenance mode is entered during restore.
- MCP keys are stored only as SHA-256 hashes (256-bit random), compared in constant time, shown once, with legacy plaintext purged.
- The SSH password is never persisted (`[JsonIgnore]`). SSH keys are written 0600 with the filename sanitized. The temp MCP config is 0600 and deleted.
- Vault-dependent features (registries, git, kubeconfig) fail closed.
- The EF Core migration baseline for legacy EnsureCreated DBs is non-destructive and tested. Migrations exist for both providers. A Testcontainers Postgres smoke test runs in CI. A DI boot gate runs in CI.
- Backup download, restore and settings are admin-gated. Backup ids are regex-validated and path-contained.
- HttpClient request logging is suppressed (Warning), which keeps token-bearing URLs out of logs.

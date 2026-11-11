# Claim register (contract 0011, INV-56)

Every checkable statement of the governed documents — `README.md`, `SECURITY.md`, `docs/security-architecture.md` and
`docs/operations/production-checklist.md` — is listed here with the test that proves it against the code or the
repository's configuration ([contract 0011](../contracts/0011-self-report-claims.md), Part B). The tables are read by
`ClaimRegisterTests`; the release gate requires every test named under *Claims* (R8).

**How it works.** The governed files are split into blocks (a paragraph, a list item, a table body row, a fenced code
block). A block that holds a backticked span, a digit or a word starting with *always, never, only, every, all, none,
no, default, cannot, must, encrypt, plaintext, hash, sign, verif, refus, reject, block, delet, remov, pin, protect,
owner-only, constant time* or *secure* is claim-shaped and ends with an invisible marker:

- `<!-- claim:SEC-07 -->` (several ids comma-separated) — the block makes the claims of those rows;
- `<!-- claim-free:advice -->` — the block makes no checkable claim, for one of the fixed reasons `policy` (a process
  commitment), `advice` (an instruction to the operator), `limit` (what Whiskers does **not** do, pinned by
  PRODUCT_CONTRACT §5), `history` (behaviour of older releases) or `pointer` (navigation only).

`Block` is the first 12 hex digits of SHA-256 over the block with its marker removed and its whitespace collapsed. Any
edit to a marked block fails `ClaimRegisterTests` until its row here is re-confirmed; the test then writes
`docs/v1/CLAIMS.md.actual` with the fresh fingerprints (and every claim-free row), to be compared and copied over.
`Quote` is the exact words the tests prove; it must stand in the block. A test may not prove less than its sentence says
(contract 0011, D12): a quantifier needs an exhaustive check, never a sample.

## Claims

| Claim | File | Block | Quote | Check | Tests |
| --- | --- | --- | --- | --- | --- |
| RM-24 | `README.md` | `9d243323d4a2` | Every call is authenticated, scope-checked, guardrail-evaluated and logged. | behaviour | `MutationGatewayCoverageTests.Every_changing_mcp_tool_is_decided_by_the_gateway`, `EndpointAuthMatrixTests.Every_endpoint_answers_each_role_as_classified_and_runs_its_handler_only_when_allowed`, `DirectMcpCallGuardWireTests.The_running_server_applies_guardrails_to_direct_tool_calls`, `AuditCompletenessTests.Every_mutating_mcp_tool_call_is_one_authoritative_event` |
| RM-01 | `README.md` | `5c5187bbb374` | MCP over Streamable HTTP at `/mcp`. No hosted or cloud endpoint. | behaviour | `McpServedSurfaceTests.The_running_server_serves_exactly_the_catalogued_tools`, `HttpSecurityTests.An_anonymous_mcp_call_gets_401_with_a_bearer_challenge_not_a_sign_in_redirect`, `ServerJsonManifestTests` |
| RM-02 | `README.md` | `372b28fc93ca` | `AUTH_DISABLED=true` (container variable `Auth__Disabled`) makes every request an Admin | artifact + behaviour | `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable`, `SessionRevocationTests.Auth_disabled_keeps_its_local_user_and_a_cookie_never_gets_it` |
| RM-03 | `README.md` | `f8b2ffd7f745` | Stored as SHA-256 hashes, shown once. | behaviour | `McpPermissionServiceTests.A_new_key_is_returned_once_in_plaintext_but_only_its_hash_reaches_the_disk` |
| RM-04 | `README.md` | `4f6e76f9725d` | `Confirm` runs only the exact call a human approved. | behaviour | `ApprovalBindingTests.A_call_that_differs_in_anything_bound_does_not_inherit_the_approval`, `ApprovalBindingTests.The_binding_covers_every_bound_field_and_ignores_argument_order`, `DirectMcpCallGuardTests.Confirm_raises_one_approval_and_the_identical_call_runs_once_after_a_human_approves` |
| RM-05 | `README.md` | `35d92465944a` | Checkpoints are signed, and the trail is tamper-evident, only when a vault key (`VAULT_KEY` or `VAULT_KEY_FILE`) is set. | behaviour | `AuditHashChainTests.An_untouched_trail_verifies_with_signed_checkpoints`, `AuditHashChainTests.Checkpoints_written_before_VAULT_KEY_was_set_are_untrusted_but_no_tamper_finding` |
| RM-14 | `README.md` | `e49f4cbca72c` | the Docker API over mTLS behind a verb-whitelisting socket proxy | artifact | `OnboardingCommandsTests.The_socket_proxy_whitelist_is_exactly_the_approved_one_in_both_places`, `OnboardingCommandsTests.The_ghostunnel_command_is_exactly_the_approved_one`, `DockerMtlsHostnameTests` |
| RM-15 | `README.md` | `72fbcc35b9f5` | Hetzner and Hostinger cloud APIs. | constant | `SelfReportClaimTests.The_cloud_targets_are_hetzner_and_hostinger` |
| RM-06 | `README.md` | `8348efa14440` | `/app/data` (`WHISKERS_DATA_DIR`). SQLite by default, PostgreSQL optional. | default | `SelfReportClaimTests.Data_lives_in_app_data_sqlite_is_the_default_and_the_overlay_needs_a_password`, `DataPathOptionsTests.Defaults_to_app_data_when_unset` |
| RM-07 | `README.md` | `8348efa14440` | Secrets vault AES-256-GCM with `VAULT_KEY` / `VAULT_KEY_FILE`; SSH and mTLS client keys are sealed with a key derived from it. | behaviour + readiness | `SelfReportClaimTests.The_vault_encrypts_with_aes_256_gcm_under_a_pbkdf2_key`, `VaultKeyCheckTests.Vault_key_file_wins_over_the_plain_variable`, `KeyMaterialAtRestTests.No_file_or_unencrypted_self_backup_holds_a_key_after_upload_rollout_renewal_rotation_and_start_migration`, `ProductionReadinessServiceTests.The_vault_line_names_every_sealed_key_kind_and_the_key_line_counts_what_is_left` |
| RM-08 | `README.md` | `7a00106404e0` | amd64 + arm64, Trivy-gated, SBOM + provenance, keyless cosign signature. | artifact | `ReleaseWorkflowTests.Both_architectures_are_scanned_by_digest_and_exactly_those_digests_are_published`, `ReleaseWorkflowTests.Release_builds_the_tag_commit_and_publishes_only_after_tests_and_scan`, `SelfReportClaimTests.The_release_attests_provenance_and_an_sbom_and_signs_keyless` |
| RM-16 | `README.md` | `71fa213a68f1` | C# / .NET 10 | artifact | `SelfReportClaimTests.The_stack_is_dotnet_10_under_the_apache_license_2` |
| RM-09 | `README.md` | `65aa5c1d1ceb` | The installer installs exactly that release, generates the vault key on a new install (`VAULT_KEY_FILE`), verifies the image signature if cosign is installed | artifact | `InstallerContractTests.A_new_install_pins_the_release_and_hands_over_a_generated_vault_key_as_a_file`, `InstallerContractTests.A_good_signature_is_checked_against_exactly_this_release_and_its_digest_is_pinned` |
| RM-10 | `README.md` | `b82851b993f0` | Unattended alternative: `WHISKERS_ADMIN_EMAIL` + `WHISKERS_ADMIN_PASSWORD_FILE`. | behaviour | `AuthBootstrapTests.Bootstrap_seeds_admin_from_WHISKERS_ADMIN_EMAIL_on_first_run`, `LocalAuthIdentityTests.Seeder_creates_admin_from_config_then_is_idempotent` |
| RM-17 | `README.md` | `eaa63593908b` | the compose file maps them to the container variables. | artifact | `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable` |
| RM-18 | `README.md` | `4566d8a6ef84` | on a new install its compose file sets `TZ`, `VAULT_KEY_FILE` | artifact | `SelfReportClaimTests.The_installer_binds_loopback_by_default_and_sets_tz_and_the_vault_key_file` |
| RM-11 | `README.md` | `bc3fc3bc24bb` | `VAULT_KEY_FILE` / `VAULT_KEY` \| same | artifact | `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable` |
| RM-19 | `README.md` | `4409d439f1aa` | `sqlite` (default) or `postgres`. The overlay needs `WHISKERS_DB_PASSWORD` in `.env`. | default + artifact | `SelfReportClaimTests.Data_lives_in_app_data_sqlite_is_the_default_and_the_overlay_needs_a_password` |
| RM-20 | `README.md` | `0ce03f931005` | `OIDC_ENABLED`, `OIDC_AUTHORITY`, `OIDC_CLIENT_ID`, `OIDC_CLIENT_SECRET` \| `Oidc__Enabled`, `Oidc__Authority`, `Oidc__ClientId`, `Oidc__ClientSecret` | artifact | `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable` |
| RM-21 | `README.md` | `112a971c683a` | Published address; default `127.0.0.1:5100`. | artifact | `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable` |
| RM-22 | `README.md` | `d304424dbc9d` | `PATH_BASE` \| `PathBase` | artifact | `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable` |
| RM-12 | `README.md` | `2807a5ff5649` | loopback only by default. | default | `ForwardedHeadersConfigTests.Without_configuration_only_loopback_is_trusted`, `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable` |
| RM-13 | `README.md` | `232138d9f27d` | **The 5-minute budget** of a compose run (per command) can be too short for large builds; Git deploy keeps its own 300 s / 600 s / 300 s. | constant | `SelfReportClaimTests.The_compose_run_budget_is_five_minutes_and_git_deploy_keeps_300_600_300_seconds` |
| RM-25 | `README.md` | `f8ce97a70a2e` | a deployment that would use one of their names is refused until the container is removed or another name is chosen | behaviour | `DeployAppComposeTests.A_legacy_container_with_the_name_is_refused_with_the_ways_out`, `DeployPageComposeTests.The_form_refuses_a_legacy_container_with_its_name`, `DeployPageComposeTests.The_compose_tab_refuses_a_legacy_container_with_a_name_it_would_use` |
| RM-23 | `README.md` | `fcbeb0625505` | Apache License 2.0 | artifact | `SelfReportClaimTests.The_stack_is_dotnet_10_under_the_apache_license_2` |
| SEC-01 | `SECURITY.md` | `27ce9a2b15eb` | Whiskers is currently **stable** (`1.0.x`). | artifact | `SelfReportClaimTests.The_supported_line_is_the_version_of_this_source_tree` |
| SEC-02 | `SECURITY.md` | `2c3376c66abd` | Helm chart pins `replicas: 1` with a `Recreate` strategy | artifact | `SelfReportClaimTests.The_helm_chart_runs_one_replica_with_recreate` |
| SEC-15 | `SECURITY.md` | `be2a1ac9b3fe` | roll out the mTLS certificates, the Docker proxy and telemetry | artifact | `OnboardingCommandsTests.DockerProxyCompose_publishes_2376_on_the_tailnet_ip_and_keeps_dangerous_verbs_off`, `OnboardingCommandsTests.NodeExporterCompose_binds_to_the_tailnet_ip_only` |
| SEC-16 | `SECURITY.md` | `2bb11d69e4f0` | the Docker API (Docker Engine API over mutual TLS, client CN pinned, behind a verb-whitelisting socket proxy) | artifact + behaviour | `OnboardingCommandsTests.The_socket_proxy_whitelist_is_exactly_the_approved_one_in_both_places`, `OnboardingCommandsTests.The_ghostunnel_command_is_exactly_the_approved_one`, `DockerMtlsHostnameTests` |
| SEC-17 | `SECURITY.md` | `838dc5210a13` | onboarding binds the mTLS Docker proxy (port 2376) and `node_exporter` to the host's mesh (WireGuard) address, not to a public interface. | artifact | `OnboardingCommandsTests.DockerProxyCompose_publishes_2376_on_the_tailnet_ip_and_keeps_dangerous_verbs_off`, `OnboardingCommandsTests.NodeExporterCompose_binds_to_the_tailnet_ip_only` |
| SEC-18 | `SECURITY.md` | `e611af28a2dc` | evaluated in code at the tool boundary — not in a prompt. | behaviour | `DirectMcpCallGuardWireTests.The_running_server_applies_guardrails_to_direct_tool_calls`, `MutationGatewayCoverageTests.Every_changing_mcp_tool_is_decided_by_the_gateway` |
| SEC-03 | `SECURITY.md` | `50a9b9218f4b` | (Viewer → Read, Operator → Write, Admin → Admin) | behaviour | `SelfReportClaimTests.Every_role_maps_to_its_scope` |
| SEC-04 | `SECURITY.md` | `10647bf9d964` | the identical call (same caller, tool and arguments) runs exactly once within 10 minutes. | constant + behaviour | `SelfReportClaimTests.An_approval_grant_lasts_ten_minutes`, `ApprovalStoreTests.An_approved_approval_is_usable_only_within_its_grant`, `DirectMcpCallGuardTests.Confirm_raises_one_approval_and_the_identical_call_runs_once_after_a_human_approves`, `ToolAuthorizationTests.Two_concurrent_runs_on_one_approval_execute_once_and_the_other_is_recorded_as_refused` |
| SEC-05 | `SECURITY.md` | `123e9b598322` | The exceptions are fixed in code and listed on the *Guardrails* page (contract 0009) | behaviour | `KillSwitchExemptionTests` |
| SEC-06 | `SECURITY.md` | `79bb80b70007` | shown once at creation, and **stored only as its SHA-256 hash** | behaviour | `McpPermissionServiceTests.A_new_key_is_returned_once_in_plaintext_but_only_its_hash_reaches_the_disk` |
| SEC-19 | `SECURITY.md` | `79bb80b70007` | leftover copies are deleted on start. | behaviour | `McpPermissionServiceTests.Leftover_files_of_the_removed_flat_key_store_are_deleted_on_start` |
| SEC-20 | `SECURITY.md` | `7c2dd459106d` | every agent and direct MCP tool call lands in Agent History (actor/key, redacted parameters, decision, result). | behaviour | `McpCallOutcomeTests`, `AuditCompletenessTests.Every_mutating_mcp_tool_call_is_one_authoritative_event` |
| SEC-07 | `SECURITY.md` | `b381d4da03c1` | Vault-stored secrets are encrypted at rest (AES-256-GCM, key derived from `VAULT_KEY` or `VAULT_KEY_FILE`) | behaviour | `SelfReportClaimTests.The_vault_encrypts_with_aes_256_gcm_under_a_pbkdf2_key`, `VaultKeyCheckTests.Vault_key_file_wins_over_the_plain_variable`, `SecretsAtRestTests.Every_vault_backed_setting_is_covered_here` |
| SEC-08 | `SECURITY.md` | `b381d4da03c1` | Whiskers writes the SSH keys and the per-host mTLS keys owner-only; the readiness line "mTLS client keys protected" checks the mTLS keys' modes | behaviour + readiness | `SelfReportClaimTests.An_uploaded_ssh_key_is_written_owner_only`, `PerHostCredentialTests.Keys_are_0600_and_host_and_slot_directories_0700_owned_by_whiskers`, `PerHostCredentialTests.Readiness_names_shared_hosts_and_a_shared_key_left_behind` |
| SEC-29 | `SECURITY.md` | `b381d4da03c1` | the SSH private keys (`ssh-keys/`) and every mTLS client key (`mtls/`, shared and per host); keys stored in plaintext before are sealed at the next start. | behaviour | `KeyMaterialAtRestTests.No_file_or_unencrypted_self_backup_holds_a_key_after_upload_rollout_renewal_rotation_and_start_migration`, `KeyMaterialMigrationTests.Legacy_ssh_shared_and_per_host_keys_are_sealed_and_open_as_before`, `SealedKeyFilesTests.Round_trip_stores_no_plaintext_and_opens_with_its_purpose` |
| SEC-30 | `SECURITY.md` | `b381d4da03c1` | mTLS keys are opened in memory only; an SSH key is unsealed for one use into a 0600 file on `/dev/shm` that is removed afterwards. | behaviour | `MtlsClientKeyLoaderTests.The_loaded_certificate_presents_the_sealed_key_in_a_tls_handshake`, `SshKeyLeaseTests.An_upload_is_stored_sealed_and_a_lease_holds_the_key_until_it_is_disposed`, `SshKeyLeaseTests.The_lease_is_removed_after_ssh_failed`, `SshKeyLeaseTests.A_directory_that_is_not_memory_backed_is_refused_and_nothing_is_unsealed` |
| SEC-31 | `SECURITY.md` | `b381d4da03c1` | A new key needs `VAULT_KEY` | behaviour | `SshKeyLeaseTests.An_upload_without_a_usable_vault_is_refused_and_nothing_is_written`, `MtlsClientKeyLoaderTests.Without_a_usable_vault_no_client_key_is_made` |
| SEC-32 | `SECURITY.md` | `b381d4da03c1` | `VAULT_KEY` can be replaced in place | behaviour | `VaultKeyRotationTests.Every_consumer_opens_with_the_new_key_and_no_longer_with_the_old_one`, `VaultKeyRotationTests.The_boot_handler_rotates_with_real_self_backups_marks_the_older_one_and_records_the_rotation` |
| SEC-09 | `SECURITY.md` | `abc5d0cb3b72` | **The default `docker-compose.yml` profile is privileged** (host PID namespace) | artifact | `SelfReportClaimTests.The_default_compose_is_privileged_and_the_hardened_one_is_not` |
| SEC-21 | `SECURITY.md` | `25b04a2a9333` | both gitignored and volume-mounted, never baked into the image or committed. | artifact | `SelfReportClaimTests.Secrets_are_neither_committed_nor_in_the_image_build_context` |
| SEC-22 | `SECURITY.md` | `c0e42235344b` | The vault's key also derives the keys for the webhook secrets, the rollback snapshots and the data-protection key ring | behaviour | `SecretsAtRestTests.Webhook_secrets_and_rollback_snapshots_are_stored_encrypted_and_keep_working`, `DataProtectionKeyStorageTests.Keys_are_written_encrypted_and_the_ring_stays_usable_after_a_restart` |
| SEC-10 | `SECURITY.md` | `53678ae7876a` | the vault is locked (it is left unchanged until the right key is set again). | behaviour | `VaultKeyCheckTests.Wrong_key_is_rejected_reads_nothing_writes_nothing_and_leaves_the_file_unchanged`, `WebhookSecretTests.A_secret_that_cannot_be_opened_refuses_every_delivery`, `DataProtectionKeyStorageTests.With_a_vault_key_that_does_not_open_the_vault_nothing_is_written_in_plaintext` |
| SEC-23 | `SECURITY.md` | `9510cbbad764` | encrypted in place at the first start with a usable vault, and keep working. | behaviour | `SecretsAtRestTests.The_migration_encrypts_plaintext_webhook_secrets_and_snapshots_and_both_keep_working`, `DataProtectionKeyStorageTests.Without_a_vault_key_the_keys_are_written_as_before_and_encrypted_in_place_once_one_is_set` |
| SEC-24 | `SECURITY.md` | `a8e9e4772ca8` | webhooks cannot be created or have their secret regenerated | behaviour | `WebhookSecretTests.Without_a_usable_vault_creating_or_rotating_a_secret_is_refused_and_nothing_is_written` |
| SEC-25 | `SECURITY.md` | `ecfb509fcf8f` | With `VAULT_KEY` set only an encrypted archive is restored | behaviour | `BackupServiceTests.With_a_vault_key_an_unencrypted_archive_is_refused` |
| SEC-11 | `SECURITY.md` | `b142274cb531` | more than 100,000 entries or 32 GiB of content, is refused | constant + behaviour | `SelfReportClaimTests.The_restore_limits_are_100000_entries_and_32_gib`, `BackupArchiverTests.An_archive_with_more_entries_than_allowed_is_refused`, `BackupArchiverTests.An_archive_that_unpacks_to_more_bytes_than_allowed_is_refused_before_it_is_written` |
| SEC-12 | `SECURITY.md` | `d0421dcda8df` | it runs only when it opens with `\restrict` (client tools 17.6 or newer) | behaviour | `PostgresDumpToolTests.Only_tools_with_the_meta_command_protection_count_as_able_to_restore` |
| SEC-13 | `SECURITY.md` | `a3d052acea2c` | With `AUTH_DISABLED=true` Whiskers itself refuses public client addresses, non-IP host names (DNS rebinding) and cross-site origins | behaviour | `AuthDisabledAccessPolicyTests.Public_client_address_is_refused`, `AuthDisabledAccessPolicyTests.Dns_rebinding_host_name_is_refused`, `AuthDisabledAccessPolicyTests.Cross_site_origin_is_refused_same_origin_allowed` |
| SEC-14 | `SECURITY.md` | `307fdb03b8fd` | It requires the one-time token from `/app/data/setup-token.txt` (or `WHISKERS_SETUP_TOKEN`) | behaviour | `SetupStateServiceTests.First_run_writes_an_owner_only_token_and_completion_requires_it`, `SetupStateServiceTests.The_token_survives_a_restart_and_can_be_preset` |
| SEC-26 | `SECURITY.md` | `c9d805213738` | Without a usable vault Whiskers creates no webhook | behaviour | `WebhookSecretTests.Without_a_usable_vault_creating_or_rotating_a_secret_is_refused_and_nothing_is_written` |
| SEC-27 | `SECURITY.md` | `71782ea65271` | Dependabot config ships in `.github/dependabot.yml`. | artifact | `OnboardingCommandsTests.The_templates_roll_out_the_same_images_as_the_files_dependabot_keeps_current` |
| SEC-28 | `SECURITY.md` | `007bd7c8b6e7` | (non-root, no `privileged`, dropped capabilities, read-only rootfs | artifact | `SelfReportClaimTests.The_default_compose_is_privileged_and_the_hardened_one_is_not` |
| SA-03 | `docs/security-architecture.md` | `70cea61dc284` | `node_exporter` → VictoriaMetrics (pull over the mesh) | artifact | `OnboardingCommandsTests.NodeExporterCompose_binds_to_the_tailnet_ip_only` |
| SA-04 | `docs/security-architecture.md` | `e915755fa45e` | Docker Engine API over **mTLS** (client certificate, CN-pinned) + verb-whitelisting socket proxy | artifact + behaviour | `OnboardingCommandsTests.The_socket_proxy_whitelist_is_exactly_the_approved_one_in_both_places`, `OnboardingCommandsTests.The_ghostunnel_command_is_exactly_the_approved_one`, `DockerMtlsHostnameTests` |
| SA-05 | `docs/security-architecture.md` | `3bc8fefd5b56` | one-shot privileged `nsenter` container | behaviour | `HostShellDeadlineTests.The_helper_is_a_one_shot_privileged_nsenter_container_in_the_host_pid_namespace` |
| SA-06 | `docs/security-architecture.md` | `066bcd52a8ab` | roll out the mTLS certificates and the Docker proxy, and install telemetry | artifact | `OnboardingCommandsTests.DockerProxyCompose_publishes_2376_on_the_tailnet_ip_and_keeps_dangerous_verbs_off`, `OnboardingCommandsTests.NodeExporterCompose_binds_to_the_tailnet_ip_only` |
| SA-07 | `docs/security-architecture.md` | `a9b7975d6653` | onboarding binds the mTLS Docker proxy and `node_exporter` to the host's mesh address, not to a public interface. | artifact | `OnboardingCommandsTests.DockerProxyCompose_publishes_2376_on_the_tailnet_ip_and_keeps_dangerous_verbs_off`, `OnboardingCommandsTests.NodeExporterCompose_binds_to_the_tailnet_ip_only` |
| SA-08 | `docs/security-architecture.md` | `3dd270dda683` | passes the same scope check in code (Read / Write / Admin, or an explicit per-key tool list) | behaviour | `ToolAuthorizationWireTests.Allowed_tools_cannot_widen_a_read_key_to_a_write_tool`, `GuardrailEngineTests.Write_tool_by_read_principal_is_denied`, `GuardrailEngineTests.Tool_outside_principal_allowlist_is_denied`, `ToolAuthorizationTests.A_stricter_stored_level_applies_to_the_agent_too` |
| SA-09 | `docs/security-architecture.md` | `cfdf38e7ce43` | `api-keys.json` no longer exists | behaviour | `McpPermissionServiceTests.Leftover_files_of_the_removed_flat_key_store_are_deleted_on_start` |
| SA-10 | `docs/security-architecture.md` | `78e331d4ad87` | The SSH private keys and the mTLS client keys are sealed with a key derived from `VAULT_KEY` | behaviour + readiness | `KeyMaterialAtRestTests.No_file_or_unencrypted_self_backup_holds_a_key_after_upload_rollout_renewal_rotation_and_start_migration`, `ProductionReadinessServiceTests.The_vault_line_names_every_sealed_key_kind_and_the_key_line_counts_what_is_left` |
| SA-01 | `docs/security-architecture.md` | `aafa47cd02e7` | the vault on **AES-256-GCM + PBKDF2** | behaviour | `SelfReportClaimTests.The_vault_encrypts_with_aes_256_gcm_under_a_pbkdf2_key` |
| SA-11 | `docs/security-architecture.md` | `251f583a6182` | both architectures (amd64 and arm64) are scanned by digest and exactly the scanned digests are published, only after the full test suite passed on the tag commit, and a published version is never replaced | artifact | `ReleaseWorkflowTests.Both_architectures_are_scanned_by_digest_and_exactly_those_digests_are_published`, `ReleaseWorkflowTests.Release_builds_the_tag_commit_and_publishes_only_after_tests_and_scan`, `ReleaseWorkflowTests.Versioned_artifacts_are_checked_for_existence_before_they_are_pushed` |
| SA-12 | `docs/security-architecture.md` | `b1989e7bb9d8` | **keyless-signed with [cosign](https://github.com/sigstore/cosign)** (Sigstore) | artifact | `SelfReportClaimTests.The_release_attests_provenance_and_an_sbom_and_signs_keyless` |
| PC-03 | `docs/operations/production-checklist.md` | `d29aa25b3152` | The installer pins `X.Y.Z` and, with cosign installed, the verified digest (`image: …:X.Y.Z@sha256:…`). | artifact | `InstallerContractTests.A_new_install_pins_the_release_and_hands_over_a_generated_vault_key_as_a_file`, `InstallerContractTests.A_good_signature_is_checked_against_exactly_this_release_and_its_digest_is_pinned` |
| PC-04 | `docs/operations/production-checklist.md` | `554600de4e28` | The default `docker-compose.yml` is privileged (host PID namespace, root) | artifact | `SelfReportClaimTests.The_default_compose_is_privileged_and_the_hardened_one_is_not` |
| PC-01 | `docs/operations/production-checklist.md` | `7b64c749fd34` | The installer binds to `127.0.0.1` by default (`--bind <addr>` or `WHISKERS_BIND` | artifact | `SelfReportClaimTests.The_installer_binds_loopback_by_default_and_sets_tz_and_the_vault_key_file`, `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable` |
| PC-05 | `docs/operations/production-checklist.md` | `078a85b415f3` | the readiness card names it as an ignored peer | readiness | `ProductionReadinessServiceTests.A_peer_whose_forwarded_headers_were_ignored_is_named_and_none_means_no_line`, `HttpSecurityTests.The_session_cookie_is_http_only_lax_and_secure_over_https` |
| PC-06 | `docs/operations/production-checklist.md` | `c87862b5df46` | Helm pins `replicas: 1` with `Recreate`. | artifact | `SelfReportClaimTests.The_helm_chart_runs_one_replica_with_recreate` |
| PC-07 | `docs/operations/production-checklist.md` | `fded4741a463` | `true` makes every visitor an Admin | behaviour | `SessionRevocationTests.Auth_disabled_keeps_its_local_user_and_a_cookie_never_gets_it` |
| PC-08 | `docs/operations/production-checklist.md` | `0df62a36aedd` | it needs the one-time token from `/app/data/setup-token.txt` or `WHISKERS_SETUP_TOKEN` | behaviour | `SetupStateServiceTests.First_run_writes_an_owner_only_token_and_completion_requires_it`, `SetupStateServiceTests.The_token_survives_a_restart_and_can_be_preset` |
| PC-09 | `docs/operations/production-checklist.md` | `d3509d35dbf7` | `OIDC_REQUIRE_HTTPS=true` (the readiness card warns otherwise) | artifact + readiness | `SelfReportClaimTests.The_release_compose_maps_each_env_name_of_the_readme_table_to_its_variable`, `ProductionReadinessServiceTests.Oidc_discovery_over_plain_http_is_named` |
| PC-10 | `docs/operations/production-checklist.md` | `36a7cb3acbd7` | AI triggers stay read-only unless granted capabilities. | behaviour | `TriggerAuthorityTests.A_new_trigger_is_read_only`, `TriggerAuthorityTests.A_granted_run_holds_no_more_than_the_grant_needs_even_with_an_admin_owner` |
| PC-11 | `docs/operations/production-checklist.md` | `20cff4622280` | run on the authority of the Admin who took them over | behaviour | `KillSwitchTests.AutoUpdate_and_AutoPatch_are_suspended_when_their_owner_lost_authority`, `KillSwitchTests.An_ownerless_AutoUpdate_is_suspended_with_the_real_owner_store_and_the_admins_are_told` |
| PC-12 | `docs/operations/production-checklist.md` | `03cc3217e7e3` | (`VAULT_KEY_FILE`, mode 0600; the installer does this) | artifact | `InstallerContractTests.The_vault_key_file_is_private`, `InstallerContractTests.A_new_install_pins_the_release_and_hands_over_a_generated_vault_key_as_a_file` |
| PC-02 | `docs/operations/production-checklist.md` | `0ad73689c89c` | File variants exist only for `VAULT_KEY_FILE`, `VAULT_KEY_NEW_FILE` (a rotation), `WHISKERS_DB_CONNECTION_FILE`, `WHISKERS_ADMIN_PASSWORD_FILE` and the VPN enrollment keys | config | `SelfReportClaimTests.File_variants_exist_only_for_the_named_settings` |
| PC-13 | `docs/operations/production-checklist.md` | `25a07634a1f6` | SSH private keys (`ssh-keys/`), all mTLS client keys (`mtls/`: the shared one and one per host) | behaviour + readiness | `KeyMaterialAtRestTests.No_file_or_unencrypted_self_backup_holds_a_key_after_upload_rollout_renewal_rotation_and_start_migration`, `ProductionReadinessServiceTests.The_vault_line_names_every_sealed_key_kind_and_the_key_line_counts_what_is_left` |
| PC-14 | `docs/operations/production-checklist.md` | `e8b741fa616a` | Self-backups hold the same data; with `VAULT_KEY` set they are encrypted. | behaviour | `BackupServiceTests.Encrypted_backup_lists_and_validates_round_trip`, `BackupServiceTests.With_a_vault_key_an_unencrypted_archive_is_refused` |
| PC-15 | `docs/operations/production-checklist.md` | `e86143438aa0` | (`Metrics__ScrapeToken`; without one the endpoint is off) | behaviour | `MetricsScrapeAuthTests.Check_NoConfiguredToken_Disabled` |
| PC-16 | `docs/operations/production-checklist.md` | `2cd98751f4e9` | (`Audit__RetentionDays`, default 365, at least 30) | default | `AuditRetentionTests.The_default_keeps_a_year`, `AuditRetentionTests.Retention_is_configurable_but_never_below_the_floor` |
| PC-17 | `docs/operations/production-checklist.md` | `46664e021d9d` | Whiskers' own control plane is protected by a built-in rule (INV-59) | behaviour | `SelfProtectionTests.Keys_agents_over_MCP_automations_and_operators_are_refused`, `SelfProtectionTests.An_interactive_Admin_needs_an_explicit_Admin_approval` |

## Claim-free blocks

| File | Block | Reason | Starts with |
| --- | --- | --- | --- |
| `README.md` | `c13702a701d0` | policy | **Status: stable (1.0).** Run it on a tr |
| `README.md` | `941b3da88f96` | history | **Scope of this README:** `main`, i.e. t |
| `README.md` | `323ff41f5965` | advice | ```bash VER=X.Y.Z # a release from https |
| `README.md` | `8e38830acb63` | advice | ```bash VER=X.Y.Z docker run -d --name w |
| `README.md` | `7f89fd39428a` | advice | This has no vault key; set `VAULT_KEY_FI |
| `README.md` | `5e10ca319749` | advice | ```bash docker exec whiskers cat /app/da |
| `README.md` | `92d3708115e3` | advice | ```bash export WHISKERS_MCP_API_KEY=… cl |
| `README.md` | `347d236a9d4a` | advice | ```json { "mcpServers": { "whiskers": { |
| `README.md` | `88b08bffdb1d` | advice | ```bash VER=X.Y.Z cosign verify "ghcr.io |
| `README.md` | `dad9e95b15b0` | history | Images before v0.12.1 are not cosign-sig |
| `README.md` | `7b6ff0ec096d` | advice | Everything is optional. Where a setting |
| `README.md` | `272aa341265e` | advice | - **`docker run`:** nothing is set; pass |
| `README.md` | `b84bad32980e` | pointer | All keys: [docs/configuration.md](https: |
| `README.md` | `0f986ac3dd73` | limit | Deliberate limits of Whiskers 1.0 ([prod |
| `README.md` | `79ff0f69e977` | limit | - **Root-equivalent administration.** An |
| `README.md` | `024c9da26d23` | limit | - **Single instance.** One process and o |
| `README.md` | `44b6eb673e50` | limit | - **Global roles.** Viewer, Operator and |
| `README.md` | `0f8418efa46a` | limit | - **No terminal transcript.** Opening a |
| `README.md` | `3a0cc64255b0` | limit | - **Out-of-band changes are invisible.** |
| `README.md` | `d5a1a574b44a` | limit | - **No second factor for local accounts. |
| `README.md` | `a2747be852ac` | limit | Compose deployments are decided on their |
| `README.md` | `568319d4e1e3` | limit | - **Proxies and tunnels behind an unreco |
| `README.md` | `7d874a4c037c` | limit | - **Tailscale serve state** is read from |
| `README.md` | `ce0710612c8e` | limit | - **A Traefik on the host** outside the |
| `README.md` | `6d29e54a90ac` | limit | - **The tag race**: putting a tag back a |
| `README.md` | `a7343775105d` | limit | - **`container.update` and `autopatch.ru |
| `README.md` | `4fa80a44962e` | limit | - **Pinned containers show an image id** |
| `README.md` | `11f26aced0e7` | limit | - **Late daemon work after a timeout**: |
| `README.md` | `3b05cab3b9c3` | history | **Upgrade note (contract 0006).** The ne |
| `README.md` | `573b0dab3b01` | history | **Upgrade note (contract 0013).** The de |
| `README.md` | `f7af4d73bc43` | advice | Requires the .NET SDK version in `global |
| `README.md` | `2319ae6706c7` | advice | ```bash dotnet build Whiskers.slnx -c Re |
| `README.md` | `fc3e806e0705` | advice | After changing a package reference: `dot |
| `README.md` | `bde34b02dcf3` | pointer | \| Installation (all paths) \| [docs/insta |
| `README.md` | `22a2a8ef1298` | pointer | \| Everything else \| [docs/README.md](htt |
| `SECURITY.md` | `e233f1b70b26` | policy | **Please report vulnerabilities privatel |
| `SECURITY.md` | `873df784dcd1` | policy | Whiskers has a single maintainer, so the |
| `SECURITY.md` | `c459f437f7d9` | policy | \| Acknowledgement of your report \| withi |
| `SECURITY.md` | `a41d1877b36e` | policy | \| Triage: confirmed or not, severity (CV |
| `SECURITY.md` | `e24b21437c3c` | policy | \| Fix released for **critical / high** \| |
| `SECURITY.md` | `586ef6d8f8ba` | policy | \| Fix released for **medium / low** \| wi |
| `SECURITY.md` | `abca188454fb` | policy | If a target slips, you will hear so — wi |
| `SECURITY.md` | `9bf87d10b106` | policy | \| `1.0.x` (stable) \| ✅ latest only \| |
| `SECURITY.md` | `5ab8016d2397` | policy | \| `< 1.0` (beta) \| ❌ \| |
| `SECURITY.md` | `e1ade1a68e7d` | limit | - **The Docker-API and host-control plan |
| `SECURITY.md` | `7bd63b63bc53` | limit | - **Not covered in 1.0, by design** (pro |
| `SECURITY.md` | `a223f82ae23f` | advice | - **Local accounts have no MFA in 1.0.** |
| `SECURITY.md` | `26ac10ed8324` | advice | - Keep the agent's guardrail presets res |
| `docs/security-architecture.md` | `14f4cc7038ab` | pointer | How Whiskers reaches managed hosts, what |
| `docs/security-architecture.md` | `7daa853142a0` | pointer | None of them uses SSH in steady state, a |
| `docs/security-architecture.md` | `597e2b99ff77` | limit | **The Docker-API and host-control planes |
| `docs/security-architecture.md` | `e89aa1cb46e5` | history | Every published release image is, from v |
| `docs/security-architecture.md` | `d5272bac9020` | history | The verify command is in the [README](.. |
| `docs/operations/production-checklist.md` | `f7f263e36e2f` | advice | Go through this list before Whiskers man |
| `docs/operations/production-checklist.md` | `d380c7f4ee0f` | pointer | Read [Accepted limits](#accepted-limits- |
| `docs/operations/production-checklist.md` | `64e87685a443` | advice | - [ ] **MCP keys with the least level** |
| `docs/operations/production-checklist.md` | `ae163255d9f4` | advice | - [ ] **A copy of `VAULT_KEY` off the ho |
| `docs/operations/production-checklist.md` | `80af1fda07b6` | advice | - [ ] **No plaintext credentials left:** |
| `docs/operations/production-checklist.md` | `8247dfc4c7e8` | advice | - [ ] **The data directory is protected* |
| `docs/operations/production-checklist.md` | `a46070c241a6` | advice | - [ ] **A scheduled self-backup** (*Sche |
| `docs/operations/production-checklist.md` | `f412d0404aa6` | advice | - [ ] **One restore tested** on a spare |
| `docs/operations/production-checklist.md` | `856161b78ae2` | advice | - [ ] **PostgreSQL:** a dedicated owner |
| `docs/operations/production-checklist.md` | `c1106c6de89e` | advice | - [ ] **`/healthz` and `/readyz` probed* |
| `docs/operations/production-checklist.md` | `6d5658970302` | advice | - [ ] **Structured logs** if you ship lo |
| `docs/operations/production-checklist.md` | `cc421d26b6fb` | advice | - [ ] **A notification channel** that re |
| `docs/operations/production-checklist.md` | `040f876fc46c` | advice | - [ ] **Onboarded hosts run over the mes |
| `docs/operations/production-checklist.md` | `f1c36c40f1a9` | advice | - [ ] **Every mTLS host on its own clien |
| `docs/operations/production-checklist.md` | `e61cb49bcfaf` | limit | These are deliberate limits of Whiskers |
| `docs/operations/production-checklist.md` | `8789645ea456` | limit | - **Root-equivalent administration.** Th |
| `docs/operations/production-checklist.md` | `363b22fdadf1` | limit | - **Host access is outside the contract. |
| `docs/operations/production-checklist.md` | `d1a321fe2e0d` | limit | - **Single instance.** One process, one |
| `docs/operations/production-checklist.md` | `0e012bca5ed5` | limit | - **Global roles.** Viewer, Operator and |
| `docs/operations/production-checklist.md` | `a2c321045126` | limit | - **No terminal transcript.** Opening an |
| `docs/operations/production-checklist.md` | `2d1f4bd3ad69` | limit | - **Out-of-band changes are invisible.** |
| `docs/operations/production-checklist.md` | `63941bcee530` | limit | - **No second factor for local accounts. |

## Retired claims

| Claim | Why |
| --- | --- |

# Operations

Guides for running Whiskers in production.

| Guide | Covers |
|---|---|
| [observability.md](observability.md) | Where the logs are, structured (JSON) logs, following one request to its operation and audit row, the loop-stall, operation-incomplete and audit-failure signals with example alert rules |
| [local-accounts.md](local-accounts.md) | Local accounts: the MFA posture (none for local accounts in 1.0 — use OIDC with MFA in production), password policy and lockout, own password change, Admin add/reset/delete, one-time recovery codes, host recovery with `--recover-local-account`, the last-Admin rule |
| [backup-and-recovery.md](backup-and-recovery.md) | Self-backup and restore on SQLite and PostgreSQL, the off-volume recovery onto a new host, recovery point and time |

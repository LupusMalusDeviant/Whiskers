# Operations

Guides for running Whiskers in production.

| Guide | Covers |
|---|---|
| [observability.md](observability.md) | Where the logs are, structured (JSON) logs, following one request to its operation and audit row, the loop-stall, operation-incomplete and audit-failure signals with example alert rules |
| [local-accounts.md](local-accounts.md) | Local accounts: the MFA posture (none for local accounts in 1.0 — use OIDC with MFA in production), password policy and lockout, own password change, Admin add/reset/delete, one-time recovery codes, host recovery with `--recover-local-account`, the last-Admin rule |
| [identity-bindings.md](identity-bindings.md) | Google/OIDC sign-in identities (R1-27): binding by issuer and subject, the IdP requirements (`email` scope, users cannot change their own address, honest `email_verified`, `Oidc:TrustEmailVerified`), pending requests, confirming in Settings or with `--confirm-identity-binding`, recycled addresses, what an upgrade or an issuer change does |
| [ci-and-release-checks.md](ci-and-release-checks.md) | What every push, pull request and release must pass (mandatory PostgreSQL suites, endpoint authorization matrix, image, hardened-profile and N−1 upgrade/recovery checks, dependency, secret and static analysis) and the repository-hosted controls (branch protection, Dependabot, secret scanning, code-scanning rules) |
| [backup-and-recovery.md](backup-and-recovery.md) | Self-backup and restore on SQLite and PostgreSQL, the off-volume recovery onto a new host, recovery point and time |

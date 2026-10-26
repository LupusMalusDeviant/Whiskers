# Architecture Decision Records

MADR-artige Entscheidungsdokumente, fortlaufend nummeriert `NNNN-slug.md`.

| Nr. | Titel | Status |
|---|---|---|
| [0001](0001-vault-aead-gcm-pbkdf2.md) | Vault: AES-256-GCM + PBKDF2, Migration von AES-CBC | Akzeptiert (2026-07-07) |
| [0002](0002-ssh-host-key-verification-deferred.md) | SSH-Host-Key-Verifizierung (`accept-new`) | Umgesetzt (`SshHostKeyPolicy`, Commit `23e691b`) |
| [0003](0003-ef-core-migrations-baseline.md) | EF-Core-Migrations mit Baseline für Bestands-DBs | Akzeptiert (2026-07-07) |
| [0004](0004-postgres-provider-support.md) | PostgreSQL-Provider + Multi-Provider-Migrations-Architektur | Akzeptiert, umgesetzt (Provider gemergt, `SqliteToPostgresMigrator`) |
| [0005](0005-mutation-gateway.md) | Mutation Gateway: one enforcement point for every infrastructure mutation | Proposed (2026-10-26) |

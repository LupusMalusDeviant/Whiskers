# Services/Setup

First-run and operational-readiness state, consumed by the setup wizard (W1) and the Settings page.

## Files

| File | Purpose |
|---|---|
| `ISetupStateService.cs` / `SetupStateService.cs` | Whether setup is complete (= at least one admin exists; admin-role-authoritative with a cached flag file). Drives the setup-redirect middleware. R1-48 (designed exemption from the gateway, maintainer option a): completion records `setup.first_admin` in the audit log **before** it writes anything and refuses without that record; it then adds the address to the whitelist (`AddFirstAdminAsync`) and makes it Admin (`SetFirstAdminAsync`), both closed once any Admin exists. Whitelist first: a crash in between leaves setup open with the same token. |
| `SetupCompletion.cs` | The atomic, race-guarded wizard completion (create admin → mark complete). |
| `ProductionReadinessService.cs` | W3.4: the static "Produktionsreif?" checklist shown in Settings — read-only checks (auth on, VAULT_KEY set, self-backup scheduled, update policy, non-root, HTTPS). Informational only, gates nothing. R1-29 (INV-56): it reports where the cloud API keys and the DNS token actually are (plaintext counted from servers.json and app-settings.json, not inferred from the vault switch), and the vault line names which credentials are still unencrypted (since R1-30: inbound webhook secrets, SSH and mTLS keys). R1-30: a second credentials line names each notification/AI credential still stored in plaintext (app-settings.json, agent-settings.json). R1-31: a third line counts plaintext webhook secrets and rollback snapshots in the database and plaintext data-protection key files; the vault line names the SSH and mTLS client keys as the only plaintext left. R1-35: "Datenspeicher lesbar" names the JSON stores recovered from their previous generation since the start and the unreadable copies (`*.corrupt-*`, `guardrails.json.unreadable-*`) still in the data directory. R1-33: on PostgreSQL a line shows whether the client tools for the self-backup are usable (`pg_dump --version`; a found version is kept for the process); tools without the `\restrict` protection (before 17.6, 16.10, 15.14, 14.19) are flagged, since their backups cannot be restored. |

## Related

- Wizard UI: [`../../Components/Pages/Setup.razor`](../../Components/Pages/Setup.razor)
- Checklist panel: [`../../Components/Shared/ProductionReadinessPanel.razor`](../../Components/Shared/ProductionReadinessPanel.razor)
- Redirect middleware: [`../../Startup/WhiskersPipelineExtensions.cs`](../../Startup/WhiskersPipelineExtensions.cs)

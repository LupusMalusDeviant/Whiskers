# Services/Onboarding

**One-click server onboarding** into the zero-SSH-key managed stack. Starting from a single SSH bootstrap connection (the only time SSH is used), the orchestrator installs Tailscale, deploys telemetry and the mTLS Docker proxy, issues certificates, and switches the server to mesh + mTLS, with live progress streamed to the UI.

The bootstrap authenticates with **either an uploaded SSH key or a transient root/SSH password** (fed to `sshpass` via the `SSHPASS` env var, never persisted, `ServerConfig.SshPassword` is `[JsonIgnore]`). On success both are dropped: the password is cleared by a store write of a clone of the record the run holds (contract 0018 A6, never by editing an instance the store may share) and the key deleted from disk (`IServerConfigService.DeleteSshKeyAsync`), so **no standing credential remains**. Before its first command every run (a retry included, which reads the record again) reports the SSH destination and the credential the bootstrap uses — „Passwort" or the key file name, flagged when the file is missing, never a value (contract 0018 A5, `OnboardingRetryTests`); the executor's precedence (password before key) is unchanged. Reachable from the server add/edit dialog (**„Speichern & Onboarden"**) or the per-row onboarding button.

See [docs/ARCHITECTURE.md](../../../../docs/ARCHITECTURE.md) > *Onboarding a new server* for the full step sequence.

## Files

| File | Purpose |
|---|---|
| `IOnboardingService.cs` / `OnboardingService.cs` | Orchestrates onboarding into the Tailscale + mTLS-proxy + telemetry stack, end to end, reporting progress (incl. surfacing the Tailscale login link in the app). Returns a step-tracked `OnboardingResult`. |
| `ScrapeConfigEditor.cs` | Step 8 without Python (contract 0017 I1): the scrape config is read base64 from the Whiskers host, the node_exporter target is inserted in C# under the first `static_configs:` (the rest of the file kept line for line), and written back as root through `WriteFileAsRootB64` (`sh` when root, else `sudo sh`, like the CA steps; decoded into a temporary file first). The read-modify-write is serialised by a static gate, and invalid UTF-8 fails the step. Server id and address are double-quoted YAML scalars from YamlDotNet's emitter; the result is parsed again and must carry exactly that target. A target already present for the address changes nothing; a file without `static_configs:` is left alone with a warning. |
| `OnboardingResult.cs` | The `OnboardingStep` enum + result record (completed steps, failed step, actionable German hint per step). Every step is idempotent → a re-run after failure resumes safely (W3). |
| `OnboardingCommands.cs` | Pure, unit-tested command/config builders (`OnboardingCommandsTests`): user input only reaches the shell through the strict `Slug` allow-list or base64; the tailnet IP passes only as a normalised IPv4 literal (`TailnetIPv4`: `AddressFamily.InterNetwork`, then `ToString()`; an IPv6 zone id such as `fe80::1%';…` is refused), and only that normalised value is used afterwards. `ReadFileB64` reads a code-owned file base64-encoded. R1-58: `NodeExporterCompose` (a copy of `deploy/telemetry/host/docker-compose.yml`) enables the textfile collector on `/host/var/lib/node_exporter/textfile_collector` through the existing read-only `/:/host` mount, for the optional SMART readings. |

## Related

- Architecture: [`../../../../docs/ARCHITECTURE.md`](../../../../docs/ARCHITECTURE.md)
- Deploy templates: [`../../../../deploy/telemetry/`](../../../../deploy/telemetry/)
- Server registry: [`../ServerConfig/`](../ServerConfig/)

# Contract 0018 — One-step server onboarding (and MCP tools for it)

Status: **Accepted — 2026-11-16 by the maintainer.** D1, D3, D5 and D6 as stated under "Decisions"; D2, D4 and
D7–D12 as recommended.

Scope:
- **Protected areas, untouched:** the auth middleware and its order, the OAuth whitelist, the mTLS/WireGuard layer
  (onboarding deploys ghostunnel and the socket proxy exactly as today, contract 0007 unchanged), and the
  docker-socket-proxy verb whitelist.
- **Protected area, approved:** the SSH host-key handling of ADR-0002 (HOCH-11), which ADR-0002 counts among the
  protected SSH paths. The maintainer explicitly approved touching it on **2026-11-16** (D3), for part C only: pinning
  and recording a first contact, and replacing a pin by an interactive Admin. The bug-fix PR (part A) does **not**
  touch it.

## Purpose

On 16 November 2026 the maintainer onboarded five Hetzner CX23 servers. It took over an hour:

1. The server form lost values. The list showed `@:22`, and onboarding failed with
   `Permission denied (publickey,password)`. The IP typed into the TCP field did not appear in the SSH field.
2. "Retry" in the onboarding window kept working with old state, including an old password that wins over the key.
3. Servers created in Hetzner with the project key have no root password login. The maintainer generated a bootstrap
   key by hand and pushed it to each server with `ssh -i … -o StrictHostKeyChecking=accept-new`.

After this contract:

- a Hetzner server is onboarded from **name, IP and the Hetzner token Whiskers already holds**, in one action;
- the connection type is set by Whiskers (SSH for the bootstrap, then TCP + mTLS), not chosen by the user;
- the SSH host key of the first contact is pinned, recorded, shown and audited;
- two MCP tools, `add_server` and `onboard_server` (plus a status read), do the same over MCP. A cloud API key never
  comes in over MCP.

## Current state (verified at `9b678c1f`)

Paths are relative to `src/Whiskers/`.

### The server form (`Components/Pages/Servers.razor`)

| # | Where | Finding |
|---|---|---|
| F1 | `Servers.razor:184-249` | The connection type is a `MudSelect` on `_form.ConnectionType`. The TCP branch binds `_form.TcpHost` (`:192`), the SSH branch binds `_form.SshHost` (`:231`). They are two different properties, so an IP typed under TCP is not in the SSH field after a switch. Switching the type does **not** clear any field in code: both sets stay in the one `_form` object. The reported clearing of SSH host and user is **not reproducible from the code**; part A pins it with a component test first. |
| F2 | `Servers.razor:611-621`, `:290`, `:296` | SSH host, user and port are validated only when a value is present. "Save" and "Save and onboard" are disabled only for an empty name. An SSH server with an empty host and user can be saved; the list renders it as `{SshUser}@{SshHost}:{SshPort}` (`:90`) = `@:22`. |
| F3 | `Servers.razor:719-727` with `Services/ServerConfig/ServerConfigService.cs:356-360` | **Edit + key upload loses the key.** `SaveSshKeyAsync` writes the key and sets `SshKeyFileName` on the stored record; the next line writes `updated` (`Record()` = `_form.Clone()`, `:761`), whose `SshKeyFileName` is still the value cloned when the dialog opened (`:511`). The upload only sets the page field `_sshKeyFileName` (`OnSshKeyFileChanged`), never `_form.SshKeyFileName`. Result: a server that had no key keeps `SshKeyFileName = null` (the executor answers "SSH key not found"); a server that had a key under another file name keeps pointing at the **old** key file, which still exists → `Permission denied (publickey…)`. The same happens on the add path when the first save's probe failed and the user saves again (`:746` turns the second save into an edit). |
| F4 | `Servers.razor:508-520`, `:690`; `Models/ServerConfig.cs:64-68` | `SshPassword` is `[JsonIgnore]`: it lives only in the store's in-memory record. It is cleared by a save with the password switch off (`:690`), by a successful onboarding, or by a restart. After a **failed** onboarding it stays in the cached record. |

### Retry and credential precedence

| # | Where | Finding |
|---|---|---|
| R1 | `Servers.razor:344`, `:969-1000` | "Retry" calls `StartOnboarding(_onboardingServer, …)` with the object captured at the first start. That object is only used for the id and name: the gateway resolves the target again (`MutationTargets.cs:488-500`), `OnboardServerAsync` re-reads the record by exact id (`OnboardingService.cs:101`, again before the certificate step `:216`), and the executor reads it again per command (`HostCommandExecutor.cs:59`). So retry does **not** run on a stale object. |
| R2 | `HostCommandExecutor.cs:147-149`, `:191-195` | What is stale is the **store's** state: a password left in the cached record (F4) takes precedence over the key ("a transient root/SSH password … takes precedence; otherwise the stored key"). Together with F3 (old key file) this explains "retry keeps the old password / old key". |
| R3 | `OnboardingService.cs:293`, `:315`; `ServerConfigService.cs:241` | After success the password is cleared by `server.SshPassword = null` on the clone **after** `UpdateServerAsync(server)`. That works only because the store keeps the instance it was given (`servers[index] = server`). It is aliasing, not a store write. |
| R4 | `OnboardingService.cs:117-121` | Step `Bootstrap` is `echo ok` over SSH. A failure there only says the command failed; it does not say whether the host, the user, the key, the password or the host key was the problem. |

### The onboarding pipeline (`Services/Onboarding/*`)

- Ten steps, `OnboardingStep` `Bootstrap … Verify` (`OnboardingResult.cs:6-18`). Every step is idempotent; a re-run
  resumes (`OnboardingService.cs:23-27`).
- The whole run holds the per-host lock (`:93-98`), checks INV-59 state and the decided connection (`:108-115`,
  `:219-224`).
- The Tailscale login needs a person: the run polls for a login URL and reports it with `LinkMarker` (`:150-163`), then
  waits up to ~5 minutes (`:166-173`).
- On success it deletes the bootstrap key (`DeleteSshKeyAsync`, `:316`). It never removes the matching public key from
  the host's `authorized_keys` (today that key is the user's own, so that is correct).
- The UI path runs as capability `server.onboard` (Admin, root-equivalent, deadline `Long` = 30 min,
  `Services/Mutations/CapabilityCatalog.cs:292`), composing `certificate.issue_mtls` and `server.save`
  (`MutationCompositions.cs:48`). There is **no MCP tool** for onboarding or for adding a server; both capabilities are
  UI-only (`Ui(...)`, `CapabilityCatalog.cs:292-294`).
- Re-onboarding a host that already holds mTLS state changes the control plane (`ControlPlaneResources.cs:65-69`), so
  it needs an interactive Admin (INV-59, `BuiltInGuardrailRules.cs:211-216`).

### Cloud and Hetzner

- The Hetzner token is **per server**: `ServerConfig.CloudKeyRef` names a vault entry (`Models/ServerConfig.cs:86-98`,
  R1-29, INV-52); `CloudCredentials.KeyOf` reads it and fails closed on a locked vault (`Services/Cloud/CloudCredentials.cs:20-32`).
  Removing a server deletes its vault entry only when no other server names it (`ServerRemoval.cs:63`), so two
  records **can** share one reference safely.
- A Whiskers server is matched to a Hetzner VM by `SshHost ?? TcpHost` against the VM's public IPv4, and **falls back to
  the name** (`Services/Cloud/Providers/HetznerCloudProvider.cs:25-26`, `:44-65`).
- What the client wraps (`Services/Hetzner/HetznerApiService.cs`): list/get servers, power on, shutdown, reboot, reset,
  `enable_rescue` with body `{ type: "linux64" }` only (`:138-139`, **no `ssh_keys`**), disable rescue, snapshots,
  backups, change type, metrics. **Not wrapped:** `reset_password`, `/ssh_keys` (create/delete), `rebuild`,
  `request_console`, action polling by id.
- MCP `hetzner_enable_rescue` (Admin) returns the rescue root password in the tool answer (`Mcp/Tools/HetznerTools.cs:44-45`;
  §7 row M-31 already lists it under INV-51). This contract does not change that tool.

### What the Hetzner Cloud API offers (from knowledge of the public API; **not verified in this session**)

| Capability | Endpoint | Notes | Verified? |
|---|---|---|---|
| Reset root password | `POST /servers/{id}/actions/reset_password` → `{ action, root_password }` | Works only on a running server with the QEMU guest agent. On images created with SSH keys, `sshd` is usually configured without password login for root, so the new password may not open SSH at all. | ❓ not verified |
| Rescue with SSH keys | `POST /servers/{id}/actions/enable_rescue` `{ type, ssh_keys: [id…] }` → `{ action, root_password }` | Takes effect at the next boot (reset/reboot). The rescue system accepts the listed project keys. | ❓ `ssh_keys` parameter not verified |
| Project SSH keys | `POST /ssh_keys` `{ name, public_key, labels }` → `{ ssh_key: { id, fingerprint } }`; `DELETE /ssh_keys/{id}` | Needs a read-write token. | ❓ not verified |
| Rebuild | `POST /servers/{id}/actions/rebuild` `{ image }` | Wipes the disk. Out of scope. | ❓ |
| Console | `POST /servers/{id}/actions/request_console` → wss URL + password | VNC; not usable for automation. Out of scope. | ❓ |
| Host key out of band | — | No endpoint known that returns a server's SSH host key. | ❓ |

Verifying these against the real API is a `[braucht Nutzer/Hardware]` item (test plan, H1–H4).

### SSH host keys

- One policy for all three SSH spawn sites: `StrictHostKeyChecking=accept-new` with Whiskers' own known_hosts file
  `<data>/ssh-keys/known_hosts` (`Services/Server/SshHostKeyPolicy.cs:14-38`, ADR-0002, HOCH-11).
- First contact is therefore **already pinned silently**: nothing is recorded in the server record, nothing is audited,
  nothing is shown, and the user cannot compare a fingerprint.
- A later key change makes ssh fail; the runbook is to delete the line by hand (ADR-0002 "Konsequenzen"). A
  Hetzner IP reused from a deleted server fails the same way, with an ssh error the UI does not explain.
- **No INV row covers SSH host keys.** The rule lives in ADR-0002 only.

### MCP permissions

- Levels come from the capability catalog (INV-13); `McpPermissionLevels.DefaultToolLevels` takes them from
  `CapabilityCatalog.McpToolLevels` (`Models/McpPermission.cs`, `CapabilityCatalog.cs:98-101`). A tool without a level
  resolves to Admin (INV-14).
- Direct MCP `Confirm` verdicts become approve-then-retry (`Services/Guardrails/DirectMcpCallGuard.cs:16-22`); the
  requester cannot approve its own non-interactive request (INV-33).
- `AgentToolRegistry.NonAgentTools` keeps credential-handling tools away from the in-process agent
  (`Services/Agent/AgentToolRegistry.cs:30-35`).

## Changes

The work comes in three PRs, in this order:

| PR | Part | Waits for this contract? |
|---|---|---|
| 1 | A — form and retry bugs | **No** (D4): plain bugs, no new interface, no protected area. |
| 2 | B, C, D — registration, cloud bootstrap, host-key pin, onboarding steps | Yes. C is covered by the ADR-0002 approval of 2026-11-16 (D3). |
| 3 | E — MCP tools | Yes; after PR 2. |

### A — Form and retry bugs (PR 1, `Servers.razor`, `ServerConfigService`)

- **A1 (F3).** A key upload sets `_form.SshKeyFileName` to the uploaded name before `Record()` is taken, on the add and on
  the edit path. The store write that follows keeps the file name `SaveSshKeyAsync` set. Repro test first.
- **A2 (F2).** For `ConnectionType.SSH` the save refuses an empty host or an empty user with a field message; the two
  buttons stay disabled while either is empty. A record with `@:22` can no longer be written.
- **A3 (F1).** Switching the type to SSH fills an **empty** `SshHost` from `TcpHost` (and the other way round). A filled
  field is never overwritten. A component test (bUnit) switches TCP → SSH → TCP → SSH with values typed in between and
  asserts nothing is lost; if it reproduces the reported clearing, the cause is fixed in the same PR.
- **A4 (F4, R2).** A key upload clears `SshPassword` on the record it writes; turning the password switch on clears
  nothing else. The edit dialog opens with the password switch **on** when the record still holds a password, so the
  user sees which method will be used. The executor is not changed (D4: precedence stays as it is; the record now never
  carries both after a save).
- **A5 (R1, R4).** The onboarding window states before each run which credential the bootstrap will use (key file name
  or "password", never the value) and the SSH destination. "Retry" shows that line again; it already re-reads the record.
- **A6 (R3).** Clearing the password after success goes through a store write on a clone, not through the alias.
  The clone is taken from the record the run already holds with `SshPassword = null` set on it; the write never
  re-reads the store's cached record, so a stale `SshPassword` cannot come back with it.

### B — Registering a server from name, IP and an existing cloud credential

New service, interface-first (`Services/Onboarding/IServerRegistration.cs`):

```csharp
/// <summary>Adds a server from the few facts a person knows: name, public IP, group, cloud provider. Capability
/// "server.create" (Admin), decided and audited through the gateway. Never takes a cloud API key.</summary>
public interface IServerRegistration
{
    [MutatingOperation("server.create")]
    Task<ServerRegistrationResult> AddAsync(ServerRegistrationRequest request, CancellationToken ct);
}

public sealed record ServerRegistrationRequest(
    string Name,
    string PublicIp,
    string? Group,
    CloudProvider CloudProvider,
    /// <summary>Optional: the server whose stored cloud credential to reuse. Null = find it by IP (D7).</summary>
    string? CloudCredentialFrom);

public sealed record ServerRegistrationResult(string ServerId, CloudBinding? Cloud, IReadOnlyList<string> Warnings);

/// <summary>The provider VM the record was bound to at registration: by exact public IP, never by name.</summary>
public sealed record CloudBinding(CloudProvider Provider, long CloudServerId, string CloudServerName,
    string? CloudServerCreated, IReadOnlyDictionary<string, string> Labels, string Status);
```

Rules:

1. `Name`: trimmed, 1–64 characters; the id is `OnboardingCommands.Slug(Name)`, made unique with a numeric suffix.
2. `PublicIp`: an IPv4 or IPv6 **literal** (`IPAddress.TryParse` plus `CommandOperands.IsSafeHost`), not loopback,
   link-local, private, CGNAT or ULA. A hostname is refused (the IP is what the cloud match uses). With
   `CloudProvider = Hetzner` only an **IPv4** literal is accepted ("Hetzner servers are matched by their public IPv4"):
   the Hetzner match compares `HetznerServer.Ipv4` only (`HetznerCloudProvider.cs:44-48`, `Models/Hetzner/HetznerModels.cs`
   `Ipv4 => PublicNet?.Ipv4?.Ip`), so an IPv6 address could never bind a VM. IPv6 stays allowed for `none`.
3. Refused when another record already has this IP as `SshHost` or `TcpHost`, or is bound to the same `CloudServerId`.
4. The record is created as `ConnectionType.SSH`, `SshHost = PublicIp`, `SshUser = "root"`, `SshPort = 22` (D10), no key,
   no password, `Enabled = true`. The user never chooses the connection type; onboarding moves it to TCP + mTLS.
5. Cloud credential (D7): with `CloudCredentialFrom`, that server's `CloudKeyRef` (same provider required). Without it,
   every distinct stored credential of that provider lists its VMs; **exactly one** VM in exactly one credential must
   have this public IP. Zero or several → refused with a message that names the candidate servers (never a token). The
   new record **shares** the vault reference (`CloudKeyRef`); no secret is copied, nothing is written to the vault.
6. The record stores the binding: new field `CloudServerId` (see persisted data). From then on the bootstrap acts on that
   id and checks it still has this IP.
7. No credential of the provider at all → the record is created without a binding and the result says so; the UI then
   offers the existing key field (the token is entered by a person, in the UI only).

### C — SSH host key: pin on first contact, record, show, audit (ADR-0002 area, D3)

```csharp
public enum SshHostKeySource { FirstContact, RescueDisk, Operator }

/// <summary>The pinned host key of a server's SSH endpoint (host, port).</summary>
public sealed record SshHostKeyPin(string KeyType, string FingerprintSha256, SshHostKeySource Source,
    DateTime PinnedAtUtc, string? OperationId);

public interface ISshHostKeyPins
{
    /// <summary>The pin recorded for the server's SSH endpoint, or null.</summary>
    SshHostKeyPin? PinOf(ServerConfig server);

    /// <summary>Reads the endpoint's keys with ssh-keyscan, refuses when known_hosts already holds a DIFFERENT key for
    /// this endpoint, otherwise writes the line and records the pin (FirstContact). Runs only inside "server.onboard".</summary>
    [MutatingOperation("server.onboard")]
    Task<SshHostKeyPin> PinFirstContactAsync(ServerConfig server, CancellationToken ct);

    /// <summary>Pins keys read from the host's own disk (rescue path, part D) BEFORE the first contact with the real
    /// system. Same refusal on a different existing key.</summary>
    [MutatingOperation("server.onboard")]
    Task<SshHostKeyPin> PinFromDiskAsync(ServerConfig server, IReadOnlyList<string> publicKeyLines, CancellationToken ct);

    /// <summary>Replaces a pin. Capability "server.hostkey_replace": Admin, interactive only, with an Admin approval
    /// exactly as INV-59 (HOCH-11's "go"; see "Gateway and approvals"). Both fingerprints must match what the person
    /// saw; otherwise refused.</summary>
    [MutatingOperation("server.hostkey_replace")]
    Task<SshHostKeyPin> ReplaceAsync(string serverId, string expectedOldFingerprint, string expectedNewFingerprint,
        CancellationToken ct);
}
```

- Fingerprints are computed by `ssh-keygen -lf` (OpenSSH in the image, `Dockerfile:79-81`), never by our own code.
  Preference order of key types: ed25519, ecdsa, rsa; all types the host offers are pinned.
- **Which key the record mirrors.** known_hosts holds every type; the record holds one: the first type of the preference
  order the host offers (ed25519, else ecdsa, else rsa). Step `HostKey` and the pin methods compare per type: for
  **every** type known_hosts holds for the endpoint, the key `ssh-keyscan` returns for that type must be the same, and
  the record's type must be among them with the recorded fingerprint. A type the host no longer offers is not a
  mismatch; a different key of any type is.
- **Reading and removing lines.** The runtime image is Debian (`mcr.microsoft.com/dotnet/aspnet`, `Dockerfile:55`), whose
  `ssh_config` may set `HashKnownHosts yes`, so lines ssh appends itself can be hashed and cannot be found by text.
  Lookup always uses `ssh-keygen -F <name> -f <known_hosts>`, removal `ssh-keygen -R <name> -f <known_hosts>`, where
  `<name>` is written as ssh records it: the bare host for port 22, `"[host]:port"` for any other port. This holds for
  the pin methods, the backfill and `ReplaceAsync`. Lines Whiskers writes itself are written in plain form.
- The known_hosts file and `SshHostKeyPolicy.Options()` stay as they are (`accept-new`), so existing servers keep
  working. The difference: onboarding pins **explicitly** before its first SSH command, records the pin and audits it.
  Every later SSH call on any path then meets a present entry, so a changed key fails as today.
- A mismatch during onboarding stops at the new step `HostKey` with both fingerprints and a hint (reused IP, rebuild, or
  an attack). Nothing is replaced automatically, on any path, for any principal.
- The server list shows the pinned fingerprint (short form) and its source; the edit dialog shows it in full.
- Existing servers without a recorded pin: a read-only backfill reads the line from known_hosts (`ssh-keygen -F`, above)
  and records it as `FirstContact` with `PinnedAtUtc = null` ("pinned before 0018"). No network access, no audit event
  (no change to what ssh trusts). INV-63 therefore covers onboardings run after 0018; a backfilled pin is shown, but it
  was never audited.

### D — Cloud bootstrap and the onboarding steps

#### Bootstrap methods (D1: rescue plus key injection; no password reset)

```csharp
public enum BootstrapMethod
{
    /// <summary>Use the credential on the record (uploaded key or password). Today's behaviour.</summary>
    None,
    /// <summary>Hetzner only: generate a key, boot the rescue system with it, append the public key to the real
    /// system's root authorized_keys, read the real host keys from disk, boot back.</summary>
    HetznerRescueKey,
}

/// <param name="ConfirmVmName">The VM name the person confirmed when it differs from the record's name (precondition 1);
/// null otherwise. Part of the call's arguments, so the approval binds it.</param>
/// <param name="AbandonBootstrap">Runs only `BootstrapCleanup` for a run that failed after injection (see "Standing
/// bootstrap key"), then ends.</param>
public sealed record OnboardingOptions(BootstrapMethod Bootstrap, string? ConfirmVmName = null,
    bool AbandonBootstrap = false);
```

There is no password-reset method (D1); `reset_password` is not wrapped.

#### Flow of `HetznerRescueKey` (new class `HetznerRescueBootstrap : ICloudBootstrap`)

```csharp
public interface ICloudBootstrap
{
    BootstrapMethod Method { get; }
    /// <summary>Gives Whiskers SSH access to a freshly created VM. Runs only inside "server.onboard".</summary>
    [MutatingOperation("server.onboard")]
    Task<CloudBootstrapResult> PrepareAsync(ServerConfig server, IProgress<OnboardingProgress> progress, CancellationToken ct);
}

public sealed record CloudBootstrapResult(bool Success, string? Error, SshHostKeyPin? HostKeyFromDisk,
    string? InjectedKeyFingerprint, bool StandingBootstrapKey, IReadOnlyList<string> CleanupWarnings);
```

1. **Preconditions (fail closed).** Provider Hetzner; a binding (`CloudServerId`) exists; the VM by that id still has the
   record's public IP (else refused: "IP and VM no longer match"); the VM is `running`; the record has **no** host-key
   pin and **no** mTLS state (D2: fresh servers only); no other record is bound to the VM; the token works for writes.
   **VM name check:** the VM's current name must equal the record's name (ordinal, ignoring case). If it differs, the
   run is refused ("the IP belongs to VM '{vmName}', not '{recordName}'") unless `ConfirmVmName` equals the VM's
   current name exactly; a stale confirmation (the VM was renamed meanwhile) is refused as well. D2's "fresh" check
   cannot tell a mistyped IP that hits another fresh VM of the same project; the name check and the VM facts in the
   dialog and on the approval card (below) are what a person compares.
2. **Key.** Prerequisite: contract 0015 Part A, which is in main (`Services/Server/SshKeyLeases.cs`: sealed keys and
   leases in a memory-backed directory under `/dev/shm`, refused when it is not `tmpfs`/`ramfs`). PR 2 builds on it and
   adds no lease directory of its own. `ssh-keygen -t ed25519 -N "" -C whiskers-bootstrap-{serverId}-{opId8}` writes
   into a fresh 0700 directory under the same memory-backed base (`SshKeyLeases.DefaultBaseDir`, same
   memory-backed check); the private key is stored sealed with `SaveSshKeyAsync` and the plaintext file removed at
   once. Every later use goes through `ISshKeyLeases.AcquireAsync`. The record's `SshPassword` is cleared.
   **Resume after injection.** While `BootstrapKeyInjectedFingerprint` is set (a run failed after step 6, e.g. in
   `disable_rescue`, a reboot or the pin), `CloudBootstrap` never generates a new key: it reuses the sealed key whose
   public fingerprint equals that field, and steps 3–8 run again with it (step 6 is idempotent by marker). If that key
   is missing or its fingerprint differs, the run is refused before any provider call ("the bootstrap key of the
   earlier run is gone; abandon the onboarding first") and points to abandon.
3. **Project key.** `POST /ssh_keys` with name `whiskers-bootstrap-{serverId}-{opId8}` and label
   `managed-by=whiskers`; its id is kept in the run (and in the audit details) for the cleanup.
4. **Rescue.** `enable_rescue` with `ssh_keys = [id]`, then a **soft reboot** (`IHetznerService.RebootAsync`, ACPI
   `reboot`, `HetznerApiService.cs:135`); if the VM has not gone down and come back within
   `Onboarding:CloudBootstrap:SoftRebootSeconds`, a **hard reset** (`ResetAsync`, `:136`) follows once. Then poll until
   the VM runs and port 22 answers (bounded by `BootTimeoutSeconds`). The audit names which of the two happened.
   "Came back" needs an **observed down-then-up**: port 22 must first stop answering (connection refused or timeout)
   and then answer again, or the SSH host keys served on port 22 must differ from the ones served before the reboot
   (`ssh-keyscan`; into rescue: the real system's keys go away, back: the rescue keys go away). A port that merely
   answers throughout does not count; the old system may simply not have shut down yet.
5. **Rescue session.** SSH to the rescue system with the new key, using a **separate, run-scoped known_hosts file**
   (`StrictHostKeyChecking=accept-new` against an empty file in the run's temp directory). This session is
   **trust on first use on the rescue system**: nothing verifies the rescue system's host key. The rescue system's key
   never enters `<data>/ssh-keys/known_hosts`.
6. **On the real disk** (one fixed script built by a unit-tested command builder, values only through base64 or
   `HostCommandSecrets`): find the root partition (the one holding `/etc/ssh/sshd_config` and `/root`), mount it
   read-write, append the public key line with its marker comment to `/root/.ssh/authorized_keys` (create with 0700/0600
   when missing, idempotent by marker), read `/etc/ssh/ssh_host_*_key.pub`, unmount, `sync`. Once the append has
   succeeded, the record's new field `BootstrapKeyInjectedFingerprint` is set (store write) **before** anything else
   runs; from here on the host carries the line.
7. **Back.** `disable_rescue`, then soft reboot with the hard-reset fallback as in 4; poll as in 4.
8. **Pin.** `PinFromDiskAsync` with the keys read in 6 (source `RescueDisk`). The first contact with the real system is
   therefore compared with keys read from its own disk. Those keys were read through the rescue session of step 5,
   which is trust on first use on the rescue system: the pin is as good as that one session (an attacker on the path
   at that moment could feed other keys). It is not trust on first use of the real system's sshd.
9. **Always (finally):** delete the project SSH key resource; if rescue is still enabled, disable it; if the VM is still
   in rescue after a failure, reset it once more so it boots its own system. Every step that fails here is reported as a
   cleanup warning in the result **and** makes the run `Failed` when it leaves the VM in rescue.

The rescue root password Hetzner returns is never read into a variable that outlives the call, never logged, never
returned (the injected key is the access).

#### Standing bootstrap key (a run that fails after injection)

A run can fail after step 6 (rescue exit, pin, `Bootstrap`, Tailscale, certificate …). Then the host still carries the
marker line, Whiskers still holds the sealed private key, `BootstrapKeyGenerated` stays true, and a resume uses that
key. This is intended (resume needs it), but it is **standing access** and is treated as such:

- **Marked.** A run that ends `Failed` while `BootstrapKeyInjectedFingerprint` is set carries
  `OnboardingResult.StandingBootstrapKey = true` and the warning "standing bootstrap key {fingerprint} in
  /root/.ssh/authorized_keys of {server}: resume the onboarding, abandon it, or remove the server". The warning is shown
  in the run window and as a badge in the server list, written to the audit details of the run
  (`standingBootstrapKey: true`, the public fingerprint), and returned by `onboard_server` and
  `get_onboarding_status`.
- **Removed by** whichever comes first:
  - a resumed run that reaches `BootstrapCleanup`;
  - **abandon**: `server.onboard` with `AbandonBootstrap = true` (UI button "Remove bootstrap access", MCP
    `onboard_server` with `abandon: true`) runs only `BootstrapCleanup`, over the mTLS host shell when the host has it,
    otherwise over SSH with the generated key. When the sealed key is gone and there is no mTLS shell, no host command
    is possible: abandon clears `BootstrapKeyInjectedFingerprint` and records the dead line (fingerprint, host) as a
    warning in its result and audit; the line has no private key anywhere and grants nothing.
    - **Host with mTLS state.** Abandon is `server.onboard`, which INV-59 treats as a control-plane capability when the
      host holds mTLS state (`ControlPlaneResources.CapabilitiesWhenStateExists`, `ControlPlaneResources.cs:66-69`).
      Over MCP it is then denied like every re-onboarding (`ProtectedResourceRule`, `Deny` for a key). In that case
      abandon runs only from the UI, by an interactive Admin with the INV-59 approval. The MCP refusal says so: "the
      host holds mTLS state; remove the bootstrap access in the UI (Servers → Remove bootstrap access)".
  - **`server.remove`**: before the record goes, `ServerRemoval` runs the same cleanup (see "Cleanup in
    `server.remove`" below). If the host cannot be reached (VM deleted, network down), the removal still goes on: the
    sealed private key is deleted with the record, so the remaining line has no private key anywhere and grants
    nothing. The removal result and its audit details carry the standing-key warning with the dead line (fingerprint,
    host).
- **Which lines.** `BootstrapCleanup` removes **every** line whose comment matches `whiskers-bootstrap-{serverId}-*`,
  not only the one of the current fingerprint, so a line left by an earlier run on this server is removed as well.
  Lines of other comments are never touched.
- **Order inside `BootstrapCleanup`:** remove the marker lines from `/root/.ssh/authorized_keys` first, read the file
  back and check none is left, clear `BootstrapKeyInjectedFingerprint`, **then** delete the sealed key
  (`DeleteSshKeyAsync`) and clear `BootstrapKeyGenerated`. If removing the line fails, the run is `Failed` (not
  `Success`) with the standing-key warning, and the sealed key is kept so that a resume or abandon can retry; only
  `server.remove` deletes the key without the line gone (above).

#### Cleanup in `server.remove`

Today `ServerRemoval` (`Services/ServerConfig/ServerRemoval.cs:15-16`, `[MutationHandler("server.remove")]`, a static
class) runs no command on a host; its only composition is `vault.secret_delete` (`MutationCompositions.cs:46`). For a
record with `BootstrapKeyInjectedFingerprint` set, 0018 adds one host command:

- **How.** `ServerRemoval.RemoveAsync` gains an optional `IBootstrapCleanup? bootstrapCleanup = null` parameter (as it
  already takes `clientCredentials`). `IBootstrapCleanup.RemoveMarkerLinesAsync(ServerConfig, CancellationToken)` is
  marked `[MutatingOperation("server.remove")]` and runs the fixed removal command of the rescue command builder
  (no caller text, values only through base64) through the host executor: the mTLS host shell when the host has it,
  otherwise SSH with the generated key via `ISshKeyLeases`. It runs **before** the record is removed.
- **Composition.** `MutationCompositions` gains `("server.remove", ["vault.secret_delete", "host.exec"])`: the
  removal is one decision that may run this one root command, never a second decision that could be refused halfway
  (R1-48). `host.exec` is Admin like `server.remove` (`CapabilityCatalog.cs:253`, `:295`), so composed ≤ composing
  holds.
- **Deadline.** Inside `server.remove`'s `Short` deadline (2 minutes, `CapabilityCatalog.cs:80`, `:295`); the host
  command has its own bound of 30 seconds. A timeout counts as unreachable.
- **Unreachable host.** The removal still proceeds: the record and the sealed key go, and the result and the
  `server.remove` audit details carry the standing-key warning with the dead line (fingerprint, host).
- **Coverage.** INV-38: the new marked operation → `MutationBypassArchitectureTests`, and the composition →
  `MutationGatewayCoverageTests`. INV-41: the command → `RescueCommandsTests`. §7 U-20 names the cleanup (below).

#### Onboarding steps

`IOnboardingService` changes (additive overload; the old signature calls it with `BootstrapMethod.None` and a string
adapter for progress, so the existing UI and tests keep compiling during PR 2):

```csharp
Task<OnboardingResult> OnboardServerAsync(string serverId, bool mayReplaceMtlsState, string? decidedConnection,
    OnboardingOptions options, IProgress<OnboardingProgress> progress, CancellationToken ct = default);

public sealed record OnboardingProgress(OnboardingStep Step, string Message, string? TailscaleLoginUrl = null);

// OnboardingResult (OnboardingResult.cs:22) gains, additively:
//     public bool StandingBootstrapKey { get; init; }   // see "Standing bootstrap key"
```

`OnboardingStep` gains three members, **appended** after `Verify`: `CloudBootstrap`, `HostKey`, `BootstrapCleanup`. Run order:

| Order | Step | What it does |
|---|---|---|
| 0 | `CloudBootstrap` | Only when `options.Bootstrap != None` (part D flow). Skipped (resume) when the record already has a `RescueDisk` pin and its key. Run again with the **same** key (never a new one) while `BootstrapKeyInjectedFingerprint` is set and no pin exists; refused when that key is gone (step 2). |
| 1 | `HostKey` | `PinOf` present → compare with `ssh-keyscan`; absent → `PinFirstContactAsync`. Mismatch → fail with both fingerprints. |
| 2 | `Bootstrap` | As today, plus a clear reason: host / user / key / password / host key (R4). |
| 3–10 | `TailscaleInstall` … `Verify` | As today. |
| 11 | `BootstrapCleanup` | Only for a key Whiskers generated: remove the marker line from `/root/.ssh/authorized_keys` through the mTLS host shell and check it is gone, **then** `DeleteSshKeyAsync` (order and failure as in "Standing bootstrap key"). A failure here makes the run `Failed`. For a user-supplied key: only `DeleteSshKeyAsync` (as today). |

Tailscale enrollment (D9): when the vault holds an onboarding auth key (`Onboarding:Tailscale:AuthKeyRef`), step 3 runs
`tailscale up --auth-key=file:<path>` with the key delivered as a `HostCommandSecrets` secret (as `TailscaleVpnProvider`
does for Whiskers itself); otherwise the login link as today.

#### UI flow (Servers page)

1. **Add server** opens a short form: Name, IP, Group, Cloud provider (preselected Hetzner when a Hetzner credential is
   stored). "Advanced" opens today's full form (TCP, SSH with key/password, Kubernetes).
2. Save → `server.create`. The dialog shows the matched VM: name, type, created, labels, status, and which stored
   credential matched (by server name).
3. **Onboard** shows the plan before anything runs: SSH destination, the credential the bootstrap will use, and — when
   the record has no key and no password and the provider is Hetzner — the offer
   "Get access via Hetzner rescue: the server is rebooted twice (a normal reboot each time, a hard reset only if it
   does not come back; about 3 minutes); its disk is not changed except for one line in /root/.ssh/authorized_keys,
   removed again at the end." The same dialog shows the bound VM as Hetzner reports it **now**: id, name, created
   time, labels and public IP. When the VM's name differs from the record's name, the dialog says so in a warning and
   the confirm button stays disabled until the person ticks "This is the right VM: '{vmName}'" (which sets
   `ConfirmVmName`). The user confirms that dialog; the click is the interactive Admin's decision (no auto-confirm).
4. The run window lists the steps with state, shows the pinned fingerprint and its source as soon as step `HostKey`
   finishes, and the Tailscale link (or "enrolled with auth key").
5. A host-key mismatch shows both fingerprints and, for an Admin, **Replace pin…**, which asks for both fingerprints to be
   confirmed and runs `server.hostkey_replace`. That call needs an Admin approval exactly as INV-59 (see "Gateway and
   approvals"). `server.hostkey_replace` is **not** in contract 0019's waves 1 or 2 (D7 there), so the call ends in
   today's `PendingApproval` refusal with the approval id; the Admin approves on the Approvals page (self-approval
   under that page's rules, four-eyes with *Require a second approver*) and clicks **Replace pin…** again, which uses
   the grant once. Adding it to the in-dialog list is a later wave of 0019.
6. A run that ended with a standing bootstrap key shows the warning and **Remove bootstrap access** next to "Retry".

### E — MCP tools

Registered in `ServerTools` (module `AllInOnePseudoModule`, `Modules/AllInOnePseudoModule.cs:36`), each with
`[McpToolLevel]` from the catalog. Errors use `McpErrorEnvelope` like the contract-0001 tools.

| Tool | Capability | Level | Parameters | Result |
|---|---|---|---|---|
| `add_server` | `server.create` (new) | admin | `name` (string), `ip` (string), `group` (string?), `cloudProvider` (`none`\|`hetzner`\|`hostinger`, default `none`), `cloudCredentialFrom` (server id?, D7) | `{ serverId, name, ssh: "root@<ip>:22", cloud: { provider, cloudServerId, cloudServerName, created, status } \| null, warnings[] , next: "onboard_server" }` |
| `onboard_server` | `server.onboard` (now also an MCP tool) | admin | `server` (id), `bootstrap` (`none`\|`hetzner_rescue`, default `none`), `confirmVmName` (string?, precondition 1), `abandon` (bool, default `false`: run only `BootstrapCleanup`) | `{ operationId, success, completedSteps[], failedStep, error, hostKey: { type, fingerprint, source }, tailscaleIp, standingBootstrapKey, cleanupWarnings[] }` |
| `get_onboarding_status` | read (no capability) | admin (D5) | `server` (id) | the in-memory snapshot of the latest run: `{ operationId, startedUtc, currentStep, finished, log[≤200], tailscaleLoginUrl?, standingBootstrapKey, result? }` |

`get_onboarding_status` is the first tool that is admin-level **and** has no capability:

- Its level comes from an explicit `[McpToolLevel(McpPermissionLevels.Admin)]` on the method
  (`Mcp/McpToolLevelAttribute.cs`) and a matching `["get_onboarding_status"] = Admin` entry in
  `McpPermissionLevels.DefaultToolLevels` (`Models/McpPermission.cs:98`); `McpToolLevelTests` keeps the two equal.
  It is not in the capability catalog, so `CapabilityCatalog.McpToolLevels` does not name it.
- `CapabilityTableConsistencyTests.Every_non_read_mcp_tool_has_a_capability` rejects today every tool whose level is
  not `read` and that has no capability, because such a tool is decided by `DirectMcpCallGuard`, not the gateway.
  PR 3 adds one named exception to that test, a set `AdminLevelReads = { "get_onboarding_status" }`, and a test that
  every member of the set has no capability and carries `[McpToolLevel(Admin)]`. The tool only reads
  `IOnboardingRuns.Latest`; adding a name to the set is a reviewed change of that test.
- Consequence at run time: `DirectMcpCallGuard` evaluates it with level `admin` (`DirectMcpCallGuard.cs:57`), so
  read-only mode (`ReadOnlyModeRule`) blocks it and a preset whose `ConfirmationRule` asks above the autonomous level
  makes it approve-then-retry. See "Open point".

- **No cloud key parameter** on any tool. `add_server` reuses a stored credential or none.
- `onboard_server` blocks until the run ends (D8). It sends MCP progress notifications when the client passed a progress
  token, one per `OnboardingProgress`. The run's token is the gateway's deadline (`Long`, 30 min), **not** the HTTP
  request's abort token: a client that disconnects does not cut a run in the middle of a reboot. The status tool shows
  how it went.
- New in-memory registry (no persistence):

```csharp
public interface IOnboardingRuns
{
    OnboardingRunSnapshot? Latest(string serverId);
}
public sealed record OnboardingRunSnapshot(string ServerId, string OperationId, DateTime StartedUtc,
    OnboardingStep? CurrentStep, bool Finished, OnboardingResult? Result, IReadOnlyList<string> Log,
    string? TailscaleLoginUrl);
```

- `onboard_server` and `get_onboarding_status` go into `AgentToolRegistry.NonAgentTools` (D6); `add_server` is offered to
  the agent.

### Persisted data (additive; `servers.json` via `JsonFileStore`, no database table changes)

| Field on `ServerConfig` | Type | Meaning |
|---|---|---|
| `CloudServerId` | `long?` | The provider VM bound at registration or at the first bootstrap. Null on existing records. |
| `SshHostKeyType` | `string?` | e.g. `ssh-ed25519`. |
| `SshHostKeyFingerprint` | `string?` | `SHA256:…` as `ssh-keygen -l` prints it. Public data. |
| `SshHostKeySource` | `SshHostKeySource?` | `FirstContact`, `RescueDisk`, `Operator`. |
| `SshHostKeyPinnedAtUtc` | `DateTime?` | Null for the backfill. |
| `BootstrapKeyGenerated` | `bool` | True while the stored SSH key is one Whiskers generated (drives `BootstrapCleanup`). |
| `BootstrapKeyInjectedFingerprint` | `string?` | Public fingerprint of the marker line while it is on the host (set in step 6, cleared by `BootstrapCleanup`). Set on a record that is not being onboarded = standing bootstrap key. |

Old files load unchanged (all fields optional); a record written by 0018 loads in an older build because unknown JSON
fields are ignored. The known_hosts file stays the source of truth for ssh; the record mirrors it for display and audit.

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `Onboarding:Tailscale:AuthKeyRef` | unset | Vault reference of a Tailscale auth key for new hosts (D9). Set in the UI (Settings), never in config files or over MCP. |
| `Onboarding:CloudBootstrap:SoftRebootSeconds` | `60` | How long a soft reboot may take to bring the VM down and back before the one hard reset. |
| `Onboarding:CloudBootstrap:BootTimeoutSeconds` | `240` | Bound for each "wait until the VM runs and port 22 answers". |
| `Onboarding:CloudBootstrap:Enabled` | `true` | Off → `hetzner_rescue` is refused on every surface. |

## Security model

### Who may do what

| Capability | Level / role | MCP tool | Risk | Resolution | Notes |
|---|---|---|---|---|---|
| `server.create` (new) | admin / Admin | `add_server` | RootEquivalent (as `server.save`: the record decides where root commands go) | Creates (`CapabilityCatalog.Creates`) | Deadline `Short`. |
| `server.onboard` | admin / Admin | `onboard_server` (new) | RootEquivalent | Upsert (as today) | Deadline `Long`. Changes from `Ui(...)` to `Tool(...)`. |
| `server.hostkey_replace` (new) | admin / Admin | — (UI only; no MCP tool, not an agent tool) | RootEquivalent | Server | Interactive Admin only (D3), by INV-59's own mechanism: `Confirm` (an Admin approval) for the interactive Admin, `Deny` for every other principal, MCP and the agent included. |
| `hetzner.ssh_key_create`, `hetzner.ssh_key_delete` (new) | admin | — | Reversible | Composed only | Never decided on their own. |

`MutationCompositions` gains: `("server.onboard", [..., "hetzner.rescue_enable", "hetzner.rescue_disable",
"cloud.reboot", "cloud.hard_reset", "hetzner.ssh_key_create", "hetzner.ssh_key_delete"])` (`cloud.reboot` for the soft
reboot, `cloud.hard_reset` for its fallback). Every composed capability is Admin (`CapabilityCatalog.cs:310-314`), so
`MutationGatewayCoverageTests` (composed ≤ composing) holds.

### Gateway and approvals

- Every write goes through the gateway: `server.create` (UI and MCP), `server.onboard` (UI and MCP, now with the
  argument `bootstrap`), `server.hostkey_replace` (UI).
- **New built-in rule `CloudBootstrapRule`:** `server.onboard` with `bootstrap != none` is `Confirm`, **whatever the
  preset**.
  - It is added to the non-human engine only (`GuardrailEngine.CreateDefault()`, next to `ExposureChangeRule`), never
    to `GuardrailEngine.CreateForHumans()`, which keeps exactly its three rules (principal ceiling, kill switch,
    protected resources; `GuardrailEngine.cs:35-40`). Contract 0019's `InlineApproval.Applies` relies on that.
  - It is code, not policy: it reads no policy field, so no preset, tool list or saved policy can remove or weaken it.
  - A person in the UI is judged by the human engine, where the rule does not exist; there the confirmation dialog of
    UI step 3 is the decision. Every principal judged by the default engine (direct MCP keys, agents over MCP,
    triggers, automations) gets `Confirm`. Over MCP this is approve-then-retry, and the requester cannot approve it
    (INV-33).
  - **Approval binding (INV-30).** For `bootstrap != none` the resolver of `server.onboard` reads the bound VM from
    Hetzner and sets a new `MutationTarget.CloudVm` (`"{CloudServerId}|{vmName}|{publicIp}"`, gateway-set, never
    from the caller). `ApprovalBinding.Of` hashes it as `targetCloudVm`, next to `targetConnection`
    (`ApprovalBinding.cs:50-52`). The arguments hash already covers `bootstrap` and `confirmVmName`. A VM renamed,
    re-addressed or replaced after the approval therefore makes the grant unusable.
  - **VM not readable → Deny before any approval (INV-25).** If, for `bootstrap != none`, the resolver cannot read the
    bound VM (Hetzner API error or timeout, vault locked, no credential, no binding, an empty answer, or a VM without
    the record's public IP), the call is refused with `Deny` at resolution, before the policy runs: no approval is
    raised, nothing is shown on a card. A call with `bootstrap != none` never goes on with `CloudVm = null`.
  - **Approval card.** For rule evaluation only (as `MutationPolicy.WithResolvedTarget`, `MutationPolicy.cs:126-150`)
    the gateway adds `__resolvedCloudVm*` keys (id, name, created, labels, public IP); `CloudBootstrapRule` writes
    them into its reason ("Hetzner VM '{name}' (id {id}, created {created}, labels {k=v,…}) at {ip} will be rebooted
    twice"), so the approval card and the notification show the VM a second person approves. A caller's own `__…`
    keys never reach the rules (`MutationPolicy.cs:132-134`).
- **`server.hostkey_replace` uses INV-59's mechanism exactly.** The capability is added to
  `ControlPlaneResources.Capabilities` (`ControlPlaneResources.cs:56-62`): it rewrites Whiskers' own trust anchor for a
  host (`<data>/ssh-keys/known_hosts` in the data volume). `ProtectedResourceRule` then answers as for every INV-59
  capability (`BuiltInGuardrailRules.cs:208-216`): `Confirm` for an interactive Admin (`IsInteractiveAdmin`,
  `:242-246`), which needs an Admin approval (four-eyes with *Require a second approver*), and `Deny` for everyone
  else. No new rule; both engines already contain `ProtectedResourceRule`. The INV-59 row's list of capabilities
  gains `server.hostkey_replace`.
  - **Contract 0019:** the approval matches `ProtectedResourceRule`, but `server.hostkey_replace` is in neither wave 1
    nor wave 2 of 0019 (its D7). It keeps today's refusal with the approval id and is approved on the Approvals page
    (UI step 5). Both contracts state this.
- Re-onboarding a host with mTLS state stays as today: interactive Admin only (INV-59).
- The kill switch denies all of them (INV-21).

### Audit

One gateway event per attempt (INV-45). Details added by the run:

- `server.create`: name, IP, provider, the credential's **source server id** (never the token), the bound VM id and name.
- `server.onboard`: bootstrap method; VM id, name and public IP; whether the VM name differed and was confirmed;
  project SSH key id created/deleted; rescue enabled/disabled; soft reboots and hard resets done; the injected
  **public** key fingerprint; the pinned host-key type, fingerprint and source; `standingBootstrapKey` when the run
  ends with the line on the host; abandon runs; cleanup warnings; the server-record diff at the switchover (as today,
  R1-22).
- `server.remove`: for a record with `BootstrapKeyInjectedFingerprint`, whether the line was removed or left dead.
- `server.hostkey_replace`: old and new fingerprints, the endpoint, the approval id (as every INV-59 approval).

### Secrets

| Secret | Where it lives | How it stays out of sinks |
|---|---|---|
| Hetzner token | vault, `CloudKeyRef` (shared reference) | Read with `CloudCredentials.KeyOf` in process; never an argument, never in a result or audit detail. |
| Generated private key | sealed at rest (contract 0015 Part A), lease in the memory-backed directory | Deleted by `BootstrapCleanup` after the lines are gone, after a failed `CloudBootstrap` before injection, and by `server.remove`. Kept, with the standing-key warning, after a failure after injection, and reused (never replaced) by a resume while `BootstrapKeyInjectedFingerprint` is set. Never logged. |
| Rescue root password (API response) | not kept | Discarded at once; `HetznerActionResponse.RootPassword` is not passed on by the bootstrap. |
| Tailscale login URL | progress, run snapshot (memory) | Admin-only views; not written to the audit detail (the event says "login link issued"). |
| Tailscale auth key (D9) | vault | Delivered as a `HostCommandSecrets` secret (file / stdin), never in the command text. |
| Host-key fingerprints, public key | record, audit | Public data. |

### Fail closed

- No binding, IP and VM no longer match, VM name differs without a matching confirmation, VM not running, record not
  fresh, token read-only, vault locked → the bootstrap does not start.
- A failure after injection → `Failed` with the standing-key warning on every surface; a failed line removal in
  `BootstrapCleanup` → `Failed`, never `Success`.
- Any error between `enable_rescue` and `disable_rescue` → the finally block disables rescue, resets once, deletes the
  project key; the run is `Failed` and names what was left.
- A host-key mismatch → stop; no automatic re-pin on any path.
- Policy or approval errors → Deny (INV-25).
- `add_server` with zero or several VM matches → refused, no record written.

## Invariant and §7 impact

### New invariants (INV-63 and INV-64; coordinated with contract 0019, which takes INV-65)

INV-63 is free: contract 0015 named it only in a rejected alternative of its D1. With 0019's row, the highest pinned ID
in `RELEASE_GATE.md` becomes INV-65 once both contracts have landed.

| ID | Statement | Test |
|---|---|---|
| INV-63 | In every onboarding run after contract 0018, the host's SSH host key is pinned before Whiskers sends it a command; the pin (type, fingerprint, source) is recorded on the server and audited. (Pins backfilled from known_hosts for hosts onboarded earlier are recorded and shown, not audited.) A different key refuses every SSH path, and the pin is replaced only by an interactive Admin through `server.hostkey_replace`, with an Admin approval as INV-59 and audited with both fingerprints. | `SshHostKeyPinTests`, `HostKeyReplaceRuleTests`, `OnboardingServiceStepTests.Host_key_is_pinned_before_any_command`, `OnboardingServiceStepTests.Mismatch_stops_at_HostKey_and_runs_no_command` *(planned, 0018)* |
| INV-64 | A cloud bootstrap that changes access to a host acts only on the provider VM bound to the record and still holding its IP (never a name match; a VM name other than the record's only with the person's explicit confirmation), needs a human Admin's approval for every non-interactive principal, bound to the VM's id, name and public IP, and never stores or returns a provider password. A run that ends `Success` leaves no standing access: the provider key resource, the injected `authorized_keys` line and the generated private key are removed, in that order for line and key. A run that ends `Failed` after the injection reports the standing bootstrap key on every surface (UI, audit, `onboard_server`, `get_onboarding_status`) until a resume, an abandon or `server.remove` removes it; `server.remove` deletes the private key in any case. | `CloudBootstrapRuleTests`, `HetznerRescueBootstrapTests`, `OnboardingServiceStepTests.BootstrapCleanup_removes_the_injected_line_before_the_key`, `OnboardingServiceStepTests.Failed_after_injection_reports_standing_key`, `OnboardingServiceStepTests.Cleanup_failure_fails_the_run`, `OnboardingServiceStepTests.Resume_after_injection_before_pin_reuses_the_key`, `CloudBootstrapRuleTests.Unreadable_vm_refuses_without_raising_an_approval`, `ServerRemovalTests.Remove_takes_the_standing_bootstrap_line_and_key` *(planned, 0018)* |

### Existing invariants touched (status unchanged ✅)

- INV-13/14: three new catalog rows; `CapabilityTableConsistencyTests` covers the tool attributes.
- INV-19: `server.create`, `server.hostkey_replace` RootEquivalent → `RootEquivalentAdminTests` membership updated.
- INV-38: the new marked operations (including `IBootstrapCleanup.RemoveMarkerLinesAsync` under `server.remove`) →
  `MutationBypassArchitectureTests`, `MutationGatewayCoverageTests` (compositions, including `server.remove` →
  `host.exec`).
- INV-41: IP validation in `add_server`; the rescue script builder → `CommandOperandsTests`, `RescueCommandsTests`.
- INV-51: the rescue password and the Tailscale URL → `OnboardingSecretsSinkTests`.
- INV-52: no new plaintext credential; the shared reference → `ServerRemovalTests` (shared ref survives removal of one).
- INV-57: the bootstrap acts on the bound VM id → `CloudBindingResolutionTests`.
- INV-59: statement unchanged; its list of control-plane capabilities gains `server.hostkey_replace`
  (`ControlPlaneResources.Capabilities`) → `HostKeyReplaceRuleTests`. `onboard_server` over MCP on a host with mTLS
  state is denied → `ControlPlaneOnboardMcpTests`.
- INV-30: the onboarding binding gains `targetCloudVm` → `CloudBootstrapRuleTests`.

### §7 path matrix

| Row | Change |
|---|---|
| U-20 Servers add/update/delete | Adds the short "add server" form (`server.create`); SSH field validation (A2); key file name kept (A1); pinned host key shown; removal takes a standing bootstrap line and key (INV-64): one fixed root command on the host (composition `host.exec`, 30 s inside `Short`), and on an unreachable host the removal proceeds with the standing-key warning in the audit. |
| U-21 Server onboarding | Gains `bootstrap`, `confirmVmName`, abandon ("Remove bootstrap access"), the steps `CloudBootstrap`, `HostKey`, `BootstrapCleanup`; audit details as above; gap column: INV-63, INV-64. |
| U-41 (new) | Replace SSH host-key pin (`server.hostkey_replace`), Admin, interactive only, INV-59 approval (on the Approvals page; not in 0019's waves 1–2), `NotVerifiable` + reason (Whiskers' own record and known_hosts; read back). |
| M-50 (new) | `add_server` (`server.create`), admin, `NotVerifiable` + reason (Whiskers' own record; the tool reads it back), GW ✅ + details. |
| M-51 (new) | `onboard_server` (`server.onboard`), admin, `NotVerifiable` + reason (as U-21), GW ✅ + details; not offered to the agent. |
| M-52 (new) | `get_onboarding_status`, admin read; answers the run snapshot; not offered to the agent. |
| §7.6 totals | M 49 → 52, U 40 → 41, total 112 → 116. Contract 0019 adds no §7 rows (it annotates U-21 and others), so these numbers do not collide. |

`RELEASE_GATE.md`: highest invariant ID → INV-64 (INV-65 after contract 0019); the two new rows start ⚠️ until their
tests exist and are listed under *Open invariants* until then.

## Test plan

### Unit and component (PR 1)

- `ServersFormTests` (bUnit): `Edit_with_key_upload_keeps_the_new_file_name`, `Second_save_after_failed_probe_keeps_the_key`,
  `Ssh_without_host_or_user_cannot_be_saved`, `Switching_type_fills_empty_ssh_host_from_tcp_host`,
  `Switching_type_back_and_forth_loses_nothing`, `Key_upload_clears_the_password`,
  `Edit_dialog_shows_password_switch_on_when_record_has_one`.
- `OnboardingRetryTests`: `Retry_reads_the_record_again` (fake store changed between runs), `Bootstrap_line_names_the_credential_not_its_value`.

### Unit and integration (PR 2)

- `ServerRegistrationTests`: IP literal and range rules; `Hetzner_registration_accepts_only_ipv4`; duplicate IP / VM refused; id slug unique;
  `Credential_found_by_exact_ip_in_exactly_one_account`; `Zero_or_two_matches_refuse`; `Name_match_is_never_used`;
  `New_record_shares_the_vault_reference_and_writes_no_secret`.
- `SshHostKeyPinTests` (fake keyscan / known_hosts in a temp dir): first contact pins and records;
  `Different_existing_key_refuses_and_keeps_both_fingerprints`; backfill from known_hosts writes no audit event;
  `Fingerprint_comes_from_ssh_keygen` (process seam); `Hashed_known_hosts_lines_are_found_and_removed_with_ssh_keygen`;
  `Non_default_port_is_looked_up_as_bracketed_host_port`; `Record_mirrors_the_preferred_key_type`;
  `A_type_the_host_no_longer_offers_is_not_a_mismatch`.
- `HostKeyReplaceRuleTests`: `server.hostkey_replace` is in `ControlPlaneResources.Capabilities`; Deny for API key,
  agent, trigger, automation, Operator; Confirm (an Admin approval) only for an interactive Admin, in the human and in
  the default engine; the replace runs only with a used grant; wrong expected fingerprints refused.
- `HetznerRescueBootstrapTests` (fake `IHetznerService`, fake executor): happy path order (key create → rescue with
  `ssh_keys` → soft reboot → inject → read host keys → disable → soft reboot → pin → key delete);
  `Soft_reboot_that_does_not_come_back_falls_back_to_one_hard_reset`;
  `Failure_after_enable_rescue_disables_rescue_resets_and_deletes_the_key`; `Not_fresh_server_is_refused`;
  `Vm_with_another_ip_is_refused`; `Reboot_counts_only_after_port_or_host_key_went_away`; `Vm_name_differing_from_the_record_is_refused_without_confirmation`;
  `Confirmed_vm_name_must_equal_the_current_vm_name`; `Rescue_host_key_never_reaches_the_main_known_hosts`;
  `Rescue_password_is_not_returned_or_logged` (log capture); `Generated_key_is_written_only_to_a_memory_backed_directory`.
- `RescueCommandsTests`: the fixed script, idempotent marker, values only through base64 / secrets.
- `OnboardingServiceStepTests` (first tests of the pipeline itself, blueprint gap): step order with and without bootstrap;
  resume skips `CloudBootstrap` once pinned from disk; `Host_key_is_pinned_before_any_command` (the fake executor
  records no command before the pin is written); `Mismatch_stops_at_HostKey_and_runs_no_command`;
  `BootstrapCleanup_removes_the_injected_line_before_the_key` (only the marker line, only for a generated key, line
  checked gone before `DeleteSshKeyAsync`); `Failed_after_injection_reports_standing_key` (result, audit detail, run
  snapshot); `Cleanup_failure_fails_the_run` (line removal fails → `Failed`, key kept, warning);
  `Abandon_runs_only_BootstrapCleanup`; `Resume_after_injection_before_pin_reuses_the_key`;
  `Resume_after_injection_with_the_key_gone_is_refused_and_points_to_abandon`;
  `Cleanup_removes_every_bootstrap_line_of_the_server_and_no_other`.
- `ServerRemovalTests.Remove_takes_the_standing_bootstrap_line_and_key`; `Remove_of_an_unreachable_host_deletes_the_key_and_names_the_dead_line`;
  `Remove_runs_the_cleanup_before_the_record_goes_and_within_its_bound`.
- `CloudBootstrapRuleTests`: Confirm for direct MCP, agent, trigger with any preset; the rule is in
  `CreateDefault()` and not in `CreateForHumans()` (which keeps three rules); `No_preset_removes_the_rule`;
  `Approval_binding_changes_with_vm_id_name_and_public_ip`; `Unreadable_vm_refuses_without_raising_an_approval`; approval hash changes with `bootstrap` and
  `confirmVmName`; `Card_reason_names_vm_name_created_labels_and_ip`.
- `ServersOnboardDialogTests` (bUnit): `Onboard_dialog_shows_vm_name_created_labels_and_ip`;
  `Differing_vm_name_keeps_confirm_disabled_until_ticked`; `Standing_key_warning_offers_remove_bootstrap_access`.
- `HetznerApiServiceTests` (HttpMessageHandler fake): request bodies of `enable_rescue` with keys, `POST/DELETE /ssh_keys`.
- Catalog and architecture tests named under INV-13/19/38 above.

### Unit and integration (PR 3)

- `OnboardingMcpToolsTests`: levels from the catalog; no key parameter on any tool (reflection);
  `Onboard_server_with_rescue_returns_pending_approval_then_runs_once`; client disconnect does not cancel the run;
  progress notifications per step; `Status_returns_latest_snapshot_admin_only`;
  `Onboard_server_and_status_report_a_standing_bootstrap_key`; `Abandon_over_mcp_runs_only_the_cleanup`;
  `Abandon_over_mcp_on_a_host_with_mtls_state_is_denied_and_names_the_ui`.
- `CapabilityTableConsistencyTests.Admin_level_reads_have_no_capability_and_declare_admin` (the `AdminLevelReads`
  exception for `get_onboarding_status`).
- `AgentToolRegistryTests.Onboarding_tools_are_not_agent_tools`.
- `AuditPathMatrixTests` rows M-50…M-52, U-41.

### Real hardware `[braucht Nutzer/Hardware]`

- H1 Verify the Hetzner endpoints and bodies used (`enable_rescue` with `ssh_keys`, `/ssh_keys`), with a read-write
  token of a test project.
- H2 One fresh CX23: full `hetzner_rescue` run from the UI; check the root partition detection, that cloud-init does not
  rewrite `authorized_keys` at the next boot, and that the marker line is gone at the end.
- H3 The same over MCP with an approval by a second session.
- H4 A reused IP: host-key mismatch shown with both fingerprints; replace the pin in the UI.
- H5 Tailscale auth key enrollment (D9).
- H6 One fresh CX23 where the run is stopped after injection (e.g. the Tailscale step cut off): the standing-key warning
  is shown, and "Remove bootstrap access" removes the line on the real host.

## Plan triage

- [Claude-prüfbar] Everything in parts A–E with fakes; every test above except H1–H6; docs (`Services/Onboarding/README.md`,
  `Services/Hetzner/README.md`, ADR-0002 amendment, `docs/operations/troubleshooting.md` host-key section,
  SECURITY_INVARIANTS rows, PRODUCT_CONTRACT §7 rows, RELEASE_GATE, CHANGELOG).
- [braucht Nutzer/Hardware] H1–H6.

## Open point (review of 2026-11-16, needs the maintainer)

- **How the guard judges `get_onboarding_status`.** D5 makes it Admin only. With today's code the level a key needs
  and the level `DirectMcpCallGuard` judges with are the same value (`DefaultToolLevels`), so an admin-level read is
  judged like an admin write: blocked in read-only mode, and approve-then-retry under a preset that confirms above the
  autonomous level (see part E). This contract keeps that (fail closed). Judging it as a read while still requiring
  an Admin key would need a separate "guard level" per tool; that is a design change and is not made here.

## Decisions (maintainer, 16 November 2026)

1. **D1 Bootstrap method.**
   - **Decided:** rescue plus key injection (`HetznerRescueKey`) is the automatic path for Hetzner. A user-supplied key
     (today's path, fixed by part A) stays for everything else. No password reset is offered.
   - Reason: it works on any Hetzner Linux image, whatever `sshd`'s password setting, and it pins the real host key read
     from the disk. A password reset needs the QEMU guest agent, often does not open SSH on key-created servers, and
     leaves a root password nobody knows.
   - **Approval:** it changes access to a live server and reboots it twice. Over MCP it always needs a human Admin's
     approval (`CloudBootstrapRule`); in the UI the person confirms a dialog that names the reboots.
2. **D2 Fresh servers only.**
   - **Decided:** the rescue bootstrap runs only for a record with no host-key pin, no mTLS state and a VM bound by exact IP.
   - Reason: it reboots; a mistyped IP of a production VM must not be one approval away from two reboots.
3. **D3 Host-key pin (ADR-0002 / HOCH-11, protected SSH path).**
   - **Decided — APPROVED:** pin explicitly before the first command, record, audit and show the host key, and refuse a
     mismatch. The rescue path pins the key read from disk; `accept-new` stays for the other paths.
   - Only an interactive Admin may replace a pin (`server.hostkey_replace`). MCP and the agent never may.
   - **Protected area:** the maintainer explicitly approved touching the protected SSH host-key path (ADR-0002, HOCH-11)
     on **2026-11-16**, for part C as written.
   - Reason: today's first contact is pinned silently, with nothing recorded, audited or shown. HOCH-11 requires a
     person's go for any key change.
4. **D4 Form and retry bugs as a separate PR now.**
   - **Decided:** yes. PR 1 ships without waiting for parts B–E. The fix is in the form and the store; the precedence in
     `HostCommandExecutor` stays unchanged.
   - Reason: F2, F3 and F4 are plain bugs with repro tests. None touches a protected area, and once a save never leaves
     both a key and a password, the precedence no longer matters.
5. **D5 MCP levels and approvals.**
   - **Decided:** `add_server`, `onboard_server` and `get_onboarding_status` are Admin only.
   - A bootstrap always needs an approval. Without a bootstrap, `add_server` and `onboard_server` follow the active
     preset.
   - Reason: the record decides where root commands run (as `server.save`), and onboarding installs as root (as
     `server.onboard`). The status log carries the Tailscale login link and host details.
6. **D6 Agent exposure.**
   - **Decided:** the in-process agent does not get `onboard_server` or `get_onboarding_status` (`NonAgentTools`). It does
     get `add_server`.
   - Reason: the agent cannot open the Tailscale link, and a run of up to 30 minutes with reboots does not belong in a
     chat turn.
7. **D7 Where the Hetzner token comes from.**
   - **Decided:** reuse a stored per-server credential by reference. Whiskers finds it by the VM's exact public IP across
     the provider's stored credentials, or the caller names it with `cloudCredentialFrom`. No secret is copied.
     Entering the token in the UI stays the fallback.
   - Reason: it needs no new entity and no migration, and makes "name + IP" enough. A global cloud account can follow
     when token rotation across many servers becomes a need.
8. **D8 MCP run model.**
   - **Decided:** `onboard_server` blocks until the run ends and sends progress notifications. The run is bound to the
     gateway deadline, not to the request. `get_onboarding_status` reads the snapshot.
   - Reason: decision, outcome and verification stay in one gateway execution (INV-39/43); no detached executions.
9. **D9 Tailscale enrollment.**
   - **Decided:** an optional onboarding auth key in the vault (`Onboarding:Tailscale:AuthKeyRef`, set in Settings);
     without it, the login link as today. Whiskers' own `Vpn:Tailscale:AuthKey` is not reused.
   - Reason: without a key, "one step" still waits for a person to click. Whiskers' own key has another lifetime and
     purpose.
10. **D10 Defaults of a registered server.**
    - **Decided:** `root`, port 22, SSH for the bootstrap, then TCP + mTLS.
    - Reason: Hetzner images log in as root; the user does not choose the connection type.
11. **D11 Bind the record to the VM id (`CloudServerId`).**
    - **Decided:** yes, for the bootstrap. The other cloud tools keep their IP-then-name resolution.
    - Reason: the bootstrap reboots and must act on exactly the VM it was registered with (INV-57). Switching every
      `cloud_*` tool to the id is a separate change.
12. **D12 Remove the injected key at the end.**
    - **Decided:** yes, step `BootstrapCleanup` over the mTLS host shell.
    - Reason: no dead root key line stays on the host.

## Out of scope

- Creating, rebuilding or deleting cloud servers.
- A root password reset and console access (D1).
- Hostinger bootstrap (registration and binding work; bootstrap `none` only).
- Changing `hetzner_enable_rescue`, which returns the rescue password (M-31).
- Switching the existing `cloud_*` tools to the VM id (D11).
- Any change to the mTLS layer, ghostunnel flags, the socket-proxy verb whitelist, auth middleware or OAuth whitelist.
- Strict `StrictHostKeyChecking=yes` on all SSH paths (ADR-0002 option 3); this contract only pins during onboarding.

## Amendment (implementation, 16 November 2026)

**Status: accepted by the maintainer on 16 November 2026.** What PR 2 built differently from the text above, or added to it.

- a) `ICloudBootstrap.PrepareAsync` has an overload with `OnboardingOptions`, which carries `ConfirmVmName`. The
  overload without options compares against no confirmed name.
- b) `ServerConfig.SshHostKeyPinnedAtUtc` is nullable: null means a pin backfilled from `known_hosts`.
- c) A `known_hosts` backfill is shown and kept in memory. It writes nothing to the record and raises no audit event.
- d) Compositions `server.create → server.save` and `server.hostkey_replace → server.save`.
- e) `server.create` is UI only until PR 3.
- f) The `HostKey` step is skipped for TCP (mTLS) records. For SSH records, removing a bootstrap line checks the host
  key first:
  - An abandon verifies a known key (a recorded pin or a `known_hosts` line). It refuses without the pins service and
    on a different key. With no known key (a line injected, nothing pinned: the VM may still be in rescue) it sends no
    SSH command and points to the resume, which pins the host keys read from the disk (review r2).
  - `server.remove` verifies a known key. Otherwise it sends no SSH command, names the dead line and goes on.
- g) The audit path for U-41 (`server.hostkey_replace`) and its §7 row come in PR 2, because R13 needs a §7 row for the
  new capability. M-50…M-52 stay in PR 3.
- h) The tests as named in code. Tests not in the classes listed in the PR 2 test plan:
  - `Resume_after_injection_before_pin_reuses_the_key` and
    `Resume_after_injection_with_the_key_gone_is_refused_and_points_to_abandon` are in `HetznerRescueBootstrapTests`.
  - `Cleanup_removes_every_bootstrap_line_of_the_server_and_no_other` is in `RescueCommandsTests`.
  - `Vm_with_another_ip_is_refused` is in `HetznerRescueBootstrapTests`.
  - `Unreadable_vm_refuses_without_raising_an_approval` and `A_readable_vm_raises_an_approval_naming_it` are in
    `CloudBootstrapRuleTests`.
  - `OnboardingSecretsSinkTests` (INV-51) and `CloudBindingResolutionTests` (INV-57) exist as named. Two related tests
    stay where they are: `HetznerRescueBootstrapTests.Rescue_password_is_not_returned_or_logged` and
    `OnboardingServiceStepTests.Tailscale_auth_key_reaches_the_host_only_through_the_secret_channel`.
  - `ControlPlaneOnboardMcpTests` comes with PR 3.

  Tests added after the PR 2 review:
  - `HetznerRescueBootstrapTests`:
    - `Failure_after_enable_rescue_with_a_failing_key_delete_still_disables_rescue_resets_and_drops_the_key`
    - `Failure_after_enable_rescue_of_an_unlisted_kind_still_cleans_up`
    - `Fingerprint_failure_drops_the_generated_key`
    - `Failure_after_the_line_was_placed_reports_the_standing_key`
    - `Not_fresh_server_is_refused`, extended with a `known_hosts` case
  - `RescueCommandsTests.Nothing_after_the_host_keys_can_fail_the_injection`
  - `OnboardingServiceStepTests.Abandon_over_ssh_checks_the_host_key_before_its_command`
  - `ServerRemovalTests`:
    - `Remove_of_an_ssh_host_without_a_verified_key_sends_no_command_and_names_the_dead_line`
    - `Remove_goes_on_whatever_the_cleanup_throws`
  - `ServersOnboardDialogTests.Onboard_after_an_injection_without_a_pin_resumes_the_rescue_bootstrap`
- i) A pin that the record cannot take (the store write fails) removes again the `known_hosts` lines it wrote for an
  endpoint that had none, so a later rescue resume is not refused as "not fresh" (D2); the failure itself stands. If
  that roll-back fails, the lines stay and a resume is refused (fail closed) until an Admin removes them (review r2).

## Amendment 2 (implementation of PR 3, 18 November 2026)

**Status: awaiting maintainer acceptance.** What PR 3 (part E) built differently from the text above, or added to it.

- a) **Policy name of the tool.** The policy sees a capability by its MCP tool name once it has one
  (`MutationPolicy`: `McpTool ?? Id`). `ControlPlaneResources.CapabilitiesWhenStateExists` therefore lists
  `onboard_server` next to `server.onboard`; without it a re-onboarding over MCP would not count as a control-plane
  change (INV-59). `ControlPlaneOnboardMcpTests` pins it.
- b) **The `server` argument.** The target resolver takes `server` as the id of `server.onboard` only when no
  `serverId` is given (the Servers page passes `serverId`). A call that passes both and names two different servers
  (compared ignoring case) is refused over MCP before policy (`invalid_argument`). The tool runs on the decided target in any case: a new
  read-only `MutationScope.DecidedTarget` hands it the server, its mTLS state and the host it reached (INV-59), and
  outside a `server.onboard` decision the tool refuses (fail closed).
- c) **Abandon refusal text.** `ProtectedResourceRule` appends "remove the bootstrap access in the UI (Servers →
  Remove bootstrap access)" to its `Deny` reason when the refused call is an abandon. Only the text changes.
- d) **Run token.** The tool ignores the token of the call (it is linked to the request) and runs on its own token
  that ends with `server.onboard`'s catalog deadline (30 minutes). A client that disconnects leaves the run going.
  Caveat: the gateway itself is unchanged, and its `deadline.Token` stays linked to the request
  (`MutationGateway.cs`, the execution's linked token source). A verifier that awaited it would record a run that
  succeeded after a client disconnect as "cancelled, effect unknown". `server.onboard` has `VerificationContract.None`,
  so nothing awaits it today; a verifier added later must not await that token, or the gateway must change first.
- e) **Run snapshot.** `OnboardingRuns` is filled by `OnboardingService` for every surface (UI and MCP), once the run
  holds the host lock, so a call refused because another run holds the lock does not replace that run's snapshot.
  `IOnboardingRunRecorder.Begin` is in-memory only and listed in `WriteNamedButNotGated`.
- f) **Additive answer fields.** `add_server` also returns `credentialFrom` (a server id). `get_onboarding_status` also
  returns `serverId` and `standingBootstrapKeyFingerprint`. It reports the standing key whenever the record carries
  `BootstrapKeyInjectedFingerprint` (while a run is going on, after it, and when no run is in memory after a
  restart) or the latest result says so (review r1; it used to answer `false` during a run). It shows the Tailscale login link only while the run is unfinished, and answers
  `not_found` when there is neither a run nor a standing key. Progress notifications are redacted with
  `SecretRedactor` like the status log; the notification of the login step carries the link as it is (Admin keys
  only, D5).
- g) **Argument checks.** `onboard_server` refuses `abandon` with a bootstrap other than `none`, `confirmVmName` with
  bootstrap `none`, and an unknown bootstrap value (`invalid_argument`). Any value other than `none` makes the resolver
  read the VM and `CloudBootstrapRule` ask for the approval first (fail closed), so an unknown value is refused after
  the approval, never run.
- h) **Carried over from the PR 2 review.** `OnboardingService` itself refuses a run with bootstrap `None` on a record
  with `BootstrapKeyInjectedFingerprint` set and no host-key pin, at step `HostKey`, before any command, and points to
  `hetzner_rescue` (resume) or `abandon`. It does not look at the provider or the binding, unlike the page's
  `RescueResumeForced`. Three existing `OnboardingServiceStepTests` that ran without a bootstrap after an injection now
  set up the pin read from the disk, the state such a run has in practice.
- i) **Tests as named in code.** `OnboardingMcpToolsTests` contains the PR 3 list under these names:
  `Levels_come_from_the_catalog`, `No_tool_takes_a_key_parameter`, `Client_disconnect_does_not_cancel_the_run` and
  `Progress_notifications_per_step`, plus `Without_a_bootstrap_the_preset_decides`,
  `Invalid_bootstrap_is_refused_before_the_run` and `Add_server_runs_through_the_gateway_and_reads_the_record_back`.
  `OnboardingServiceStepTests.Injected_without_a_pin_refuses_a_run_without_bootstrap` covers h.
  `ControlPlaneOnboardMcpTests` comes with this PR, as amendment h of PR 2 said. Review r1 added
  `OnboardingMcpToolsTests.Status_reports_the_standing_key_while_a_run_is_going`,
  `OnboardingMcpToolsTests.Server_and_serverId_naming_two_servers_are_refused`,
  `OnboardingServiceStepTests.A_run_refused_at_the_host_lock_keeps_the_latest_snapshot` and `OnboardingRunsTests`.
- j) **Records and the gate.** The release gate's rule R14 accepts an M row without a capability at level `admin` only
  for a tool in `AdminLevelReads` (M-52); every other such row must still say `read`. The tests that took "not read"
  to mean "mutating" (`CapabilityCatalogTests`, `AuditCompletenessTests`, `AuditPathMatrixTests`,
  `MutationGatewayCoverageTests`) leave the members of `AdminLevelReads` out. The argument `cloudCredentialFrom` is a
  server id, but the sink redactor reads its name as a credential: the audit keeps only its length (a payload
  parameter); the gateway event's details still name the source server id (`MutationScope.Note`).
- k) **Open point unchanged.** `get_onboarding_status` is judged by `DirectMcpCallGuard` at the admin level. It is
  blocked in read-only mode and held for approval under a preset that confirms above the autonomous level.

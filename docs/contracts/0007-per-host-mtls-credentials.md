# Contract 0007 — Per-host mTLS client credentials (R1-44)

Status: **Accepted** · 6 November 2026 by the maintainer, with the decisions D1–D6 at the end as recommended. R1-44
touches the protected mTLS layer; the maintainer approved that work on 5 November 2026. The rollout starts with Bifröst
and the whiskers.site host. **Amendment 1** (review round 1, at the end) is pending acceptance.

## Purpose

Today one client certificate opens every managed host. A stolen `client.key` gives root on the whole fleet: the Docker
API `POST` is root-equivalent, and host commands run through a privileged nsenter helper over the same connection. After
R1-44:

- every host accepts only its own client identity;
- host A's credential is refused by host B;
- existing hosts move over one by one, through an explicit rollout that can be undone.

Sources: INV-58, TA-44, E-06 (`docs/v1/audit/E-exec-injection.md:121-135`), WORK_PACKAGES R1-44.

## Current state (verified at `25797a37`)

### The shared client credential

- `{MtlsDir}/client.crt`, `client.key` and `ca.crt` (`DataPathOptions.cs:114`, `/app/data/mtls`) were made by hand with
  step-ca, CN `serverwatch-client`. No code generates them.
- No file modes are set. They are covered by the self-backup (`SelfBackupLayout.cs:69,112`) and by the control-plane
  guard (`ControlPlaneResources.cs:45`).

### Onboarding

- `OnboardingService.OnboardServerAsync` writes these three paths into every `ServerConfig`
  (`OnboardingService.cs:198-200`).
- `OnboardingCommands.DockerProxyCompose(tsIp)` sets `ghostunnel server … --cacert=/certs/ca.crt
  --allow-cn=serverwatch-client` (`OnboardingCommands.cs:102-110`). The flags are the same on every host.

### Issuing and renewing server certificates

- `MtlsCertificateCommands.SignCsr(csrB64, cn)` runs `docker exec step-ca step certificate sign … --not-after 8760h
  --bundle` on the local host (`MtlsCertificateCommands.cs:16,42-53`).
- A server certificate is a host-side P-256 key with CN `{slug}-dockerproxy` (`MtlsIdentity.ForNewHost`).
- `MtlsCertificateService.RenewAsync(serverId, ct)` renews it with a guarded activation: `.prev` copies, a 3-minute
  systemd-run guard, `docker restart ghostunnel`, then confirmation through the new certificate
  (`MtlsCertificateCommands.cs:90-120`).

### Connecting

- `DockerConnectionManager.BuildMtlsCredentials(ServerConfig)` reads `TcpClientCertPath` and `TcpClientKeyPath`.
- `ValidateMtlsServerCert` checks the chain against `TcpCaCertPath` and the hostname against `TcpHost`, failing closed
  (`DockerConnectionManager.cs:~334-400`).
- `HostCommandExecutor` uses the same connection.

### Gaps and tests

- No renewal of the client certificate (it expires on 8 October 2027).
- No revocation: `RevocationMode.NoCheck`, and no CRL or OCSP.
- Pinned tests: `OnboardingCommandsTests.The_ghostunnel_command_is_exactly_the_approved_one` (`:177-188`) and
  `DockerProxyCompose_…` (`:105-111`).
- No `PerHostCredentialTests` yet.

### Operator-supplied credentials

A hand-edited `servers.json` can hold arbitrary paths, and `TcpUseTls` can be on without a client certificate. Both stay
supported unchanged.

## Boundaries (fixed)

- **Validation is not weakened.** The chain against the fleet CA, the server-hostname match and failing closed all stay
  as they are. `RevocationMode` stays as it is; no new trust anchor is added.
- **Nothing else in the protected layer changes.** The socket-proxy verbs, ports, the ghostunnel image and the listen and
  target flags stay. Only the `--allow-cn` value changes, per host.
- **No crypto of our own.** Keys are P-256 from .NET `ECDsa`. CSRs come from `CertificateRequest`, signed by the existing
  step-ca.
- **Every step on a host can be undone.** Each host step uses the existing guarded activation (`.prev` files, guard
  timer, confirmation through the new path). An unconfirmed step rolls back by itself.
- **No secret is logged**, not even as a prefix. A private key never leaves Whiskers' data directory, and never goes to
  a host.

## Types and interfaces

### Identity

```csharp
/// <summary>The client identity Whiskers presents to one managed host.</summary>
public sealed record MtlsClientIdentity(string ServerId, string Slug, int Generation)
{
    /// <summary>"whiskers-client-{slug}-g{generation}" (see D2).</summary>
    public string CommonName { get; }
    public static MtlsClientIdentity For(ServerConfig server, int generation);
}
```

### Store (`Services/Certificates/IMtlsClientCredentialStore.cs`)

```csharp
public interface IMtlsClientCredentialStore
{
    /// <summary>{MtlsDir}/hosts/{serverId}/: client.key (0600), client.crt, generation; directory 0700.</summary>
    MtlsClientCredentialPaths PathsFor(string serverId);
    bool IsManaged(ServerConfig server);          // paths point into {MtlsDir}/hosts/{server.Id}/
    Task<MtlsClientCredentialPaths> IssueAsync(ServerConfig server, int generation, CancellationToken ct);
    Task RemoveAsync(string serverId, CancellationToken ct);   // on server removal
}
public sealed record MtlsClientCredentialPaths(string Cert, string Key, string Ca, int Generation);
```

`IssueAsync` works like this:

1. Generate the key in-process.
2. Build a CSR with CN `MtlsClientIdentity.CommonName` and the clientAuth EKU.
3. Sign it with `SignCsr`.
4. Validate it with a new `MtlsCertificateValidator.ValidateIssuedClient`: chain, CN, EKU, key match, validity.
5. Write the key and certificate atomically (`*.new`, then rename) with their modes.

On Windows hosts of Whiskers itself, the modes do not apply. Instead the ACLs are checked, and a readiness line reports
the result.

### Service (`IMtlsCertificateService`, additive)

```csharp
/// <summary>Moves one host from the shared to its own client identity (see Rollout). Admin, capability
/// "mtls.client_migrate", decided and audited through the gateway.</summary>
Task<MtlsClientMigrationResult> MigrateClientAsync(string serverId, IProgress<string>? progress, CancellationToken ct);

/// <summary>New certificate, same or next generation (D2). Capability "mtls.client_renew".</summary>
Task<MtlsClientMigrationResult> RenewClientAsync(string serverId, bool rotate, CancellationToken ct);

public sealed record MtlsClientMigrationResult(bool Ok, string Phase, string? Error, MtlsClientIdentity? Identity);
```

### Onboarding (`OnboardingCommands`)

`DockerProxyCompose(tsIp, string allowedClientCn)`. A new host gets its own identity from the start, and onboarding
writes `PathsFor(serverId)` into the `ServerConfig`.

### Tools and UI

- MCP tools `migrate_mtls_client_credential` and `renew_mtls_client_credential` (Admin).
- On the server page, a status line: "shared client credential / own credential (generation n), expires …".
- A **Migrate** button for Admins. The click goes through the gateway like any mutation.

## Rollout per host (`MigrateClientAsync`)

| Phase | Step | Undo |
|---|---|---|
| 1 Issue | `IssueAsync(server, 1)` | Delete the files. Nothing on the host changed. |
| 2 Widen | The host's compose gets `--allow-cn=serverwatch-client --allow-cn=<new CN>` (ghostunnel accepts several), then a guarded restart. Confirmation goes **through the new credential**. | The guard restores `.prev` after 3 minutes without confirmation. |
| 3 Switch | `ServerConfig` gets the new paths. The connection is rebuilt and checked (Docker ping plus one host command). | Restore the old paths. The host still accepts both. |
| 4 Narrow | The compose keeps only `--allow-cn=<new CN>`, then a guarded restart, confirmed through the new credential. | The guard restores the phase-2 state. |
| 5 Record | Audit entry, `INV-58` status for this host. | — |

A phase that fails stops the run, leaves the host in the last confirmed state, and reports the phase. A second call
resumes from that state; it reads the allowed CNs in the host's compose, so every phase can safely run again.

The shared `client.key` is deleted **only** by a separate Admin action, `mtls.shared_client_retire`. That action refuses
to run while any `ServerConfig` still points to the shared paths.

## Tests (acceptance)

- **`PerHostCredentialTests`**, using the `TestCa` fixture and a validator that models ghostunnel's check (chain plus
  allowed CN):
  - host A's credential is refused at host B, and accepted at A;
  - a credential of generation n is refused after a rotation to n+1 (D2);
  - the shared CN is refused after phase 4.
- **Command-line tests:** per-host `--allow-cn` in `DockerProxyCompose`, with two values in phase 2. The approved-flags
  test is updated with that list and nothing else.
- **Rollout tests:** each phase is idempotent, resumes after a failure, takes the guard path when confirmation is
  missing, and never touches operator-supplied paths.
- **Store tests:** file modes (Unix), atomic write, and no key material in logs (log capture).
- **Retire test:** refused while a server still uses the shared credential.
- **Readiness:** a line per host still using the shared credential, and a line for wrong key modes.

## Documentation

- `docs/ARCHITECTURE.md` PKI section: one client identity per host, and the blast radius after R1-44.
- `docs/v1/SECURITY_INVARIANTS.md` INV-58 set to ✅, with its tests.
- `Services/Certificates/README.md`, `deploy/README.md` (ghostunnel flags).
- An operator guide, "Migrating to per-host client credentials", covering order, undo, and how to verify per host.
- CHANGELOG upgrade note: existing hosts keep working until they are migrated, and the shared credential stays until it
  is retired.

## Plan triage

- [Claude-prüfbar] Identity, store, issuing, validator, rollout logic with a fake host, command lines, tool and UI wiring,
  readiness, every test listed above, docs.
- [braucht Nutzer/Hardware] Real rollout: Bifröst first, then the whiskers.site host (hetzner-apps), then host by host. Run
  the guarded restarts against live ghostunnel. After the last host, the user decides on `mtls.shared_client_retire`.

## Decisions for the maintainer

- **D1 Identity format.** Recommended: CN `whiskers-client-{slug}-g{n}`, with `--allow-cn`. Alternative: a URI SAN
  `spiffe://whiskers/host/{serverId}` with `--allow-uri`. That is cleaner, but a new pattern in the protected layer.
- **D2 Revocation without CRL.** Recommended: the generation in the CN. A rotation (`renew … rotate: true`) raises the
  generation and narrows the host to the new CN, so the old certificate is dead there at once. A plain renewal keeps the
  generation.
- **D3 Key storage.** Recommended: files with 0600 in `{MtlsDir}/hosts/{id}/`, as with the server keys. Alternative:
  encrypted with `VAULT_KEY` and decrypted only in memory. That is more protection on a stolen backup, but the key must
  be written out for the Docker client.
- **D4 Validity.** Recommended: 1 year (as step-ca issues today), with renewal through the `CertificateMonitor` warnings.
  Alternative: 90 days with automatic renewal. That would be a follow-up package.
- **D5 Bulk rollout.** Recommended: one host per action, no "migrate all" in this package. Matches your wish to go host by
  host.
- **D6 New hosts.** Recommended: onboarding uses its own identity right away (generation 1), with no shared phase.

## Amendment 1 — review round 1 (6 November 2026, pending acceptance)

This amendment is binding where it differs from the text above. Decisions D1–D6 are unchanged.

### Identity (review B3)

- `{slug}` is `Slug(server.Id)`, not the display name.
  - Display names are lossy ("Web 1" and "web_1" both become `web-1`), not unique, and change on rename.
- At first issue, the CN and the generation are stored in `{MtlsDir}/hosts/{id}/identity`.
  - From then on they are only read from there, never derived again.
- `IssueAsync` refuses when another server already maps to the same CN.
- The CN matches the `MtlsIdentity` DNS-label pattern and has at most 63 characters.
- `serverId` is checked against an allow-list pattern before it is used in a path.
- `IsManaged` compares canonical full paths.

### Store

- `Ca` is the existing `{MtlsDir}/ca.crt`. There is no per-host copy and no new trust anchor.
- The store holds `client.key`, `client.crt`, `identity` and `rollout.json`.
- Key files are created with `UnixCreateMode` 0600 when they are opened, not changed with chmod afterwards.
- `ValidateIssuedClient` accepts step's default EKU (serverAuth+clientAuth). It requires clientAuth and refuses any SAN.
  - The CSR carries no SAN.

### Host steps and confirmation (review B2)

- **Applying an `--allow-cn` change.** ghostunnel is recreated detached:
  `systemd-run … docker compose -f <file> up -d --force-recreate ghostunnel`. A restart would keep the old command line.
- **Guard.** It uses its own guard script and marker, never `renew-guard.sh` / `.renew-ok`.
  - Without the marker, it restores `docker-compose.yml.prev` and recreates ghostunnel.
- **Confirmation in phases 2 and 4.** A one-off client built from the **new** credential connects, through the same
  `BuildMtlsCredentials` / `ValidateMtlsServerCert` logic.
  - `docker inspect ghostunnel` `.Args` must equal the intended list.
- **Phase 4 negative check.** A handshake with the shared credential (with the previous generation, when rotating) must
  be refused **before** the marker is written.
- **Phase 5.** It is recorded only after both checks have passed.

### Resume and concurrency (review M6)

- **Where host state comes from.** It is read from the `docker inspect ghostunnel` args, not from the compose file.
- **Guard in progress.** No step runs while a `whiskers-*-guard*` systemd unit is active on the host.
- **One lock per host.** The rollout shares it with the server-certificate renewal (`RenewAsync`). A persisted phase
  journal (`rollout.json`) says where the rollout stands after a restart.
- **Preflight before any host change:**
  - ghostunnel is managed by compose; the file is found through the label `com.docker.compose.project.config_files`;
  - its args equal the approved list, except `--allow-cn`.

  Otherwise the step is refused with an explanation. Only the `--allow-cn` items are edited, in place. Image pins and all
  other flags stay as the host has them.

### Retiring and restore (review M7)

- **Retire is a UI action only** (Admin), with no MCP tool.
- **What retire requires.** Every TCP+TLS server must have a phase-5 record with the negative check passed. The match is
  on the client certificate's CN and thumbprint, not on path strings.
- **Older self-backups.** They still contain the shared key, and the operator guide says so.
- **Restore after a migration.** A readiness line names the case "shared client credential present while hosts are
  narrowed" and points to break-glass re-onboarding over SSH.

### Capabilities and renewal (review M8)

- **Registration.** `mtls.client_migrate`, `mtls.client_renew` and `mtls.shared_client_retire` are registered in
  `CapabilityCatalog`, `ControlPlaneResources.Capabilities` and `MutationCompositions` (raw host command allowance),
  the same way as `certificate.renew_mtls`.
  - MCP tools exist only for migrate and renew.
- **Rotation.** `rotate: true` reuses phases 2–5 for g(n) → g(n+1).
- **Plain renewal.** It keeps local `.prev` copies of the key and the certificate until the new connection is confirmed.

### Tests (review M9 and minors)

- **Validation is preserved.** After phase 3, after a renewal, after a rotation and for a newly onboarded host, the
  credentials that are built validate through `ValidateMtlsServerCert` with `TcpCaCertPath`. A server certificate for the
  wrong IP, or one from a foreign CA, is refused.
- **Store.** Owner uid, key 0600 and directory 0700 (Unix).
- **Enrollment.** `OnboardServerAsync` writes `PathsFor(id)` and the per-host `--allow-cn`.
- **Identity.** Two servers whose names collide get different CNs.
- **Docs.** A doc check confirms the blast-radius section in ARCHITECTURE and INV-58.
- **Fixture.** `TestCa` moves to a shared test fixture.
- **[braucht Nutzer/Hardware]** Live negative checks: A's credential at B's ghostunnel, and the shared credential after
  phase 4.

### INV-58

✅ only "per host after migration". Readiness shows the status per host until every managed host is narrowed.

### Definition

"Operator-supplied paths" are paths that are neither the shared `{MtlsDir}/client.*` nor the managed
`{MtlsDir}/hosts/{id}/*`. Migrating a server that is on the shared paths is the normal case.

# Certificates

Two jobs: notice before a certificate expires, and renew the mTLS proxy certificates without moving private keys
around.

## Expiry monitor (`CertificateMonitor`)

Every 12 hours it looks at:

- Whiskers' own mTLS client certificate and CA bundle, read from disk. Every certificate in the bundle counts,
  and the intermediate is usually the first to expire.
- The certificate each mTLS proxy (ghostunnel) presents. It is read from the TLS handshake, which is then
  aborted. Nothing is sent and nothing is trusted.
- certbot's certificates on every enabled server. A host without certbot simply has none.

It notifies 30, 14 and 7 days before expiry (`certificate_expiring`) and once more on expiry
(`certificate_expired`), one notification per stage per certificate. The last announced stage is kept in
`certificate-alerts.json`, so deploys do not repeat alerts. A renewed certificate starts over.

## Proxy certificates (`MtlsCertificateService`, MCP `renew_mtls_certificate`)

The private key of a proxy is generated **on the host** and never leaves it:

1. The host creates a P-256 key (`server.key.new`) and a CSR.
2. Whiskers checks the CSR before anything is signed: its signature, the subject, exactly the expected SANs and
   that it does not ask to be a CA. The host is the very thing the certificate vouches for. A host that could
   pick its own SANs could get a certificate for another host's IP and impersonate that proxy.
3. step-ca on Badwolf signs it for one year (leaf + intermediate).
4. Whiskers checks the result: same key as the CSR, the expected identity, and a chain plus host name that its
   own mTLS check accepts.
5. The host stages the files and refuses them if the certificate does not match the new key.

At **onboarding** the staged files simply become the live ones before the proxy first starts.

**Renewal** keeps the identity the proxy has today (CN and SANs of the certificate it presents). Every command to
that host passes through the ghostunnel being renewed, so a broken certificate would lock Whiskers out of the
host that has to undo it. The host therefore protects itself:

- The current files are kept as `.prev`, and a `systemd-run` timer is armed for 3 minutes
  (`/opt/dockerproxy/renew-guard.sh`).
- The new files are swapped in and ghostunnel restarts detached.
- Whiskers waits until the proxy presents the new certificate, then confirms *through it*. The confirmation
  leaves `.renew-ok` and deletes the old key, because the old certificate stays valid for months.
- If no confirmation has arrived when the timer fires, the host restores the `.prev` files and restarts
  ghostunnel.

The tool is admin-only and audited (`certificate.renew_mtls`). Rotating the CA itself, which would need a phase
where old and new CA are trusted side by side, is not covered.

## Per-host client credentials (R1-44, contract 0007)

Each managed host has its own client identity `whiskers-client-{slug}-g{n}` (INV-58). The parts:

| Type | Role |
|---|---|
| `MtlsClientIdentity` | The name. The slug is `Slug(serverId)`; when it is too long or already held by another server, it is cut and gets the first 6 hex digits of SHA-256(serverId). The slug is fixed at the first issue and read back from the store afterwards. The name is at most 63 characters, one DNS label, with room for generation 9999. |
| `IMtlsClientCredentialStore` / `MtlsClientCredentialStore` | `{MtlsDir}/hosts/{serverId}/` (0700): `identity` (the stem plus the active, pending and previous slot), `rollout.json` (the journal), and one slot `g{n}-s{k}/` per issued certificate with `client.key` (0600, created with that mode) and `client.crt`. The key is generated in-process; only the CSR goes to step-ca. The CA bundle is the shared `{MtlsDir}/ca.crt`. `ProposeNewSlot` names the next slot; `IssueAsync` / `IssuePendingAsync` write exactly that slot under the store lock and return it. Nothing returns an "active" slot: the server record is authoritative. A slot that any record names is never deleted. |
| `MtlsCertificateValidator.ValidateIssuedClient` | The issued certificate must match the key and the CN, must not be a CA, must have clientAuth in its EKU (serverAuth may be there too) and must chain to `ca.crt`. It may carry at most one DNS SAN, equal to the CN. |
| `MtlsClientRollout` (part of `MtlsCertificateService`) | Migrate (`mtls.client_migrate`), renew or rotate (`mtls.client_renew`), retire the shared credential (`mtls.shared_client_retire`, UI only). The rollout phases are issue, widen, switch, narrow, record. It shares one lock per host with the server-certificate renewal. A resume derives its state from ghostunnel's live `.Args`, the server record and the store, never from the last phase record. |
| `MtlsAllowListCommands` / `MtlsHostState` | The host scripts and their preflight. They edit only the `--allow-cn` items. The guard (`whiskers-allow-guard-{run}`) takes a blocking `flock`, rolls back unless its own run's marker exists, recreates under `whiskers-allow-guard-recreate-{run}`, checks the result and logs it. The confirmation runs under `timeout 20` inside the same lock. |
| `MtlsClientProbe` | The one-off handshake. A TLS alert counts as Refused; a timeout, reset or EOF counts as NotChecked. The confirmation script runs through a one-off client built from the NEW credential (`HostShellOperations.ForOneOffClient`). |

Operator guide: `docs/operations/per-host-mtls-migration.md`.

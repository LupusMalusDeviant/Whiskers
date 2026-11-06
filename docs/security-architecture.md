# Security architecture

How Whiskers reaches managed hosts, what protects which secret, and how release images are built and signed. The full
design, PKI (step-ca), onboarding flow and trade-offs are in [ARCHITECTURE.md](ARCHITECTURE.md); the threat model and
private reporting are in [SECURITY.md](../SECURITY.md).

## Three planes per managed host

None of them uses SSH in steady state, and none of them needs a stored SSH key:

| Plane | What | Transport |
|---|---|---|
| **Telemetry** | host CPU/RAM/disk, container stats | `node_exporter` → VictoriaMetrics (pull over the mesh) |
| **Docker API** | list/restart/deploy/inspect | Docker Engine API over **mTLS** (client certificate, CN-pinned) + verb-whitelisting socket proxy |
| **Host control** | `execute_command`, systemd, journald, edits | one-shot privileged `nsenter` container, driven over the same mTLS Docker channel |

**SSH is a bootstrap channel only.** Onboarding opens a single SSH connection — with host-key verification
(trust-on-first-use, pinned afterwards) — to join the host to a private WireGuard mesh (Tailscale/NetBird), roll out
the mTLS certificates and the Docker proxy, and install telemetry. The bootstrap credential (uploaded key or a password
held in memory only) is then deleted. From that point on the host is operated key-free over the three mTLS/mesh
planes; Whiskers holds no standing SSH key for it. A server that was added as a plain SSH connection and never
onboarded keeps using SSH until it is migrated.

**The Docker-API and host-control planes are root-equivalent on the managed host** — creating a container or running
`nsenter` is root by construction. mTLS and the mesh decide *who* can reach that power; scopes, guardrails and
approvals decide *what* a given caller may do with it.

## Summary

- **No standing SSH key** for managed hosts in steady state; management ports are **mesh-bound**, nothing
  management-related is exposed to the public internet by design.
- **Deterministic policy enforcement, not prompt instructions.** Every MCP tool call — from a signed-in web user, an
  external MCP client or the in-process agent — passes the same scope check in code (Read / Write / Admin, or an
  explicit per-key tool list), then the same guardrail engine: action classification (Allow / Confirm / Block,
  protected resources, forbidden arguments, read-only kill switch) and human-in-the-loop approval for `Confirm`. See
  [governance.md](governance.md#how-the-policy-layer-applies).
- **MCP API keys** are created in the web UI with an explicit scope; the ServerWatch-era unscoped key file
  `api-keys.json` no longer exists ([mcp.md](mcp.md#api-keys-and-scopes)).
- **Secrets** (`.env`, the encrypted vault, the DB) live under `/app/data` / `.env`, both gitignored and
  volume-mounted — never baked into the image or committed. Not everything there is encrypted: the SSH private keys
  and the mTLS client keys are protected by file permissions only
  ([what the data directory holds](operations/production-checklist.md#what-the-data-directory-holds)).
- **Full-repo security review & remediation** ([security-fixes changelog](reviews/2026-07-07-security-fixes-changelog.md)):
  all critical and high findings, plus most medium/low issues, are fixed — per-key RBAC on **every** path (including
  the agent and MCP), the vault on **AES-256-GCM + PBKDF2**, schema via **EF Core migrations**, trust-critical images
  **pinned by digest**, restricted forwarded-header trust, and a scrape-token-gated `/metrics`.

## Supply chain

Every published release image is, from v0.12.1 on:

- **scanned before it ships** — the release pipeline builds the image, runs a Trivy scan, and fails the whole run on
  any CRITICAL, so a vulnerable image is never pushed. From the release after 0.15.0 on, both architectures (amd64 and
  arm64) are scanned by digest and exactly the scanned digests are published, only after the full test suite passed
  on the tag commit, and a published version is never replaced;
- **built multi-arch with provenance + an SBOM attestation** (SLSA provenance and an SPDX SBOM attached to the image,
  and a standalone SBOM as a release asset);
- **keyless-signed with [cosign](https://github.com/sigstore/cosign)** (Sigstore) — the signature is bound to the
  release workflow's GitHub OIDC identity and recorded in the public Rekor transparency log; no signing key is held by
  anyone.

The verify command is in the [README](../README.md#verifying-the-image). A valid result means the image was built by
this repository's release workflow from a tag — not rebuilt or tampered with. Images published before v0.12.1 are
provenance-attested but not cosign-signed.

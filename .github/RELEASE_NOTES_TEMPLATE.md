<!--
  Release-notes template for Whiskers. The release workflow attaches auto-generated commit notes;
  use this to write the human-facing highlights on top, in the product's own language. Keep the
  framing governance-first (see docs/product/POSITIONING.md) — features are evidence of the control
  plane's reach, not the headline. Delete sections that don't apply.
-->

# Whiskers vX.Y.Z

**One-line summary** — what this release changes for someone operating infrastructure with humans and AI agents.

## Highlights

- **<headline change>** — one sentence on the operator-visible value.
- …

## Governance & security

<!-- Anything touching permissions, guardrails, approvals, audit/history, secrets, SSH/mesh, the
     supply chain (Trivy gate, SBOM, cosign). Call out behavior changes explicitly. -->

- …

## Added / Changed / Fixed

See the [CHANGELOG](../CHANGELOG.md) for the full list. Notable:

- **Added:** …
- **Changed:** … (⚠️ breaking changes and required operator actions here)
- **Fixed:** …

## Upgrading

- Image: `ghcr.io/lupusmalusdeviant/whiskers:X.Y.Z` · Helm: `oci://ghcr.io/lupusmalusdeviant/charts/whiskers`
- Verify the image signature before running it (see README → Security → Supply chain).
- Migrations run automatically on boot; back up `/app/data` (and your database) first.
- Note any manual steps a breaking change requires.

## Known limitations

Beta: not yet API-stable; single-replica by design; Kubernetes scope is intentionally limited (pods,
logs, honest scale/rollout). Report issues privately per [SECURITY.md](../SECURITY.md) when security-relevant.

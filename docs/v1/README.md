# docs/v1 — the 1.0 product contract and its release gate

**Start here as a reader:**

- [PRODUCT_CONTRACT.md](PRODUCT_CONTRACT.md): what Whiskers 1.0 promises, its path matrix and its accepted limits.
- [SECURITY_INVARIANTS.md](SECURITY_INVARIANTS.md): the security invariants (INV-xx) and the evidence for each.

**Release-gate and process artefacts.** The release-gate tests read most of these, so do not rename or reshape them by hand:

- [RELEASE_GATE.md](RELEASE_GATE.md): the gate rules and the accepted exceptions (read by `ReleaseGateTests`).
- [CLAIMS.md](CLAIMS.md): the claim register of the governed documents (contract 0011, read by `ClaimRegisterTests`).
- [CAPABILITY_INVENTORY.md](CAPABILITY_INVENTORY.md): **generated** from the code by `ReleaseGateTests`; do not edit.
- [PATH_CAPABILITIES.md](PATH_CAPABILITIES.md): the hand-maintained capabilities of each path row (read by the gate).
- [WORK_PACKAGES.md](WORK_PACKAGES.md): the R1 work packages that led to 1.0.
- [TECHNICAL_AUDIT.md](TECHNICAL_AUDIT.md) and [audit/](audit/): the historical technical audit of 0.15.0 that the work packages came from.

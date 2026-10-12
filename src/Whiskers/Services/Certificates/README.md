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

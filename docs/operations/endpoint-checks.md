# Endpoint checks

An endpoint check asks one managed host whether a service it can reach answers: an HTTP status and its latency, a TCP
connect, or how long a TLS certificate is still valid. The check runs **on the host it is bound to**, from that host's
side of the network, through the same host command executor as every other host command (local `nsenter`, the
one-shot helper container over the mTLS Docker connection, or SSH). Whiskers itself never opens a connection to the
target; it only decides which addresses the host may contact. Contract:
[`docs/contracts/0004-endpoint-checks.md`](../contracts/0004-endpoint-checks.md) (R1-57).

Checks are listed under **Checks** (`/checks`). Every signed-in role can read them. Only an Admin creates, edits,
deletes or runs one ("Run now"), because a run is a command on the host. The read-only MCP tool
`endpoint_checks_list` shows the checks and their last result; it gives the number of internal-target exceptions, not
their addresses. There is no MCP tool that writes a check.

## Required tools on each host

The checks use fixed command templates. Each host that runs a check needs:

| Tool | Used for | Notes |
|---|---|---|
| `getent` | resolving a host name with the host's own resolver (`getent ahosts '<name>'`) | not needed for a check whose target is an IP literal |
| `curl` | HTTP checks | |
| `openssl` | TLS-expiry checks (`openssl s_client`) | |
| `nc` (**OpenBSD netcat**) | TCP checks (`nc -z -w <timeout>`) | Detected by `nc -h` naming "OpenBSD netcat". Another variant (BusyBox, GNU, Nmap's `ncat`) has other flags; the check then fails with "unsupported nc variant" instead of guessing |

A missing tool fails the check ("… is not installed on the host"). It never passes.

Install them:

```sh
# Debian / Ubuntu (getent is part of libc-bin and always present)
sudo apt-get update && sudo apt-get install -y curl openssl netcat-openbsd

# Alpine (getent comes with musl-utils; BusyBox nc is not OpenBSD netcat)
sudo apk add musl-utils curl openssl netcat-openbsd
```

On a Debian or Ubuntu host where `nc` points at another variant, select OpenBSD netcat with
`sudo update-alternatives --config nc`.

## What a check may contact

Before anything is probed, Whiskers judges every address:

1. An IP-literal target is taken as it is. It must be written canonically: a dotted quad (`192.0.2.10`) or an RFC 5952
   IPv6 address (`2001:db8::1`). Decimal (`2130706433`), octal (`0177.0.0.1`), hex (`0x7f.1`), shortened IPv4
   (`127.1`), zones (`fe80::1%eth0`) and brackets are refused on save.
2. A host name is resolved **on the host** with `getent ahosts`. The complete answer must be readable; a cut-off,
   empty or unreadable answer fails the run.
3. Every address in the answer, after canonicalisation (IPv4-mapped, NAT64 and 6to4 forms count as the IPv4 address
   they carry), must be public, or allowed by one of the check's exceptions. One internal address refuses the whole
   run (outcome *Refused*, naming the address and its range), even if it would not have been tried.
4. Then at most the first **4** unique addresses are tried, in answer order, until one answers. The result names the
   answering address, or that none answered, and how many addresses were not tried.

The denied ranges are the fixed R1-54 list (loopback, RFC 1918, link-local including `169.254.169.254`, CGNAT and
Tailscale, ULA, documentation, multicast and the rest). They cannot be configured.

HTTP checks accept only absolute `http://` and `https://` URLs, without user name or password, quotes, whitespace,
shell or glob characters; `curl` runs with `--proto '=http,https'`, without `-L` (a redirect is the answer, judged
against the expected status, and is not followed), with `--noproxy '*'` and pinned to the judged address with
`--resolve`. A definition port that differs from the URL's port (80/443 when the URL names none) is refused.

### Internal-target exceptions

A service on the LAN needs an exception on its check. Exceptions are part of the check definition, so only an Admin can
set them. In the form, enter one exception per line:

```text
host port address...
IP port
```

- `db.lan 5432 10.0.0.5` — the name `db.lan` on port 5432 may resolve to `10.0.0.5`. A name needs every address it
  may resolve to; an answer with an internal address that is not listed is still refused.
- `10.0.0.5 5432` — the literal target `10.0.0.5` on port 5432.

An exception matches only the exact name or literal, port and address. Ranges, wildcards and lists are refused. At most
100 exceptions per check.

## Bounds

| Bound | Value |
|---|---|
| Executor calls per run | at most 1 + 4 (one `getent`, at most four probes) |
| One attempt | the check's timeout, 1–30 s (default 10 s); every transport ends a call at most 5 s after it |
| Whole run | one deadline: timeout × 5 + 10 s (60 s at the default timeout) |
| Output per call | 16 384 bytes, cut on the host with `head -c 16384`; the exit marker `__rc=` is inside that bound, so a cut-off answer has no marker and fails |
| Interval | at least 60 s (validated on save; a stored shorter value still waits 60 s), at most 86 400 s |
| Concurrency | at most 4 runs at a time across the fleet, 1 per server; the loop ticks every 15 s |
| Result retention | 7 days per check; each check's last result is kept indefinitely |

A run passes only on positive evidence: the exit marker `__rc=0` at the very end of the output **and** the answer the
probe exists for (a three-digit HTTP status, a TCP connect, a server certificate whose `notAfter` is far enough away).
A non-zero exit, a missing marker, a missing tool or unreadable output is a failure. A TLS check fails when `openssl`
exits non-zero even if it printed a certificate.

## Audit

Every run is a privileged execution and appears in the audit log as one `endpointcheck.run` event with its own
operation id; the recorded result carries the same id. A run by the background loop acts as the check's **owner** (the
Admin who saved it last), whose authority is read again on every run. When the owner is no longer Admin (demoted,
removed from the whitelist, account deleted), the check is **suspended** instead of run: it is switched off with the
reason shown and an `automation_suspended` notification. An Admin takes it over by saving it again. "Run now" runs in
the signed-in Admin's own decision. Saving and deleting a check are audited `endpointcheck.save` and
`endpointcheck.delete` events. The kill switch refuses all three, and an audit failure refuses before anything is
written or run.

With the 60 s minimum interval a check produces at most 1 440 audit events per day; audit retention is that of the
audit log (R1-23).

## Alerts

A check that fails feeds the alert `endpoint_check_failed` (suppressible by quiet hours):

- **Opens** after the check has failed for 5 minutes, and never on a single run (at least two failing runs).
- **Clears** with `endpoint_check_failed_recovered` after the check has passed for 5 minutes. One passing run in between
  does not end the failure.
- A *Refused* run (an internal address without an exception, a definition that no longer validates, a gateway refusal)
  counts as failed, and so does a certificate with fewer days left than the check's warning days (default 14).
- A gap in the runs longer than three intervals (at least 3 minutes) closes an open alert as unconfirmed.
- Switching a check off or deleting it closes its open alert. Open alerts are kept across a restart
  (`endpoint-check-breaches.json` in the data directory, part of the self-backup).

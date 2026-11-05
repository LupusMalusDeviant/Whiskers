#!/usr/bin/env bash
# R1-52: the locked restore of the whole solution — exactly the packages and content hashes of every packages.lock.json,
# or the step fails. Every workflow restores through this script (BuildReproducibilityTests counts a call of it as a locked
# restore and checks this file).
#
# Retried only for a failure that can pass on the next try (nuget.org resetting the connection): up to three tries, 20 s
# apart, and the third failure fails. A failure that the next try would repeat fails at once, without retrying:
#   NU1004  the lock file does not match the project       NU1005  the lock file cannot be honoured
#   NU1403  a package's content hash differs from the lock file (a supply-chain signal, never hidden behind retries)
#   NU1101  a package does not exist on the source         NU1102  the locked version does not exist (e.g. unlisted
#                                                                   after a malware removal)
# NU1101/NU1102 fail at once only when the log shows no source failure (NU1301, NU1801): nuget.org can report a package
# as missing when the request for it failed, and that is retried. NU1004/NU1005/NU1403 always fail at once.
# The output stays in the job log (tee) and is searched for these codes. No NuGet cache anywhere: a cache hit skips
# NuGet's content-hash and signature checks.
set -euo pipefail
cd "$(dirname "$0")/../.."

log="$(mktemp)"
trap 'rm -f "$log"' EXIT

for attempt in 1 2 3; do
  status=0
  dotnet restore Whiskers.slnx --locked-mode 2>&1 | tee "$log" || status=$?
  if [ "$status" -eq 0 ]; then exit 0; fi
  if grep -Eq 'NU1004|NU1005|NU1403' "$log"; then echo "::error::the lock file or a content hash does not match (exit $status), not retried"; exit 1; fi
  if grep -Eq 'NU1101|NU1102' "$log" && ! grep -Eq 'NU1301|NU1801' "$log"; then echo "::error::a locked package is missing on the source (exit $status), not retried"; exit 1; fi
  if [ "$attempt" -eq 3 ]; then echo "::error::the locked restore failed three times (last exit $status)"; exit 1; fi
  echo "::warning::the locked restore failed (exit $status, attempt $attempt of 3); retrying in 20 s"
  sleep 20
done
exit 1

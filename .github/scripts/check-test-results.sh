#!/usr/bin/env bash
# R1-47 (TA-50, F-13): reads a test run's TRX file and fails closed — instead of trusting that "some test passed".
#
#   check-test-results.sh <trx-file> <minimum-passed> [--no-skips]
#
# Fails when the file is missing or has no counters, when any test failed, errored, timed out or aborted, when fewer than
# <minimum-passed> tests passed (a suite that silently vanished — a project missing from the solution, a filter that
# matches nothing), and with --no-skips when any test did not run (a mandatory suite that turned into skips, e.g. the
# PostgreSQL tests without Docker). Lists the tests that did not pass.
set -euo pipefail

fail() { echo "::error::$*" >&2; exit 1; }

[ $# -ge 2 ] && [ $# -le 3 ] || fail "usage: $0 <trx-file> <minimum-passed> [--no-skips]"
trx="$1"
minimum="$2"
no_skips="${3:-}"
[ -z "$no_skips" ] || [ "$no_skips" = "--no-skips" ] || fail "unknown option '$no_skips'"
printf '%s' "$minimum" | grep -Eq '^[0-9]+$' || fail "minimum '$minimum' is not a number"
[ -s "$trx" ] || fail "no test results at '$trx': the run did not produce them"

counters="$(tr -d '\r' < "$trx" | grep -o '<Counters [^>]*>' | head -n1 || true)"
[ -n "$counters" ] || fail "'$trx' has no <Counters>: not a complete TRX file"

attr() {
    local value
    value="$(printf '%s' "$counters" | sed -n "s/.* $1=\"\([0-9]*\)\".*/\1/p")"
    printf '%s' "$value" | grep -Eq '^[0-9]+$' || fail "'$trx' has no numeric '$1' counter"
    printf '%s' "$value"
}
total="$(attr total)"
executed="$(attr executed)"
passed="$(attr passed)"
failed="$(attr failed)"
errors="$(attr error)"
timeouts="$(attr timeout)"
aborted="$(attr aborted)"
skipped=$((total - executed))
# Each result that did not pass, by name (xUnit reports a skipped test as NotExecuted).
not_passed="$(tr -d '\r' < "$trx" | grep -o '<UnitTestResult [^>]*>' \
    | grep -v 'outcome="Passed"' | sed -n 's/.*testName="\([^"]*\)".*outcome="\([^"]*\)".*/  \2  \1/p' || true)"

echo "$(basename "$trx"): total=$total executed=$executed passed=$passed failed=$failed error=$errors timeout=$timeouts aborted=$aborted skipped=$skipped"

if [ $((failed + errors + timeouts + aborted)) -gt 0 ]; then
    printf '%s\n' "$not_passed"
    fail "$((failed + errors + timeouts + aborted)) test(s) did not pass"
fi
if [ "$passed" -lt "$minimum" ]; then
    fail "only $passed test(s) passed, at least $minimum are expected: part of the suite did not run"
fi
if [ "$no_skips" = "--no-skips" ] && { [ "$skipped" -gt 0 ] || [ -n "$not_passed" ]; }; then
    printf '%s\n' "$not_passed"
    fail "$skipped test(s) were skipped in a suite that must run completely"
fi
echo "ok"

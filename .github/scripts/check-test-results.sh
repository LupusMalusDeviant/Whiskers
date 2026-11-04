#!/usr/bin/env bash
# R1-47 (TA-50, F-13): reads a test run's TRX file and fails closed — instead of trusting that "some test passed".
#
#   check-test-results.sh <trx-file> <minimum-passed> [--no-skips] [--require <manifest>]
#
# Fails when the file is missing or has no counters, when any test failed, errored, timed out or aborted, when fewer than
# <minimum-passed> tests passed (a suite that silently vanished — a project missing from the solution, a filter that
# matches nothing), and with --no-skips when any test did not run (a mandatory suite that turned into skips, e.g. the
# PostgreSQL tests without Docker). Lists the tests that did not pass.
#
# --require <manifest>: tests that must be in this run and pass, independent of the floor — a floor alone lets one
# mandatory test vanish (an attribute removed, a class renamed) while the rest of the suite still clears it. Each line of
# the manifest is "<minimum> <name>": <name> is a test (Namespace.Class.Method, every case of a theory) or a whole class
# (Namespace.Class); at least <minimum> results must match it, and every one of them must have passed. Blank lines and
# lines starting with # are ignored.
set -euo pipefail

fail() { echo "::error::$*" >&2; exit 1; }
usage() { fail "usage: $0 <trx-file> <minimum-passed> [--no-skips] [--require <manifest>]"; }

[ $# -ge 2 ] || usage
trx="$1"
minimum="$2"
shift 2
no_skips=""
manifest=""
while [ $# -gt 0 ]; do
    case "$1" in
        --no-skips) no_skips="--no-skips"; shift ;;
        --require) [ $# -ge 2 ] || usage; manifest="$2"; shift 2 ;;
        *) fail "unknown option '$1'" ;;
    esac
done
printf '%s' "$minimum" | grep -Eq '^[0-9]+$' || fail "minimum '$minimum' is not a number"
[ -s "$trx" ] || fail "no test results at '$trx': the run did not produce them"
if [ -n "$manifest" ]; then [ -s "$manifest" ] || fail "no required-tests manifest at '$manifest'"; fi

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

# One line per result: "<outcome><TAB><test name>" (xUnit reports a skipped test as NotExecuted).
results="$(mktemp)"
trap 'rm -f "$results" "$results.names" "$results.outcomes"' EXIT
tr -d '\r' < "$trx" | grep -o '<UnitTestResult [^>]*>' \
    | sed -n 's/.* testName="\([^"]*\)".*/\1/p' > "$results.names" || true
tr -d '\r' < "$trx" | grep -o '<UnitTestResult [^>]*>' \
    | sed -n 's/.* outcome="\([^"]*\)".*/\1/p' > "$results.outcomes" || true
[ "$(wc -l < "$results.names")" -eq "$(wc -l < "$results.outcomes")" ] \
    || fail "'$trx' has results without a name or an outcome"
paste "$results.outcomes" "$results.names" > "$results"
not_passed="$(grep -v '^Passed	' "$results" | sed 's/^\([^	]*\)	/  \1  /' || true)"

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

if [ -n "$manifest" ]; then
    problems=0 entries=0
    while IFS= read -r line || [ -n "$line" ]; do
        line="${line%$'\r'}"
        case "$line" in ''|'#'*) continue ;; esac
        want="${line%% *}"
        name="${line#* }"
        if ! printf '%s' "$want" | grep -Eq '^[1-9][0-9]*$' || [ -z "$name" ] || [ "$name" = "$line" ]; then
            fail "'$manifest': not '<minimum> <name>': '$line'"
        fi
        entries=$((entries + 1))
        # The test itself, a case of it ("Name(...)"), or a member of the class ("Name.Method").
        matched="$(awk -F'\t' -v n="$name" '$2 == n || index($2, n "(") == 1 || index($2, n ".") == 1' "$results")"
        found=0 ok=0
        if [ -n "$matched" ]; then
            found="$(printf '%s\n' "$matched" | wc -l | tr -d ' ')"
            ok="$(printf '%s\n' "$matched" | grep -c '^Passed	' || true)"
        fi
        if [ "$found" -lt "$want" ]; then
            echo "::error::required: $name — $found result(s) in this run, at least $want expected (missing or renamed)" >&2
            problems=$((problems + 1))
        elif [ "$ok" -ne "$found" ]; then
            printf '%s\n' "$matched" | grep -v '^Passed	' | sed 's/^\([^	]*\)	/  \1  /' >&2
            echo "::error::required: $name — $((found - ok)) of $found result(s) did not pass (skipped or failed)" >&2
            problems=$((problems + 1))
        fi
    done < "$manifest"
    [ "$entries" -gt 0 ] || fail "'$manifest' names no required test"
    [ "$problems" -eq 0 ] || fail "$problems required test entr(y/ies) of $(basename "$manifest") missing or not passed"
    echo "required: all $entries entries of $(basename "$manifest") present and passed"
fi
echo "ok"

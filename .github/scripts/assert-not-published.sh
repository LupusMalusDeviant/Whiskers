#!/usr/bin/env bash
# R1-45: refuse to replace an immutable release artifact.
#
#   assert-not-published.sh image   <image-ref>            e.g. ghcr.io/lupusmalusdeviant/whiskers:0.16.0
#   assert-not-published.sh chart   <oci-chart> <version>  e.g. oci://ghcr.io/lupusmalusdeviant/charts/whiskers 0.16.0
#   assert-not-published.sh release <tag>                  e.g. v0.16.0 (GitHub release of $GITHUB_REPOSITORY)
#   assert-not-published.sh image-matching <image-ref> <amd64-digest> <arm64-digest>
#
# image/chart/release: exit 0 only when the registry/API says the artifact does NOT exist. If it exists, or if
# its existence cannot be established (tool missing, auth error, outage, unexpected message), the script fails:
# a release run never overwrites a published version and never guesses.
#
# image-matching (used by the publish job, so "Re-run failed jobs" can finish a run that broke after tagging):
# absent -> prints "state=absent"; present with exactly the given linux/amd64 + linux/arm64 manifests and no
# other platform -> prints "state=identical"; anything else fails. The state line also goes to $GITHUB_OUTPUT.
#
# Moving tags (`X.Y`, `latest`) are not checked here.
set -euo pipefail

kind="${1:?usage: assert-not-published.sh image|chart|release|image-matching ...}"

# Prints "absent" or "present"; exits 1 when the answer cannot be established.
probe() {
  local what="$1"; shift
  local out rc=0
  out="$("$@" 2>&1)" || rc=$?
  if [ "$rc" -eq 0 ]; then
    echo present
    return 0
  fi
  if [ "$rc" -eq 126 ] || [ "$rc" -eq 127 ]; then
    echo "::error::Could not establish whether ${what} exists (the tool could not run, exit ${rc}): ${out}" >&2
    exit 1
  fi
  # Only the registry's / API's own "does not exist" answers count as absent.
  if printf '%s\n' "$out" | grep -Eq 'manifest unknown|MANIFEST_UNKNOWN|NAME_UNKNOWN|: not found$|release not found'; then
    echo absent
    return 0
  fi
  echo "::error::Could not establish whether ${what} exists, refusing to publish: ${out}" >&2
  exit 1
}

refuse_if_present() {
  local what="$1"; shift
  local state
  state="$(probe "$what" "$@")"
  if [ "$state" = "present" ]; then
    echo "::error::${what} already exists. A published version is immutable; bump the version and tag again."
    exit 1
  fi
  echo "${what} is not published yet."
}

case "$kind" in
  image)
    ref="${2:?image reference required}"
    refuse_if_present "image ${ref}" docker buildx imagetools inspect "$ref"
    ;;
  chart)
    chart="${2:?chart reference required}"
    version="${3:?chart version required}"
    refuse_if_present "chart ${chart} ${version}" helm show chart "$chart" --version "$version"
    ;;
  release)
    tag="${2:?tag required}"
    refuse_if_present "GitHub release ${tag}" gh release view "$tag" --repo "${GITHUB_REPOSITORY:?}"
    ;;
  image-matching)
    ref="${2:?image reference required}"
    amd64="${3:?amd64 digest required}"
    arm64="${4:?arm64 digest required}"
    state="$(probe "image ${ref}" docker buildx imagetools inspect "$ref")"
    if [ "$state" = "present" ]; then
      raw="$(docker buildx imagetools inspect --raw "$ref")"
      platforms="$(printf '%s' "$raw" | jq -r '[.manifests[]? | select(.platform.os != "unknown") | "\(.platform.os)/\(.platform.architecture)=\(.digest)"] | sort | join(" ")')"
      expected="linux/amd64=${amd64} linux/arm64=${arm64}"
      if [ "$platforms" != "$expected" ]; then
        echo "::error::image ${ref} already exists with different content (${platforms:-no platform manifests}); a published version is immutable. Bump the version and tag again."
        exit 1
      fi
      state=identical
      echo "image ${ref} already holds exactly the scanned digests; continuing without re-tagging."
    else
      echo "image ${ref} is not published yet."
    fi
    echo "state=${state}"
    if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "state=${state}" >> "$GITHUB_OUTPUT"; fi
    ;;
  *)
    echo "::error::unknown artifact kind '${kind}'"
    exit 2
    ;;
esac

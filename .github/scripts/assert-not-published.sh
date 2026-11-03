#!/usr/bin/env bash
# R1-45: refuse to replace an immutable release artifact.
#
#   assert-not-published.sh image   <image-ref>            e.g. ghcr.io/lupusmalusdeviant/whiskers:0.16.0
#   assert-not-published.sh chart   <oci-chart> <version>  e.g. oci://ghcr.io/lupusmalusdeviant/charts/whiskers 0.16.0
#   assert-not-published.sh release <tag>                  e.g. v0.16.0 (GitHub release of $GITHUB_REPOSITORY)
#
# Exit 0 only when the registry/API says the artifact does NOT exist. If it exists, or if its existence
# cannot be established (auth error, outage, unexpected message), the script fails: a release run never
# overwrites a published version and never guesses. Moving tags (`X.Y`, `latest`) are not checked here.
set -euo pipefail

kind="${1:?usage: assert-not-published.sh image|chart|release ...}"

check_absent() {
  local what="$1"; shift
  local out
  if out="$("$@" 2>&1)"; then
    echo "::error::${what} already exists. A published version is immutable; bump the version and tag again."
    exit 1
  fi
  if printf '%s' "$out" | grep -Eqi 'not found|manifest unknown|name unknown'; then
    echo "${what} is not published yet."
    return 0
  fi
  echo "::error::Could not establish whether ${what} exists, refusing to publish: ${out}"
  exit 1
}

case "$kind" in
  image)
    ref="${2:?image reference required}"
    check_absent "image ${ref}" docker buildx imagetools inspect "$ref"
    ;;
  chart)
    chart="${2:?chart reference required}"
    version="${3:?chart version required}"
    check_absent "chart ${chart} ${version}" helm show chart "$chart" --version "$version"
    ;;
  release)
    tag="${2:?tag required}"
    check_absent "GitHub release ${tag}" gh release view "$tag" --repo "${GITHUB_REPOSITORY:?}"
    ;;
  *)
    echo "::error::unknown artifact kind '${kind}'"
    exit 2
    ;;
esac

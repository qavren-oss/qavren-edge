#!/usr/bin/env bash
# Repair path for native.yml's `reuse` job: an actions/cache restore for one native leg missed,
# so fetch that leg's uploaded artifact from a recent run on the default branch instead of
# taking the pull request red.
#
# Why this exists: the cache entry and the artifact are written by the same `build` job, but the
# cache save is best-effort - a lost key reservation, an interrupted run or a 10 GB-limit
# eviction leaves the artifact intact and the cache entry absent. Without this fallback, that
# state failed every subsequent managed-only PR, including documentation-only ones.
#
# How a run is chosen, and why NOT `gh run list --status success`:
#   * "successful run" is the wrong predicate. A run that failed only in `device-tests-ios`
#     still uploaded three perfectly good `native-*` artifacts, and on a repo whose newest runs
#     are red for an unrelated reason the success filter matches nothing at all - which is
#     exactly how the first real exercise of this fallback failed.
#   * The right predicate is "the newest completed run that STILL HAS this artifact". The
#     artifacts endpoint lists only unexpired artifacts, so artifact presence subsumes the
#     30-day retention check as well.
# So: list the 10 newest completed runs of ci.yml on the default branch (any conclusion), and
# walk them newest-first until one reports the requested artifact name. Each leg resolves
# independently - they may legitimately land on different runs - so the chosen id is memoised
# per artifact.
#
# The artifact is unpacked into exactly the directory the cache would have restored
# (foundation/native/artifacts), so the caller re-seeds the cache from it and re-uploads it
# under the unchanged artifact name; downstream ci.yml jobs cannot tell the two paths apart.
#
# Usage: bash .github/scripts/reuse-native-artifact.sh <artifact-name>
# Needs: GH_TOKEN with `actions: read`, GITHUB_REPOSITORY, QEDGE_FALLBACK_BRANCH, GITHUB_ENV.
set -euo pipefail

artifact="${1:?usage: reuse-native-artifact.sh <artifact-name>}"
dest="foundation/native/artifacts"
branch="${QEDGE_FALLBACK_BRANCH:?QEDGE_FALLBACK_BRANCH is not set}"
repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is not set}"

# Newest-first run ids for one workflow file on the default branch. Any conclusion: see above.
# A 404 (no such workflow file in this repo's history) is not an error here, just an empty list.
list_runs() {
  gh api "repos/$repo/actions/workflows/$1/runs?branch=$branch&status=completed&per_page=10" \
    --jq '.workflow_runs[].id' 2>/dev/null || true
}

# Unexpired artifact names for one run.
list_artifacts() {
  gh api "repos/$repo/actions/runs/$1/artifacts?per_page=100" \
    --jq '.artifacts[].name' 2>/dev/null || true
}

count_lines() {
  printf '%s' "$1" | grep -c . || true
}

# Each leg memoises its own run id, so three cold legs cost at most one run listing.
cache_var="QEDGE_FALLBACK_RUN_$(printf '%s' "$artifact" | tr 'a-z-' 'A-Z_')"
run_id="${!cache_var:-}"

if [ -z "$run_id" ]; then
  source_workflow="ci.yml"
  runs="$(list_runs "$source_workflow")"
  n="$(count_lines "$runs")"
  echo "$source_workflow: $n completed run(s) on $branch"
  if [ "$n" -eq 0 ]; then
    # Before the trigger dedup, the natives were also built by standalone `native` runs, and
    # those uploaded the identical artifact names. Worth one more look before giving up.
    source_workflow="native.yml"
    runs="$(list_runs "$source_workflow")"
    n="$(count_lines "$runs")"
    echo "ci.yml returned no runs; $source_workflow: $n completed run(s) on $branch"
  fi

  while IFS= read -r candidate; do
    [ -n "$candidate" ] || continue
    names="$(list_artifacts "$candidate")"
    echo "  run $candidate: $(count_lines "$names") unexpired artifact(s) [$(printf '%s' "$names" | tr '\n' ' ')]"
    if printf '%s\n' "$names" | grep -qxF "$artifact"; then
      run_id="$candidate"
      echo "  -> selected run $candidate for $artifact"
      break
    fi
  done <<EOF
$runs
EOF

  if [ -z "$run_id" ]; then
    echo "::error::No cached natives for this native tree, and none of the $n most recent completed $source_workflow run(s) on $branch still carries a '$artifact' artifact (artifacts expire after 30 days). Run the 'native' workflow on $branch via workflow_dispatch once, then re-run this PR."
    exit 1
  fi
  echo "$cache_var=$run_id" >> "${GITHUB_ENV:-/dev/null}"
fi

echo "cache miss for $artifact; recovering it from $branch run $run_id"
mkdir -p "$dest"
if ! gh run download "$run_id" --repo "$repo" -n "$artifact" -D "$dest"; then
  echo "::error::'$artifact' was listed on run $run_id but could not be downloaded. Run the 'native' workflow on $branch via workflow_dispatch once, then re-run this PR."
  exit 1
fi
if [ -z "$(find "$dest" -type f -print -quit)" ]; then
  echo "::error::'$artifact' from run $run_id unpacked to nothing."
  exit 1
fi
echo "recovered $artifact ($(find "$dest" -type f | wc -l) file(s)):"
find "$dest" -type f | sort | head -40

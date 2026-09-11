#!/usr/bin/env bash
# Repair path for native.yml's `reuse` job: an actions/cache restore for one native leg missed,
# so fetch that leg's uploaded artifact from the newest successful `ci` run on the default
# branch instead of taking the pull request red.
#
# Why this exists: the cache entry and the artifact are written by the same `build` job, but the
# cache save is best-effort - a lost key reservation, an interrupted run or a 10 GB-limit
# eviction leaves the artifact intact and the cache entry absent. Before this fallback, that
# state failed every subsequent managed-only PR, including documentation-only ones.
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

# Resolved once per job and carried to later steps, so three cold legs cost one API lookup.
if [ -z "${QEDGE_FALLBACK_RUN_ID:-}" ]; then
  QEDGE_FALLBACK_RUN_ID="$(gh run list --repo "$GITHUB_REPOSITORY" --workflow ci.yml \
    --branch "$branch" --status success --limit 1 \
    --json databaseId --jq '.[0].databaseId // empty')"
  if [ -z "$QEDGE_FALLBACK_RUN_ID" ]; then
    echo "::error::No cached natives for this native tree and no successful ci run on $branch to recover $artifact from. Run the 'native' workflow on $branch via workflow_dispatch once, then re-run this PR."
    exit 1
  fi
  echo "QEDGE_FALLBACK_RUN_ID=$QEDGE_FALLBACK_RUN_ID" >> "$GITHUB_ENV"
  export QEDGE_FALLBACK_RUN_ID
fi

echo "cache miss for $artifact; recovering it from $branch run $QEDGE_FALLBACK_RUN_ID"
mkdir -p "$dest"
if ! gh run download "$QEDGE_FALLBACK_RUN_ID" --repo "$GITHUB_REPOSITORY" -n "$artifact" -D "$dest"; then
  echo "::error::No cached natives for this native tree and run $QEDGE_FALLBACK_RUN_ID has no '$artifact' artifact (expired after its 30-day retention?). Run the 'native' workflow on $branch via workflow_dispatch once, then re-run this PR."
  exit 1
fi
if [ -z "$(find "$dest" -type f -print -quit)" ]; then
  echo "::error::'$artifact' from run $QEDGE_FALLBACK_RUN_ID unpacked to nothing."
  exit 1
fi
echo "recovered $artifact:"
find "$dest" -type f | sort | head -40

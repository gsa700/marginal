#!/usr/bin/env bash
# Marginal - promote an Edge build to Stable: the same binary, its pre-release
# flag cleared, marked latest so every Stable copy is offered it, and the first
# line of its notes changed from "An Edge build: ..." to "Promoted to Stable."
# (the line he wrote by hand on AlbumWall 0.6.13, 2026-10-10, the first promotion).
#
#   tools/promote.sh 0.2.11
#
# His call, every time: nothing here decides that a build has run on Edge
# long enough.
set -euo pipefail

REPO=gsa700/marginal
VERSION=${1:?usage: promote.sh X.Y.Z}
TAG="v$VERSION"
command -v gh >/dev/null || { echo "promote: the GitHub CLI (gh) is needed" >&2; exit 1; }

state=$(gh release view "$TAG" -R "$REPO" --json isPrerelease,isDraft -q '"\(.isPrerelease) \(.isDraft)"') \
    || { echo "promote: no release $TAG" >&2; exit 1; }
case "$state" in
    "true false") ;;
    "false false") echo "promote: $TAG is already Stable" >&2; exit 1 ;;
    *) echo "promote: $TAG is a draft; publish it on Edge first" >&2; exit 1 ;;
esac

notes=$(mktemp)
trap 'rm -f "$notes"' EXIT
gh release view "$TAG" -R "$REPO" --json body -q .body \
    | sed '1s/ An Edge build: offered to copies set to Edge until it is promoted to Stable\./ Promoted to Stable./' \
    > "$notes"
head -1 "$notes" | grep -q "Promoted to Stable" || echo "promote: the first line of the notes is not the usual one; promoting anyway, check it" >&2

gh release edit "$TAG" -R "$REPO" --prerelease=false --latest --notes-file "$notes" >/dev/null
latest=$(gh api "repos/$REPO/releases/latest" -q .tag_name)
[ "$latest" = "$TAG" ] || { echo "promote: done, but the latest release is $latest, not $TAG" >&2; exit 1; }
echo "PROMOTED: $TAG is Stable and latest. Every installed copy is offered it."

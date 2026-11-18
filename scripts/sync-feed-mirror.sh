#!/bin/bash

# Copy the published appcast to the old-feed mirror.
#
# Builds <= 1.4.11 read their Sparkle feed from
# https://dinhanhthi.github.io/PopGuy/appcast.xml. After the repo moved to
# Coprimal/PopGuy, GitHub no longer serves or redirects that address, so the user
# site dinhanhthi/dinhanhthi.github.io serves a copy at PopGuy/appcast.xml.
# scripts/publish-appcast.sh runs this script on every release so those builds
# always see the latest version. Never edit the mirror's appcast by hand.
#
# Usage: scripts/sync-feed-mirror.sh [appcast.xml]

set -euo pipefail

MIRROR_REPO="dinhanhthi/dinhanhthi.github.io"
MIRROR_PATH="PopGuy/appcast.xml"
SOURCE="${1:-appcast.xml}"

fail() {
  echo "error: $*" >&2
  exit 1
}

[[ -f "$SOURCE" ]] || fail "$SOURCE not found"

# A missing mirror file (HTTP 404, first run) means create it; any other error fails.
err_file="$(mktemp)"
trap 'rm -f "$err_file"' EXIT
if current_sha="$(gh api "repos/$MIRROR_REPO/contents/$MIRROR_PATH" --jq .sha 2>"$err_file")"; then
  [[ -n "$current_sha" ]] || fail "cannot read the sha of $MIRROR_PATH in $MIRROR_REPO"
elif grep -q 'HTTP 404' "$err_file"; then
  current_sha=""
else
  cat "$err_file" >&2
  fail "cannot read $MIRROR_PATH in $MIRROR_REPO"
fi
local_sha="$(git hash-object "$SOURCE")"

if [[ "$current_sha" == "$local_sha" ]]; then
  echo "mirror already current ($MIRROR_REPO/$MIRROR_PATH)"
  exit 0
fi

version="$(grep -o '<sparkle:shortVersionString>[^<]*' "$SOURCE" | head -1 | sed 's/.*>//' || true)"

# The body goes through stdin: the base64 appcast grows with every release.
# No sha creates the file; a sha updates it.
base64 <"$SOURCE" | tr -d '\n' |
  jq -Rs --arg message "chore: sync PopGuy appcast ${version:-update}" --arg sha "$current_sha" \
    '{message: $message, content: .} + (if $sha == "" then {} else {sha: $sha} end)' |
  gh api -X PUT "repos/$MIRROR_REPO/contents/$MIRROR_PATH" --input - --jq .commit.sha >/dev/null ||
  fail "cannot write $MIRROR_PATH in $MIRROR_REPO"

if [[ -z "$current_sha" ]]; then
  echo "mirror created: https://dinhanhthi.github.io/$MIRROR_PATH (${version:-unknown version})"
else
  echo "mirror updated: https://dinhanhthi.github.io/$MIRROR_PATH (${version:-unknown version})"
fi

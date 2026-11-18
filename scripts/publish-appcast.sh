#!/usr/bin/env bash
#
# publish-appcast.sh — Generate + commit + push the Sparkle appcast.
#
# Finds generate_appcast in DerivedData, runs it over the release zip, copies appcast.xml
# to the repo root, commits, and pushes. The zip must already be notarized + stapled
# (build-and-notarize.sh) and the GitHub release must exist (publish-release.sh).
#
# New builds read https://popguy.coprimal.dev/appcast.xml (deploy-pages.yml publishes it
# on the push); this script waits for that deploy and checks the live feed. Builds
# <= 1.4.11 read the old feed, which sync-feed-mirror.sh copies to the mirror.
#
# Usage:
#   scripts/publish-appcast.sh build/export/PopGuy-0.4.1.zip
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

ZIP="${1:-}"
[[ -n "$ZIP" ]] || { echo "usage: scripts/publish-appcast.sh <notarized-zip>" >&2; exit 1; }
[[ -f "$ZIP" ]] || { echo "error: zip not found: $ZIP" >&2; exit 1; }

# Derive version from zip filename (PopGuy-X.Y.Z.zip)
VERSION="$(basename "$ZIP" .zip | grep -oE '[0-9]+\.[0-9]+\.[0-9]+')"
[[ -n "$VERSION" ]] || { echo "error: cannot derive version from zip name: $ZIP" >&2; exit 1; }

# Find generate_appcast in DerivedData (built as part of the Sparkle SPM package).
# Use the newest PopGuy-* match: moving the project to macos/ left a stale PopGuy-<hash> folder.
ARTIFACTS="$(ls -td ~/Library/Developer/Xcode/DerivedData/PopGuy-*/SourcePackages/artifacts \
  2>/dev/null | head -1)"
GA="$(find "$ARTIFACTS" -name generate_appcast 2>/dev/null | head -1)"
[[ -n "$GA" ]] || {
  echo "error: generate_appcast not found in DerivedData." >&2
  echo "       Build the project first (build-and-notarize.sh runs a full archive)." >&2
  exit 1
}

echo "==> Generating appcast for v$VERSION"
echo "    generate_appcast: $GA"

mkdir -p build/appcast-archives
# Copy zip only — no .md notes file (keeps the in-app update prompt compact;
# full release notes live on GitHub via --full-release-notes-url).
cp "$ZIP" build/appcast-archives/
"$GA" build/appcast-archives/ \
  --full-release-notes-url "https://github.com/Coprimal/PopGuy/releases" \
  --download-url-prefix "https://github.com/Coprimal/PopGuy/releases/download/v$VERSION/"
cp build/appcast-archives/appcast.xml appcast.xml

echo "==> Committing and pushing appcast.xml"
# Safe to re-run: commit only when appcast.xml changed, and always push (retries a failed push).
git add appcast.xml
if ! git diff --cached --quiet -- appcast.xml; then
  git commit -m "release: appcast v$VERSION" -- appcast.xml
fi
git push

echo "==> Syncing the old-feed mirror (builds <= 1.4.11)"
"$REPO_ROOT/scripts/sync-feed-mirror.sh" appcast.xml

echo "==> Waiting for the new feed (deploy-pages.yml publishes it in ~1-3 min)"
# The live feed is the gate, not a workflow run: whichever push started the deploy, it is done
# once the feed lists this version. A failed or cancelled deploy shows up as a timeout here.
FEED_OK=""
for _ in $(seq 1 60); do
  # Read the whole feed first: with pipefail, grep -q closing the pipe early fails curl.
  if FEED="$(curl -fsSL --max-time 20 https://popguy.coprimal.dev/appcast.xml)" && grep -qF "/v$VERSION/" <<<"$FEED"; then
    FEED_OK=1
    break
  fi
  sleep 10
done
[[ -n "$FEED_OK" ]] || {
  echo "error: https://popguy.coprimal.dev/appcast.xml does not list v$VERSION after 10 min." >&2
  echo "       Check the deploy-pages.yml runs (gh run list --workflow deploy-pages.yml);" >&2
  echo "       re-run the failed one (gh run rerun <id>), then re-run this script." >&2
  exit 1
}

echo
echo "Done. appcast.xml is live."
echo "  New feed: https://popguy.coprimal.dev/appcast.xml"
echo "  Builds <= 1.4.11 read it through the mirror (scripts/sync-feed-mirror.sh)."

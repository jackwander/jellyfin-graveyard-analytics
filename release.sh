#!/usr/bin/env bash
#
# Cuts a release: stamps the version everywhere it is written down, publishes, zips the
# two shipped assemblies, and patches manifest.json with the real checksum and timestamp.
#
#   ./release.sh v1.2.0.0 --changelog "What changed."
#
# What this replaces. The old script published, zipped, printed an MD5 and stopped —
# manifest.json was then edited by hand, which is two fields (checksum, timestamp) that
# nothing verifies and that silently break every install when they are wrong. It also
# called `md5 -q`, which is BSD-only, so it did not run on Linux or in CI.
#
# The version lives in three files and Jellyfin reads two of them independently: the
# assembly version (from the csproj) is what the Plugins page shows, and manifest.json is
# what the catalogue offers. They have disagreed before. This script writes all three from
# one argument so they cannot.

set -euo pipefail

usage() {
  cat >&2 <<'EOF'
Usage: ./release.sh vX.X.X.X [--changelog "text"] [--dry-run]
       ./release.sh vX.X.X.X --changelog "text" --publish [--yes]

  vX.X.X.X      Four-part version. The leading v is optional and is stripped for
                the version fields; it is kept for the Releases/ directory and the
                git tag the sourceUrl points at.
  --changelog   Release notes for manifest.json. If omitted and this version is
                already in the manifest, its existing changelog is kept.
  --dry-run     Build, zip and compute the checksum, but leave manifest.json,
                the csproj and build.yaml untouched.
  --publish     Also commit the three stamped files, tag, and push both — which
                is what triggers .github/workflows/release.yml to build the same
                zip, re-check its checksum against the manifest, and publish the
                GitHub release. Refuses unless the tree is clean apart from those
                three files, HEAD is on master, and the tag is unused locally and
                on the remote.
  --yes         Do not ask for confirmation before pushing. Required when stdin
                is not a terminal.
  --skip-tests  Skip the test run. Only for re-cutting an artifact you have
                already tested; the release workflow runs them again regardless.
EOF
  exit 1
}

# Resolve the repo root from this script, not from the caller's working directory —
# the old script assumed it was invoked from the root and cd'd around relative to that.
REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

PROJECT_DIR="JellyfinGraveyardAnalytics"
CSPROJ="$PROJECT_DIR/JellyfinGraveyardAnalyticsPlugin.csproj"
BUILD_YAML="$PROJECT_DIR/build.yaml"
MANIFEST="manifest.json"
TEST_PROJECT="tests/GraveyardAnalytics.Tests/GraveyardAnalytics.Tests.csproj"
# manifest.json is served from this branch and is what every installed client polls, so a
# release cut anywhere else would tag code the catalogue never points at.
RELEASE_BRANCH="master"
REMOTE="origin"
PUBLISH_DIR="$PROJECT_DIR/bin/Release/net9.0/publish"
REPO_URL="https://github.com/jackwander/jellyfin-graveyard-analytics"

# Kept in step with build.yaml's artifacts list. Microsoft.Data.Sqlite is not here on
# purpose: the csproj compiles against it with ExcludeAssets="runtime" because Jellyfin
# already has it loaded, and a second copy is how you get a native-provider clash.
FILES=("Dapper.dll" "JellyfinGraveyardAnalyticsPlugin.dll")

RAW_VERSION="${1:-}"
[ -n "$RAW_VERSION" ] || usage
shift

CHANGELOG=""
CHANGELOG_SET=0
DRY_RUN=0
PUBLISH=0
ASSUME_YES=0
SKIP_TESTS=0

while [ $# -gt 0 ]; do
  case "$1" in
    --changelog)
      [ $# -ge 2 ] || { echo "❌ --changelog needs a value." >&2; usage; }
      CHANGELOG="$2"
      CHANGELOG_SET=1
      shift 2
      ;;
    --dry-run) DRY_RUN=1; shift ;;
    --publish) PUBLISH=1; shift ;;
    --yes|-y) ASSUME_YES=1; shift ;;
    --skip-tests) SKIP_TESTS=1; shift ;;
    *) echo "❌ Unknown argument: $1" >&2; usage ;;
  esac
done

if [ "$DRY_RUN" -eq 1 ] && [ "$PUBLISH" -eq 1 ]; then
  echo "❌ --dry-run and --publish contradict each other." >&2
  exit 1
fi

# Prompting is the default because the last step is a push, and a pushed tag cannot be
# taken back in any way that helps: clients poll the manifest on master.
if [ "$PUBLISH" -eq 1 ] && [ "$ASSUME_YES" -eq 0 ] && [ ! -t 0 ]; then
  echo "❌ --publish needs a terminal to confirm at, or --yes to say you meant it." >&2
  exit 1
fi

# --- version ---------------------------------------------------------------------
VERSION="${RAW_VERSION#v}"
if ! printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'; then
  echo "❌ '$RAW_VERSION' is not a four-part version. Jellyfin's manifest requires X.X.X.X." >&2
  exit 1
fi
TAG="v$VERSION"

command -v jq >/dev/null 2>&1 || {
  echo "❌ jq is required to patch $MANIFEST. Install it (brew install jq / apt install jq)." >&2
  exit 1
}

# --- checksum, portably ----------------------------------------------------------
# GNU coreutils has md5sum, BSD/macOS has md5 -q. The old script only knew the second.
md5_of() {
  if command -v md5sum >/dev/null 2>&1; then
    md5sum "$1" | cut -d' ' -f1
  elif command -v md5 >/dev/null 2>&1; then
    md5 -q "$1"
  else
    echo "❌ Neither md5sum nor md5 is available; cannot checksum the release." >&2
    exit 1
  fi
}

# targetAbi is the Jellyfin the plugin is actually compiled against, read from the pinned
# package rather than typed again. It was hardcoded, and the csproj pin has moved before.
ABI_SHORT="$(grep -o 'Include="Jellyfin.Controller" Version="[^"]*"' "$CSPROJ" \
  | head -1 | sed 's/.*Version="\([^"]*\)".*/\1/')"
if [ -z "$ABI_SHORT" ]; then
  echo "❌ Could not read the Jellyfin.Controller version out of $CSPROJ." >&2
  exit 1
fi
# 10.11.6 -> 10.11.6.0; a version already four-part is left alone.
case "$(printf '%s' "$ABI_SHORT" | tr -cd '.' | wc -c | tr -d ' ')" in
  2) TARGET_ABI="$ABI_SHORT.0" ;;
  3) TARGET_ABI="$ABI_SHORT" ;;
  *) echo "❌ Unexpected Jellyfin.Controller version '$ABI_SHORT'." >&2; exit 1 ;;
esac

echo "📦 Release $TAG  (targetAbi $TARGET_ABI)"
[ "$DRY_RUN" -eq 1 ] && echo "   dry run — no files will be rewritten"

# --- pre-flight for --publish ------------------------------------------------------
# All of it runs before the first file is rewritten, so a refusal leaves the tree exactly
# as it was found and there is nothing to undo by hand.
if [ "$PUBLISH" -eq 1 ]; then
  BRANCH="$(git rev-parse --abbrev-ref HEAD)"
  if [ "$BRANCH" != "$RELEASE_BRANCH" ]; then
    echo "❌ On '$BRANCH', but a release has to be cut from '$RELEASE_BRANCH' — that is the" >&2
    echo "   branch $MANIFEST is served from." >&2
    exit 1
  fi

  # Checked first, because it is the one that cannot be recovered from. A tag already on
  # the remote has a GitHub release and a checksum behind it, and installed clients have
  # that file; moving it breaks every install that trusted the old checksum.
  if git ls-remote --tags --exit-code "$REMOTE" "refs/tags/$TAG" >/dev/null 2>&1; then
    echo "❌ $REMOTE already has tag $TAG. That version is published; pick a new one." >&2
    exit 1
  fi

  # Only the three stamped files may differ. Anything else would ship unreviewed under a
  # "Release" message. Untracked files are ignored: test-release.sh lives here permanently
  # and is deliberately not in the repository.
  DIRTY="$(git status --porcelain --untracked-files=no \
    | awk '{ print $NF }' \
    | grep -v -e "^$CSPROJ$" -e "^$BUILD_YAML$" -e "^$MANIFEST$" || true)"
  if [ -n "$DIRTY" ]; then
    echo "❌ These files are modified and are not part of a release commit:" >&2
    printf '   %s\n' $DIRTY >&2
    echo "   Commit or stash them first." >&2
    exit 1
  fi

  if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
    EXISTING="$(git rev-parse "refs/tags/$TAG")"
    if [ "$EXISTING" != "$(git rev-parse HEAD)" ]; then
      echo "❌ Tag $TAG already exists locally and points at ${EXISTING:0:8}, not HEAD." >&2
      echo "   Delete it or use a new version number." >&2
      exit 1
    fi
    echo "ℹ️  Tag $TAG already exists here and matches HEAD; it will be reused."
  fi
fi

# --- tests -------------------------------------------------------------------------
# The release workflow runs these too, but it runs them after the tag exists — and a tag is
# the one thing here that cannot be withdrawn cleanly. Failing before the stamp is cheaper
# than failing after the push.
if [ "$SKIP_TESTS" -eq 0 ]; then
  echo "🧪 Testing..."
  dotnet test "$TEST_PROJECT" -c Release -p:TreatWarningsAsErrors=true --nologo --verbosity quiet
else
  echo "⚠️  Skipping tests (--skip-tests)."
fi

# --- stamp the version -----------------------------------------------------------
if [ "$DRY_RUN" -eq 0 ]; then
  # perl rather than sed -i: the -i flag takes an argument on BSD and does not on GNU,
  # which is the same portability trap the md5 call fell into.
  perl -pi -e "s|<Version>[^<]*</Version>|<Version>$VERSION</Version>|" "$CSPROJ"
  perl -pi -e "s|<AssemblyVersion>[^<]*</AssemblyVersion>|<AssemblyVersion>$VERSION</AssemblyVersion>|" "$CSPROJ"
  perl -pi -e "s|<FileVersion>[^<]*</FileVersion>|<FileVersion>$VERSION</FileVersion>|" "$CSPROJ"
  perl -pi -e "s|^version: \".*\"|version: \"$VERSION\"|" "$BUILD_YAML"
  echo "✅ Stamped $VERSION into $CSPROJ and $BUILD_YAML"
fi

# --- build -----------------------------------------------------------------------
# Warnings as errors here too, so a release cannot be cut from a tree CI would reject.
echo "🔨 Publishing..."
dotnet publish "$CSPROJ" -c Release -p:TreatWarningsAsErrors=true

DEST_DIR="Releases/$TAG"
ZIP_NAME="JellyfinGraveyardAnalytics.zip"
mkdir -p "$DEST_DIR"
rm -f "$DEST_DIR/$ZIP_NAME"

for FILE in "${FILES[@]}"; do
  if [ ! -f "$PUBLISH_DIR/$FILE" ]; then
    echo "❌ $FILE is missing from $PUBLISH_DIR — the publish did not produce what build.yaml promises." >&2
    exit 1
  fi
  cp "$PUBLISH_DIR/$FILE" "$DEST_DIR/"
  echo "✅ Staged $FILE"
done

# A zip records each entry's modification time, and `dotnet publish` stamps the plugin DLL
# with the moment it ran — so the same source produced a different archive, and a different
# md5, on every single build. That is not cosmetic: release.yml commits the manifest first
# and then *rebuilds* the zip in CI and refuses to publish if the checksums disagree, so
# that gate could never have passed. The pipeline had never been run end to end.
#
# The assemblies themselves are already byte-identical build to build (the .NET compiler is
# deterministic); only the timestamps moved. Flattening them to the zip epoch — 1980-01-01,
# the earliest the format can represent — makes the archive a function of its contents.
# -X drops the uid/gid and high-precision timestamp extras, which differ between the
# maintainer's machine and CI's runner even when the files do not.
echo "🗜️  Zipping..."
(
  cd "$DEST_DIR"
  touch -t 198001010000 "${FILES[@]}"
  zip -qX "$ZIP_NAME" "${FILES[@]}"
)

CHECKSUM="$(md5_of "$DEST_DIR/$ZIP_NAME")"
# -u so the stamp is UTC and not the releaser's timezone; both date implementations
# accept this spelling.
TIMESTAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

echo "🔐 md5 $CHECKSUM"

if [ "$DRY_RUN" -eq 1 ]; then
  echo "---"
  echo "🎉 Dry run complete. $DEST_DIR/$ZIP_NAME built; $MANIFEST untouched."
  exit 0
fi

# --- patch the manifest ----------------------------------------------------------
# Re-releasing the same version replaces its entry rather than adding a second one, so
# the script is safe to re-run after a failed upload.
if [ "$CHANGELOG_SET" -eq 0 ]; then
  CHANGELOG="$(jq -r --arg v "$VERSION" \
    'first(.[0].versions[] | select(.version == $v) | .changelog) // ""' "$MANIFEST")"
  if [ -z "$CHANGELOG" ]; then
    echo "⚠️  No --changelog given and $VERSION is new; writing a placeholder." >&2
    CHANGELOG="Release $VERSION."
  fi
fi

TMP_MANIFEST="$(mktemp)"
trap 'rm -f "$TMP_MANIFEST"' EXIT

jq --indent 2 \
  --arg v "$VERSION" \
  --arg log "$CHANGELOG" \
  --arg abi "$TARGET_ABI" \
  --arg url "$REPO_URL/releases/download/$TAG/$ZIP_NAME" \
  --arg sum "$CHECKSUM" \
  --arg ts "$TIMESTAMP" \
  '.[0].versions |= ([{
      version: $v,
      changelog: $log,
      targetAbi: $abi,
      sourceUrl: $url,
      checksum: $sum,
      timestamp: $ts
    }] + map(select(.version != $v)))' \
  "$MANIFEST" > "$TMP_MANIFEST"

# jq exits non-zero on a parse error and set -e would have caught it, but an empty
# result would still truncate the manifest — check before overwriting.
[ -s "$TMP_MANIFEST" ] || { echo "❌ Refusing to write an empty $MANIFEST." >&2; exit 1; }
mv "$TMP_MANIFEST" "$MANIFEST"
trap - EXIT

echo "✅ Patched $MANIFEST (checksum + timestamp $TIMESTAMP)"
echo "---"
echo "🎉 $TAG is ready."
echo "📍 $DEST_DIR/$ZIP_NAME"

if [ "$PUBLISH" -eq 0 ]; then
  echo ""
  echo "Next — or re-run with --publish to have this do it:"
  echo "  git add $CSPROJ $BUILD_YAML $MANIFEST"
  echo "  git commit -m \"Release $TAG\""
  echo "  git tag $TAG"
  echo "  git push $REMOTE $RELEASE_BRANCH && git push $REMOTE $TAG"
  echo ""
  echo "The tag push is what publishes: release.yml rebuilds this exact zip, re-checks its"
  echo "checksum against $MANIFEST, and attaches it to the GitHub release. Nothing is"
  echo "uploaded by hand — the build is reproducible, so CI's zip is byte-for-byte this one."
  echo "---"
  exit 0
fi

# --- publish -----------------------------------------------------------------------
echo ""
git add "$CSPROJ" "$BUILD_YAML" "$MANIFEST"

# Re-checked after staging, not just before: the stamp is the only thing that should have
# changed the tree since the pre-flight.
STAGED="$(git diff --cached --name-only)"
UNEXPECTED="$(printf '%s\n' "$STAGED" \
  | grep -v -e "^$CSPROJ$" -e "^$BUILD_YAML$" -e "^$MANIFEST$" -e '^$' || true)"
if [ -n "$UNEXPECTED" ]; then
  echo "❌ Refusing to commit: something other than the three stamped files is staged:" >&2
  printf '   %s\n' $UNEXPECTED >&2
  exit 1
fi

if [ -z "$STAGED" ]; then
  echo "ℹ️  Nothing to commit — the three files already say $VERSION."
else
  git commit -q -m "Release $TAG"
  echo "✅ Committed Release $TAG"
fi

if ! git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
  git tag "$TAG"
  echo "✅ Tagged $TAG"
fi

AHEAD="$(git rev-list --count "$REMOTE/$RELEASE_BRANCH..$RELEASE_BRANCH" 2>/dev/null || echo '?')"
echo ""
echo "About to push to $REMOTE:"
echo "  $RELEASE_BRANCH → $(git rev-parse --short HEAD)  ($AHEAD commit(s) ahead)"
echo "  $TAG → publishes the GitHub release and the manifest entry clients poll"
echo ""

if [ "$ASSUME_YES" -eq 0 ]; then
  printf 'Type the version to confirm (%s): ' "$VERSION"
  read -r CONFIRM
  if [ "$CONFIRM" != "$VERSION" ]; then
    echo "❌ Not confirmed. Nothing pushed — the commit and tag are local, so 'git reset'" >&2
    echo "   and 'git tag -d $TAG' undo them." >&2
    exit 1
  fi
fi

# Branch first. If the tag arrived first, release.yml would start building a commit the
# remote branch did not have yet, and its manifest check would read the old file.
git push "$REMOTE" "$RELEASE_BRANCH"
echo "✅ Pushed $RELEASE_BRANCH"
git push "$REMOTE" "$TAG"
echo "✅ Pushed $TAG"

echo "---"
echo "🚀 $TAG published. release.yml is now building it:"
echo "   $REPO_URL/actions"
echo "   It re-checks the checksum against $MANIFEST and fails the release rather than"
echo "   attaching a zip that disagrees with what clients will verify."
echo "---"

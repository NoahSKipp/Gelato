#!/usr/bin/env bash
VERSION_FILE=build.yaml
VERSION_PATTERN="^version:"
PATCH_FALLBACK=0
read_version() { sed -n 's/^version: "\(.*\)"$/\1/p'; }
write_version() { sed -i "s/^version: .*/version: \"$1\"/" "$VERSION_FILE"; }

# Cuts a release: the version git-cliff computes is written to the version
# file, committed, pushed, and released on GitHub from that commit.
#
# Safe to run again after a failure part way. A version that was written
# and pushed but never released (no tag) is released from the commit that
# wrote it, with the notes it would have had; a version that is already
# released ends the run cleanly.
#
#   VERSION=1.2.3   complete this version; it must be the one in the version file
#   DRY_RUN=1       print what would happen, change nothing
set -euo pipefail
cd "$(dirname "$0")/.."

dry=${DRY_RUN:-0}
run() {
  if [ "$dry" = 1 ]; then echo "[dry run] $*"; else "$@"; fi
}
tag_exists() { git rev-parse -q --verify "refs/tags/$1" >/dev/null; }

git fetch --tags --quiet
last=$(git describe --tags --abbrev=0 2>/dev/null || true)
prefix=""
case "$last" in v*) prefix=v ;; esac
current=$(read_version < "$VERSION_FILE")
[ -n "$current" ] || { echo "no version found in $VERSION_FILE" >&2; exit 1; }
notes=$(mktemp)
trap 'rm -f "$notes"' EXIT

if [ -n "${VERSION:-}" ]; then
  tag="$prefix${VERSION#v}"
  if tag_exists "$tag"; then
    echo "$tag is already released, nothing to do."
    exit 0
  fi
  if [ "${VERSION#v}" != "$current" ]; then
    echo "VERSION=$tag, but $VERSION_FILE is at $current: only the version already written can be completed." >&2
    exit 1
  fi
  mode=resume
elif ! tag_exists "$prefix$current" && [ "$prefix$current" != "$last" ]; then
  tag="$prefix$current"
  echo "$VERSION_FILE is at $current, which was never released: completing it."
  mode=resume
else
  if [ -n "$last" ] && [ -z "$(git log --format=%h "$last..HEAD")" ]; then
    echo "No commits since $last, nothing to release."
    exit 0
  fi
  tag=$(git cliff --bumped-version 2>/dev/null)
  if [ -n "$last" ] && [ "$tag" = "$last" ]; then
    if [ "$PATCH_FALLBACK" = 1 ]; then
      # Commits marked [skip changelog] don't count toward git-cliff's bump,
      # so a release made only of those would reuse the last tag.
      tag=$(echo "$last" | awk -F. -v OFS=. '{$NF=$NF+1; print}')
    else
      echo "No commits since $last that change the version, nothing to release."
      exit 0
    fi
  fi
  if tag_exists "$tag"; then
    echo "$tag is already released, nothing to do."
    exit 0
  fi
  mode=bump
fi

if [ "$mode" = resume ]; then
  # The commit that wrote this version, and the notes it would have had.
  target=$(git log -1 --format=%H -G"$VERSION_PATTERN" -- "$VERSION_FILE")
  if [ "$(git show "$target:$VERSION_FILE" | read_version)" != "$current" ]; then
    echo "could not find the commit that set $VERSION_FILE to $current" >&2
    exit 1
  fi
  since=$(git describe --tags --abbrev=0 "$target^" 2>/dev/null || true)
  echo "Release $tag from $(git log -1 --format='%h %s' "$target")"
  git cliff --tag "$tag" --strip all ${since:+"$since..$target^"} > "$notes" 2>/dev/null
else
  echo "New version will be: $tag"
  git cliff --unreleased --tag "$tag" --strip all > "$notes" 2>/dev/null
  echo "Updating $VERSION_FILE to ${tag#v}"
  if [ "$dry" = 1 ]; then
    echo "[dry run] write ${tag#v} to $VERSION_FILE, commit \"chore(release): bump version to $tag\", push"
    target="<the bump commit>"
  else
    write_version "${tag#v}"
    git add "$VERSION_FILE"
    git commit -m "chore(release): bump version to $tag"
    git push
    target=$(git rev-parse HEAD)
  fi
fi

echo "Release notes:"
sed 's/^/  /' "$notes"

# GitHub sometimes answers 5xx; a failed answer can still have created the
# release, so look before trying again.
for wait in 5 15 45 0; do
  if [ "$dry" = 1 ]; then
    run gh release create "$tag" --target "$target" --title "$tag" --notes-file "$notes"
    break
  fi
  if out=$(gh release create "$tag" --target "$target" --title "$tag" --notes-file "$notes" 2>&1); then
    echo "$out"
    break
  fi
  echo "$out" >&2
  if gh release view "$tag" >/dev/null 2>&1; then
    echo "$tag exists after all."
    break
  fi
  if ! echo "$out" | grep -qE 'HTTP 5[0-9][0-9]' || [ "$wait" = 0 ]; then
    echo "Release $tag failed; run the release again with VERSION=${tag#v} to complete it." >&2
    exit 1
  fi
  echo "GitHub error, trying again in ${wait}s"
  sleep "$wait"
done
if [ "$dry" = 1 ]; then echo "Dry run finished, nothing was changed."; else echo "Release $tag created."; fi

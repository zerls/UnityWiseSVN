#!/usr/bin/env bash
# WiseSVN UPM Packaging Script
# Usage: ./Scripts/pack.sh <version> [--dry-run] [--no-push]
# Example:
#   ./Scripts/pack.sh 1.6.0 --dry-run     # preview only
#   ./Scripts/pack.sh 1.6.0               # full release
#   ./Scripts/pack.sh 1.6.0 --no-push     # bump + commit + tag, but skip push

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
PACKAGE_JSON="$REPO_ROOT/Assets/DevLocker/VersionControl/WiseSVN/package.json"
CHANGELOG="$REPO_ROOT/Docs/CHANGELOG.md"
SUBTREE_PREFIX="Assets/DevLocker/VersionControl/WiseSVN"

DRY_RUN=false
NO_PUSH=false
VERSION=""

# Parse args
for arg in "$@"; do
    case "$arg" in
        --dry-run) DRY_RUN=true ;;
        --no-push) NO_PUSH=true ;;
        *) VERSION="$arg" ;;
    esac
done

# ---------- Validation ----------

if [ -z "$VERSION" ]; then
    echo "Usage: $0 <version> [--dry-run] [--no-push]"
    echo "Example: $0 1.6.0"
    exit 1
fi

if ! echo "$VERSION" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    echo "Error: version must be semver format (X.Y.Z), got: $VERSION"
    exit 1
fi

if [ ! -f "$PACKAGE_JSON" ]; then
    echo "Error: package.json not found at $PACKAGE_JSON"
    exit 1
fi

cd "$REPO_ROOT"

# Check git working tree is clean
if ! git diff-index --quiet HEAD --; then
    if [ "$DRY_RUN" = false ]; then
        echo "Error: working tree has uncommitted changes. Commit or stash them first."
        exit 1
    fi
fi

# Get current version for reference
CURRENT_VERSION=$(grep -o '"version": *"[^"]*"' "$PACKAGE_JSON" | head -1 | sed 's/.*"\([^"]*\)".*/\1/')
TODAY=$(date +%Y-%m-%d)
NEW_ENTRY="## [$VERSION] - $TODAY"
TAG="v$VERSION"
COMMIT_MSG="Updated package version to $VERSION"

echo "Current version: $CURRENT_VERSION"
echo "New version:     $VERSION"
echo "Dry run:         $DRY_RUN"
echo ""

# ---------- Step 1: Update package.json ----------
echo "==> Updating version in package.json"
if [ "$DRY_RUN" = false ]; then
    # `sed -i.bak` is portable (GNU + BSD); we explicitly remove the backup after.
    sed -i.bak -e "s/\"version\": *\"[^\"]*\"/\"version\": \"$VERSION\"/" "$PACKAGE_JSON"
    rm -f "${PACKAGE_JSON}.bak"
    echo "  package.json version set to $VERSION"
else
    echo "  (dry-run) Would update version in package.json"
fi

# ---------- Step 2: Update CHANGELOG.md ----------
echo "==> Updating CHANGELOG.md"
if ! grep -qF "[$VERSION]" "$CHANGELOG" 2>/dev/null; then
    # Use awk for cross-platform compatibility (GNU sed `a\` syntax differs on BSD/macOS).
    if [ "$DRY_RUN" = false ]; then
        TMPFILE="${CHANGELOG}.tmp.$$"
        awk -v entry="$NEW_ENTRY" '
            BEGIN { inserted = 0 }
            !inserted && /^## \[/ {
                print entry
                print ""
                inserted = 1
            }
            { print }
            END {
                if (!inserted) {
                    print ""
                    print entry
                }
            }
        ' "$CHANGELOG" > "$TMPFILE"
        mv "$TMPFILE" "$CHANGELOG"
        echo "  Added ${NEW_ENTRY} to CHANGELOG.md"
        echo "  Tip: edit CHANGELOG.md to fill in the release notes before committing."
    else
        echo "  (dry-run) Would insert ${NEW_ENTRY} into CHANGELOG.md"
    fi
else
    echo "  Version $VERSION already in CHANGELOG.md — skipping"
fi

# ---------- Step 3: Commit ----------
echo "==> Committing: $COMMIT_MSG"
if [ "$DRY_RUN" = false ]; then
    git add "$PACKAGE_JSON" "$CHANGELOG"
    git commit -m "$COMMIT_MSG"
    echo "  Committed."
else
    echo "  (dry-run) Would: git add && git commit -m \"$COMMIT_MSG\""
fi

# ---------- Step 4: Tag ----------
echo "==> Creating tag: $TAG"
if [ "$DRY_RUN" = false ]; then
    git tag "$TAG" -m "$COMMIT_MSG"
    echo "  Tagged."
else
    echo "  (dry-run) Would: git tag $TAG"
fi

# Rollback helper: undo the local tag if push fails so the user can retry the script.
rollback_tag() {
    echo "  Rolling back local tag $TAG so a retry is possible..."
    git tag -d "$TAG" >/dev/null 2>&1 || true
}

# ---------- Step 5: Subtree push ----------
echo "==> Pushing subtree to origin upm"
if [ "$DRY_RUN" = true ]; then
    echo "  (dry-run) Would: git subtree push --prefix $SUBTREE_PREFIX origin upm"
elif [ "$NO_PUSH" = true ]; then
    echo "  (--no-push) Skipped"
else
    if ! git subtree push --prefix "$SUBTREE_PREFIX" origin upm; then
        echo "  ERROR: subtree push failed."
        rollback_tag
        exit 1
    fi
    echo "  Subtree pushed."
fi

# ---------- Step 6: Push tag ----------
echo "==> Pushing tag $TAG"
if [ "$DRY_RUN" = true ]; then
    echo "  (dry-run) Would: git push origin $TAG"
elif [ "$NO_PUSH" = true ]; then
    echo "  (--no-push) Skipped"
else
    if ! git push origin "$TAG"; then
        echo "  ERROR: tag push failed."
        rollback_tag
        exit 1
    fi
    echo "  Tag pushed."
fi

# ---------- Done ----------
echo ""
echo "Done. Version: $VERSION"
if [ "$DRY_RUN" = true ]; then
    echo "(dry-run — no changes were made)"
fi

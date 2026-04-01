#!/bin/bash
set -euo pipefail

BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STAGING_DIR="${RUSH_STAGING:-$SCRIPT_DIR/dist}"
BUILD_SHA_FILE="$SCRIPT_DIR/.last-build-sha"
PUBLISH_TMP="$SCRIPT_DIR/.publish-tmp"

export PATH="/opt/homebrew/opt/dotnet/bin:/opt/homebrew/Cellar/dotnet/10.0.105/libexec:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"

# ── Check if rebuild is needed ───────────────────────────────────────
CURRENT_SHA=$(git -C "$SCRIPT_DIR" rev-parse HEAD 2>/dev/null || echo "unknown")
LAST_SHA=""
if [[ -f "$BUILD_SHA_FILE" ]]; then
    LAST_SHA=$(cat "$BUILD_SHA_FILE")
fi

FORCE="${FORCE:-false}"
if [[ "$CURRENT_SHA" == "$LAST_SHA" && "$FORCE" != "true" && -f "$BIN_LINK" ]]; then
    VERSION=$("$BIN_LINK" --version 2>/dev/null || echo "unknown")
    echo "Already built: $VERSION (commit ${CURRENT_SHA:0:7})"
    echo "Use FORCE=true ./install.sh to rebuild anyway."
else
    # ── Build to temp dir (never conflicts with running binary) ──────
    rm -rf "$PUBLISH_TMP" "$PUBLISH_TMP"-*
    echo "Building release binary..."
    dotnet publish -c Release -r osx-arm64 -o "$PUBLISH_TMP" "$SCRIPT_DIR"
    echo ""

    # ── Install: copy to /usr/local/lib/rush ─────────────────────────
    sudo rm -rf /usr/local/lib/rush
    sudo mkdir -p /usr/local/lib/rush
    sudo cp -rf "$PUBLISH_TMP"/* /usr/local/lib/rush/
    sudo rm -f "$BIN_LINK"
    sudo ln -sf /usr/local/lib/rush/rush "$BIN_LINK"

    VERSION=$("$BIN_LINK" --version 2>/dev/null || echo "unknown")
    echo "$CURRENT_SHA" > "$BUILD_SHA_FILE"
fi

# ── Register as login shell (sudo only if needed) ───────────────────
if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
    echo "  → $SHELLS_FILE (registering as valid shell)"
    echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
fi

echo ""
echo "Installed: $VERSION"

# ── Distribution: wait for CI, download native binaries (--full) ─────
if [[ "${1:-}" == "--full" ]]; then
    mkdir -p "$STAGING_DIR"

    echo ""
    echo "Waiting for CI to build native binaries..."

    # Find the CI run for the current commit
    RUN_ID=$(gh run list -R mhasse1/rush --branch main --json databaseId,headSha \
        -q ".[] | select(.headSha == \"$CURRENT_SHA\") | .databaseId" 2>/dev/null | head -1)

    if [[ -z "$RUN_ID" ]]; then
        echo "  ! No CI run found for $CURRENT_SHA. Push first, then run --full."
        echo "  Falling back to local cross-compile..."
        echo "  → Building Windows x64..."
        dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -o "$PUBLISH_TMP-win-x64" "$SCRIPT_DIR" > /dev/null
        echo "  → Building Linux x64..."
        dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true -o "$PUBLISH_TMP-linux-x64" "$SCRIPT_DIR" > /dev/null

        rm -f "$STAGING_DIR/rush_x64.exe" "$STAGING_DIR/rush-linux-x64"
        cp "$PUBLISH_TMP-win-x64/rush.exe" "$STAGING_DIR/rush_x64.exe"
        cp "$PUBLISH_TMP-linux-x64/rush" "$STAGING_DIR/rush-linux-x64"
    else
        echo "  CI run: $RUN_ID"

        # Wait for it to complete (gh run watch exits when done)
        gh run watch "$RUN_ID" -R mhasse1/rush --exit-status 2>/dev/null || {
            echo "  ! CI failed. Check: gh run view $RUN_ID -R mhasse1/rush"
            exit 1
        }

        echo "  CI passed. Downloading artifacts..."

        # Download native binaries built on actual target platforms
        rm -f "$STAGING_DIR/rush.exe" "$STAGING_DIR/rush_x64.exe" "$STAGING_DIR/rush-linux-x64" "$STAGING_DIR/rush-osx-arm64"

        gh run download "$RUN_ID" -R mhasse1/rush -n rush-win-x64 -D "$STAGING_DIR" 2>/dev/null && \
            mv "$STAGING_DIR/rush.exe" "$STAGING_DIR/rush_x64.exe" && \
            echo "  → $STAGING_DIR/rush_x64.exe (win-x64, native CI build)" || \
            echo "  ! Failed to download win-x64 artifact"

        gh run download "$RUN_ID" -R mhasse1/rush -n rush-linux-x64 -D "$STAGING_DIR" 2>/dev/null && \
            mv "$STAGING_DIR/rush" "$STAGING_DIR/rush-linux-x64" && \
            echo "  → $STAGING_DIR/rush-linux-x64 (linux-x64, native CI build)" || \
            echo "  ! Failed to download linux-x64 artifact"

        gh run download "$RUN_ID" -R mhasse1/rush -n rush-osx-arm64 -D "$STAGING_DIR" 2>/dev/null && \
            mv "$STAGING_DIR/rush" "$STAGING_DIR/rush-osx-arm64" 2>/dev/null && \
            echo "  → $STAGING_DIR/rush-osx-arm64 (osx-arm64, native CI build)" || \
            echo "  ! Failed to download osx-arm64 artifact"
    fi

    # Docs (always from local — they're not platform-specific)
    rm -f "$STAGING_DIR/rush-lang-spec.yaml" "$STAGING_DIR/user-manual.md"
    ln -f "$SCRIPT_DIR/docs/rush-lang-spec.yaml" "$STAGING_DIR/rush-lang-spec.yaml"
    ln -f "$SCRIPT_DIR/docs/user-manual.md" "$STAGING_DIR/user-manual.md"
    echo "  → $STAGING_DIR/rush-lang-spec.yaml"
    echo "  → $STAGING_DIR/user-manual.md"

    echo ""
    echo "Staged: $STAGING_DIR/"
    ls -lh "$STAGING_DIR/"
fi

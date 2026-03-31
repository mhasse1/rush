#!/bin/bash
set -euo pipefail

BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STAGING_DIR="${RUSH_STAGING:-$SCRIPT_DIR/dist}"
BUILD_SHA_FILE="$SCRIPT_DIR/.last-build-sha"

# Publish to a temp dir so dotnet never conflicts with the running binary
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
    rm -rf "$PUBLISH_TMP"
    echo "Building release binary..."
    dotnet publish -c Release -r osx-arm64 -o "$PUBLISH_TMP" "$SCRIPT_DIR"
    echo ""

    # ── Install: copy binary (Unix unlinks first → no lock conflict) ──
    sudo cp -f "$PUBLISH_TMP/rush" "$BIN_LINK"
    sudo chmod +x "$BIN_LINK"

    VERSION=$("$BIN_LINK" --version 2>/dev/null || echo "unknown")

    # ── Cross-compile (only with --full) ─────────────────────────────
    if [[ "${1:-}" == "--full" ]]; then
        echo "  → Building Windows ARM64..."
        dotnet publish -c Release -r win-arm64 -o "$PUBLISH_TMP-win-arm64" "$SCRIPT_DIR" > /dev/null
        echo "  → Building Windows x64..."
        dotnet publish -c Release -r win-x64 -o "$PUBLISH_TMP-win-x64" "$SCRIPT_DIR" > /dev/null
        echo "  → Building Linux x64..."
        dotnet publish -c Release -r linux-x64 -o "$PUBLISH_TMP-linux-x64" "$SCRIPT_DIR" > /dev/null
    fi

    # Save build SHA
    echo "$CURRENT_SHA" > "$BUILD_SHA_FILE"
fi

# ── Register as login shell (sudo only if needed) ───────────────────
if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
    echo "  → $SHELLS_FILE (registering as valid shell)"
    echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
fi

echo ""
echo "Installed: $VERSION"

# ── Stage to distribution dir (only with --full) ────────────────────
if [[ "${1:-}" == "--full" ]]; then
    mkdir -p "$STAGING_DIR"

    # Remove old hardlinks
    rm -f "$STAGING_DIR/rush.exe" "$STAGING_DIR/rush_x64.exe" \
          "$STAGING_DIR/rush-linux-x64" \
          "$STAGING_DIR/rush-lang-spec.yaml" "$STAGING_DIR/user-manual.md"

    # Windows ARM64 → rush.exe
    ln "$PUBLISH_TMP-win-arm64/rush.exe" "$STAGING_DIR/rush.exe"
    echo "  → $STAGING_DIR/rush.exe (win-arm64)"

    # Windows x64 → rush_x64.exe
    ln "$PUBLISH_TMP-win-x64/rush.exe" "$STAGING_DIR/rush_x64.exe"
    echo "  → $STAGING_DIR/rush_x64.exe (win-x64)"

    # Linux x64
    ln "$PUBLISH_TMP-linux-x64/rush" "$STAGING_DIR/rush-linux-x64"
    echo "  → $STAGING_DIR/rush-linux-x64 (linux-x64)"

    # Docs
    ln -f "$SCRIPT_DIR/docs/rush-lang-spec.yaml" "$STAGING_DIR/rush-lang-spec.yaml"
    ln -f "$SCRIPT_DIR/docs/user-manual.md" "$STAGING_DIR/user-manual.md"
    echo "  → $STAGING_DIR/rush-lang-spec.yaml"
    echo "  → $STAGING_DIR/user-manual.md"

    echo ""
    echo "Staged: $STAGING_DIR/"
    ls -lh "$STAGING_DIR/"
fi

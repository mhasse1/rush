#!/bin/bash
set -euo pipefail

BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net10.0/osx-arm64/publish"

export PATH="/opt/homebrew/opt/dotnet/bin:/opt/homebrew/Cellar/dotnet/10.0.105/libexec:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/Cellar/dotnet/10.0.105/libexec}"

# ── Build ────────────────────────────────────────────────────────────
echo "Building release binary..."
dotnet publish -c Release -r osx-arm64 "$SCRIPT_DIR"
echo ""

VERSION=$("$PUBLISH_DIR/rush" --version 2>/dev/null || echo "unknown")

# ── Symlink to publish dir (default) ────────────────────────────────
# Every dotnet publish instantly updates the installed binary.
# Remove old copy-based install if it exists.
OLD_INSTALL="/usr/local/lib/rush"
if [[ -d "$OLD_INSTALL" ]]; then
    echo "  → removing $OLD_INSTALL/ (switching to direct symlink)"
    sudo rm -rf "$OLD_INSTALL"
fi

# Create or update symlink
if [[ ! -L "$BIN_LINK" ]] || [[ "$(readlink "$BIN_LINK")" != "$PUBLISH_DIR/rush" ]]; then
    echo "  → $BIN_LINK → $PUBLISH_DIR/rush"
    sudo ln -sf "$PUBLISH_DIR/rush" "$BIN_LINK"
fi

# Register as valid login shell
if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
    echo "  → $SHELLS_FILE (registering as valid shell)"
    echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
fi

echo ""
echo "Installed: $VERSION  (dev symlink)"

# ── Windows cross-compile (only with --full) ─────────────────────────
if [[ "${1:-}" == "--full" ]]; then
    WIN_STAGING="$HOME/Resilio/coi/src/rush-tests"
    mkdir -p "$WIN_STAGING"

    echo ""
    echo "  → Building Windows ARM64..."
    dotnet publish -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true "$SCRIPT_DIR" > /dev/null
    cp "$SCRIPT_DIR/bin/Release/net10.0/win-arm64/publish/rush.exe" "$WIN_STAGING/rush.exe"
    echo "  → $WIN_STAGING/rush.exe"

    echo "  → Building Windows x64..."
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "$SCRIPT_DIR" > /dev/null
    cp "$SCRIPT_DIR/bin/Release/net10.0/win-x64/publish/rush.exe" "$WIN_STAGING/rush-x64.exe"
    echo "  → $WIN_STAGING/rush-x64.exe"

    echo "  → Copying docs..."
    cp "$SCRIPT_DIR/docs/rush-lang-spec.yaml" "$WIN_STAGING/"
    cp "$SCRIPT_DIR/docs/user-manual.md" "$WIN_STAGING/"
fi

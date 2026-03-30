#!/bin/bash
set -euo pipefail

BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net10.0/osx-arm64/publish"

export PATH="/opt/homebrew/opt/dotnet/bin:/opt/homebrew/Cellar/dotnet/10.0.105/libexec:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"

# ── Build ────────────────────────────────────────────────────────────
echo "Building release binary..."
dotnet publish -c Release -r osx-arm64 "$SCRIPT_DIR"
echo ""

VERSION=$("$PUBLISH_DIR/rush" --version 2>/dev/null || echo "unknown")

# ── Symlink to publish dir ───────────────────────────────────────────
if [[ ! -L "$BIN_LINK" ]] || [[ "$(readlink "$BIN_LINK" 2>/dev/null)" != "$PUBLISH_DIR/rush" ]]; then
    # Remove old wrapper or stale symlink
    if [[ -f "$BIN_LINK" ]] || [[ -L "$BIN_LINK" ]]; then
        sudo rm -f "$BIN_LINK"
    fi
    echo "  → $BIN_LINK → $PUBLISH_DIR/rush"
    sudo ln -sf "$PUBLISH_DIR/rush" "$BIN_LINK"
fi

# ── Register as login shell (sudo only if needed) ───────────────────
if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
    echo "  → $SHELLS_FILE (registering as valid shell)"
    echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
fi

echo ""
echo "Installed: $VERSION  ($(du -h "$PUBLISH_DIR/rush" | cut -f1 | xargs))"

# ── Windows cross-compile (only with --full) ─────────────────────────
if [[ "${1:-}" == "--full" ]]; then
    WIN_STAGING="${WIN_STAGING:-$SCRIPT_DIR/dist}"
    mkdir -p "$WIN_STAGING"

    echo ""
    echo "  → Building Windows ARM64..."
    dotnet publish -c Release -r win-arm64 "$SCRIPT_DIR" > /dev/null
    cp "$SCRIPT_DIR/bin/Release/net10.0/win-arm64/publish/rush.exe" "$WIN_STAGING/rush-arm64.exe"
    echo "  → $WIN_STAGING/rush-arm64.exe"

    echo "  → Building Windows x64..."
    dotnet publish -c Release -r win-x64 "$SCRIPT_DIR" > /dev/null
    cp "$SCRIPT_DIR/bin/Release/net10.0/win-x64/publish/rush.exe" "$WIN_STAGING/rush-x64.exe"
    echo "  → $WIN_STAGING/rush-x64.exe"

    echo "  → Building Linux x64..."
    dotnet publish -c Release -r linux-x64 "$SCRIPT_DIR" > /dev/null
    cp "$SCRIPT_DIR/bin/Release/net10.0/linux-x64/publish/rush" "$WIN_STAGING/rush-linux-x64"
    echo "  → $WIN_STAGING/rush-linux-x64"
fi

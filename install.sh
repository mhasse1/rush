#!/bin/bash
set -euo pipefail

BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net10.0/osx-arm64/publish"
STAGING_DIR="${RUSH_STAGING:-$SCRIPT_DIR/dist}"

export PATH="/opt/homebrew/opt/dotnet/bin:/opt/homebrew/Cellar/dotnet/10.0.105/libexec:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"

# ── Build ────────────────────────────────────────────────────────────
echo "Building release binary..."
dotnet publish -c Release -r osx-arm64 "$SCRIPT_DIR"
echo ""

VERSION=$("$PUBLISH_DIR/rush" --version 2>/dev/null || echo "unknown")

# ── Symlink to publish dir ───────────────────────────────────────────
if [[ ! -L "$BIN_LINK" ]] || [[ "$(readlink "$BIN_LINK" 2>/dev/null)" != "$PUBLISH_DIR/rush" ]]; then
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

# ── Windows cross-compile + Resilio staging (only with --full) ───────
if [[ "${1:-}" == "--full" ]]; then
    echo ""
    echo "  → Building Windows ARM64..."
    dotnet publish -c Release -r win-arm64 "$SCRIPT_DIR" > /dev/null
    echo "  → Building Windows x64..."
    dotnet publish -c Release -r win-x64 "$SCRIPT_DIR" > /dev/null
    echo "  → Building Linux x64..."
    dotnet publish -c Release -r linux-x64 "$SCRIPT_DIR" > /dev/null

    # ── Stage to Resilio for distribution ────────────────────────────
    mkdir -p "$STAGING_DIR"

    # Remove old hardlinks (hardlinks can't be updated in place)
    rm -f "$STAGING_DIR/rush.exe" "$STAGING_DIR/rush_x64.exe" \
          "$STAGING_DIR/rush-lang-spec.yaml" "$STAGING_DIR/user-manual.md"

    # Windows ARM64 → rush.exe
    ln "$SCRIPT_DIR/bin/Release/net10.0/win-arm64/publish/rush.exe" "$STAGING_DIR/rush.exe"
    echo "  → $STAGING_DIR/rush.exe (win-arm64)"

    # Windows x64 → rush_x64.exe
    ln "$SCRIPT_DIR/bin/Release/net10.0/win-x64/publish/rush.exe" "$STAGING_DIR/rush_x64.exe"
    echo "  → $STAGING_DIR/rush_x64.exe (win-x64)"

    # Linux x64
    rm -f "$STAGING_DIR/rush-linux-x64"
    ln "$SCRIPT_DIR/bin/Release/net10.0/linux-x64/publish/rush" "$STAGING_DIR/rush-linux-x64"
    echo "  → $STAGING_DIR/rush-linux-x64 (linux-x64)"

    # Docs
    ln "$SCRIPT_DIR/docs/rush-lang-spec.yaml" "$STAGING_DIR/rush-lang-spec.yaml"
    ln "$SCRIPT_DIR/docs/user-manual.md" "$STAGING_DIR/user-manual.md"
    echo "  → $STAGING_DIR/rush-lang-spec.yaml"
    echo "  → $STAGING_DIR/user-manual.md"

    echo ""
    echo "Resilio staging: $STAGING_DIR/"
    ls -lh "$STAGING_DIR/"
fi

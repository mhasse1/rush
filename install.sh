#!/bin/bash
set -euo pipefail

INSTALL_DIR="/usr/local/lib/rush"
BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net8.0/osx-arm64/publish"

# Build release binary
echo "Building release binary..."
export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
dotnet publish -c Release -r osx-arm64 "$SCRIPT_DIR"

VERSION=$("$PUBLISH_DIR/rush" --version 2>/dev/null || echo "unknown")
echo "Installing $VERSION..."

echo "  → $INSTALL_DIR/"
sudo mkdir -p "$INSTALL_DIR"
sudo rsync -a --delete "$PUBLISH_DIR/" "$INSTALL_DIR/"
sudo chmod +x "$INSTALL_DIR/rush"

echo "  → $BIN_LINK (symlink)"
sudo ln -sf "$INSTALL_DIR/rush" "$BIN_LINK"

if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
    echo "  → $SHELLS_FILE (registering as valid shell)"
    echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
else
    echo "  → $SHELLS_FILE (already registered)"
fi

echo ""
echo "Installed: $(rush --version)"
echo ""
echo "To set as default shell:"
echo "  chsh -s $BIN_LINK"

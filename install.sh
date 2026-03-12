#!/bin/bash
set -euo pipefail

INSTALL_DIR="/usr/local/lib/rush"
BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net8.0/osx-arm64/publish"

# ── Dev mode: symlink directly to publish dir ────────────────────────
# During active development, skip the copy step. Every `dotnet publish`
# instantly updates the installed binary.
if [[ "${1:-}" == "--dev" ]]; then
    echo "Building release binary..."
    export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
    export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
    dotnet publish -c Release -r osx-arm64 "$SCRIPT_DIR"

    VERSION=$("$PUBLISH_DIR/rush" --version 2>/dev/null || echo "unknown")
    echo ""
    echo "Dev install: $VERSION"

    # Remove old install dir if it exists (was a copy)
    if [[ -d "$INSTALL_DIR" ]]; then
        echo "  → removing $INSTALL_DIR/ (was a copy, switching to symlink)"
        sudo rm -rf "$INSTALL_DIR"
    fi

    echo "  → $BIN_LINK → $PUBLISH_DIR/rush"
    sudo ln -sf "$PUBLISH_DIR/rush" "$BIN_LINK"

    if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
        echo "  → $SHELLS_FILE (registering as valid shell)"
        echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
    fi

    echo ""
    echo "Installed: $(rush --version)"
    echo "  Binary:  $PUBLISH_DIR/rush"
    echo "  Update:  dotnet publish -c Release -r osx-arm64"
    exit 0
fi

# ── Fast path: symlink already points at publish dir ─────────────────
# If a previous --dev install created a direct symlink, just rebuild.
if [[ -L "$BIN_LINK" ]] && [[ "$(readlink "$BIN_LINK")" == "$PUBLISH_DIR/rush" ]]; then
    echo "Building release binary..."
    export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
    export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
    dotnet publish -c Release -r osx-arm64 "$SCRIPT_DIR"

    VERSION=$("$PUBLISH_DIR/rush" --version 2>/dev/null || echo "unknown")
    echo ""
    echo "Installed: $VERSION  (dev symlink)"
    exit 0
fi

# ── Standard install: copy to /usr/local/lib/rush ────────────────────
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

# ── Windows builds: cross-compile and stage for transfer ──────────
WIN_STAGING="$HOME/Resilion/cio/tmp"
mkdir -p "$WIN_STAGING"

echo "  → Building Windows ARM64..."
dotnet publish -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true "$SCRIPT_DIR" > /dev/null
cp "$SCRIPT_DIR/bin/Release/net8.0/win-arm64/publish/rush.exe" "$WIN_STAGING/rush.exe"
echo "  → $WIN_STAGING/rush.exe"

echo "  → Copying docs..."
cp "$SCRIPT_DIR/docs/rush-lang-spec.yaml" "$WIN_STAGING/"
cp "$SCRIPT_DIR/docs/user-manual.md" "$WIN_STAGING/"
echo "  → $WIN_STAGING/rush-lang-spec.yaml"
echo "  → $WIN_STAGING/user-manual.md"

echo ""
echo "Installed: $(rush --version)"
echo ""
echo "To set as default shell:"
echo "  chsh -s $BIN_LINK"

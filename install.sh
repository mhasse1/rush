#!/bin/bash
set -euo pipefail

BIN_LINK="/usr/local/bin/rush"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net10.0/osx-arm64/publish"
SDK_DIR="/opt/homebrew/lib/rush-sdk"

# Detect .NET 10 from Homebrew
if [[ -d "/opt/homebrew/opt/dotnet/libexec" ]]; then
    DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
elif [[ -d "/usr/local/share/dotnet" ]]; then
    DOTNET_ROOT="/usr/local/share/dotnet"
elif [[ -n "${DOTNET_ROOT:-}" ]]; then
    : # use existing
else
    echo "Error: .NET 10 not found. Install with: brew install dotnet"
    exit 1
fi
export DOTNET_ROOT
export PATH="/opt/homebrew/opt/dotnet/bin:$PATH"

# ── Build (always as current user) ──────────────────────────────────
echo "Building release binary..."
dotnet publish -c Release -r osx-arm64 "$SCRIPT_DIR"
echo ""

# ── Install SDK DLLs ────────────────────────────────────────────────
echo "  → Installing SDK to $SDK_DIR/"
sudo mkdir -p "$SDK_DIR"
# Copy all DLLs and locale directories, exclude rush-specific files
for f in "$PUBLISH_DIR"/*; do
    base=$(basename "$f")
    case "$base" in
        rush|rush.dll|rush.pdb|rush.runtimeconfig.json|rush.deps.json) continue ;;
        *) sudo cp -r "$f" "$SDK_DIR/" ;;
    esac
done

# ── Install Rush wrapper ─────────────────────────────────────────────
# Wrapper sets DOTNET_ROOT so the framework-dependent binary finds .NET.
# exec replaces the wrapper process — no overhead after startup.
RUSH_BIN="$PUBLISH_DIR/rush"
WRAPPER="#!/bin/sh
export DOTNET_ROOT=\"$DOTNET_ROOT\"
exec \"$RUSH_BIN\" \"\$@\"
"

OLD_INSTALL="/usr/local/lib/rush"
if [[ -d "$OLD_INSTALL" ]]; then
    echo "  → removing $OLD_INSTALL/ (switching to wrapper)"
    sudo rm -rf "$OLD_INSTALL"
fi

echo "$WRAPPER" | sudo tee "$BIN_LINK" > /dev/null
sudo chmod +x "$BIN_LINK"
echo "  → $BIN_LINK (wrapper → $RUSH_BIN)"

# ── Register as login shell (sudo only if needed) ───────────────────
if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
    echo "  → $SHELLS_FILE (registering as valid shell)"
    echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
fi

VERSION=$(DOTNET_ROOT="$DOTNET_ROOT" "$RUSH_BIN" --version 2>/dev/null || echo "unknown")
echo ""
echo "Installed: $VERSION"
echo "  Rush:    $RUSH_BIN ($(du -h "$RUSH_BIN" | cut -f1 | xargs))"
echo "  SDK:     $SDK_DIR/ ($(du -sh "$SDK_DIR" | cut -f1 | xargs))"
echo "  Runtime: $DOTNET_ROOT"

# ── Windows cross-compile (only with --full) ─────────────────────────
if [[ "${1:-}" == "--full" ]]; then
    WIN_STAGING="${WIN_STAGING:-$SCRIPT_DIR/dist}"
    mkdir -p "$WIN_STAGING"

    echo ""
    echo "  → Building Windows ARM64..."
    dotnet publish -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true "$SCRIPT_DIR" > /dev/null
    cp "$SCRIPT_DIR/bin/Release/net10.0/win-arm64/publish/rush.exe" "$WIN_STAGING/rush-arm64.exe"
    echo "  → $WIN_STAGING/rush-arm64.exe"

    echo "  → Building Windows x64..."
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true "$SCRIPT_DIR" > /dev/null
    cp "$SCRIPT_DIR/bin/Release/net10.0/win-x64/publish/rush.exe" "$WIN_STAGING/rush-x64.exe"
    echo "  → $WIN_STAGING/rush-x64.exe"

    echo "  → Building Linux x64..."
    dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true "$SCRIPT_DIR" > /dev/null
    cp "$SCRIPT_DIR/bin/Release/net10.0/linux-x64/publish/rush" "$WIN_STAGING/rush-linux-x64"
    echo "  → $WIN_STAGING/rush-linux-x64"
fi

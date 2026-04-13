#!/bin/bash
set -euo pipefail

BIN_NAME="rush"
BIN_LINK="/usr/local/bin/$BIN_NAME"
SHELLS_FILE="/etc/shells"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "Rush — building release binary..."
cargo build --release --manifest-path "$SCRIPT_DIR/Cargo.toml" -q 2>&1

BUILT="$SCRIPT_DIR/target/release/rush-cli"
if [[ ! -f "$BUILT" ]]; then
    echo "Build failed: $BUILT not found"
    exit 1
fi

echo "Installing to $BIN_LINK..."
# Stage into a sibling path then rename, so we can replace a binary
# that is currently executing (Linux "text file busy"). rename() just
# swaps which inode the name points at; the running process keeps its
# old inode alive until it exits.
TMP_LINK="${BIN_LINK}.new.$$"
sudo cp "$BUILT" "$TMP_LINK"
sudo chmod +x "$TMP_LINK"
sudo mv -f "$TMP_LINK" "$BIN_LINK"

# macOS: strip quarantine + ad-hoc sign
if [[ "$(uname)" == "Darwin" ]]; then
    sudo xattr -cr "$BIN_LINK" 2>/dev/null || true
    sudo codesign --sign - --force "$BIN_LINK" 2>/dev/null || true
fi

# Register as login shell
if ! grep -q "$BIN_LINK" "$SHELLS_FILE" 2>/dev/null; then
    echo "Registering in $SHELLS_FILE..."
    echo "$BIN_LINK" | sudo tee -a "$SHELLS_FILE" > /dev/null
fi

VERSION=$("$BIN_LINK" --version 2>/dev/null || echo "unknown")
echo "Installed: $VERSION"

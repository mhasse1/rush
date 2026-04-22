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

# ── Optional: build + install rush-ps-bridge ─────────────────────────
# Skipped unless the .NET 10 SDK is on PATH or PS_BRIDGE=1 is set.
# PS_BRIDGE=0 forces skip even when the SDK is present.

BRIDGE_BIN_LINK="/usr/local/bin/rush-ps-bridge"
BRIDGE_DIR="$SCRIPT_DIR/dotnet/rush-ps-bridge"

build_bridge=0
if [[ "${PS_BRIDGE:-}" == "1" ]]; then
    build_bridge=1
elif [[ "${PS_BRIDGE:-}" != "0" ]] && command -v dotnet >/dev/null 2>&1; then
    # Opt-in on SDK 10+ availability; silent no-op otherwise.
    if dotnet --list-sdks 2>/dev/null | grep -qE "^10\." ; then
        build_bridge=1
    fi
fi

if [[ "$build_bridge" == "1" && -d "$BRIDGE_DIR" ]]; then
    RID=""
    case "$(uname -s)" in
        Linux)  RID="linux-$(uname -m | sed 's/x86_64/x64/;s/aarch64/arm64/')" ;;
        Darwin) RID="osx-$(uname -m | sed 's/x86_64/x64/;s/arm64/arm64/')" ;;
    esac

    if [[ -n "$RID" ]]; then
        echo "rush-ps-bridge — publishing single-file binary ($RID)..."
        (
            cd "$BRIDGE_DIR"
            dotnet publish -c Release -r "$RID" -p:PublishSingleFile=true \
                --self-contained -v quiet 2>&1 | tail -5
        )

        BRIDGE_BUILT="$BRIDGE_DIR/bin/Release/net10.0/$RID/publish/rush-ps-bridge"
        if [[ -f "$BRIDGE_BUILT" ]]; then
            echo "Installing bridge to $BRIDGE_BIN_LINK..."
            TMP_LINK="${BRIDGE_BIN_LINK}.new.$$"
            sudo cp "$BRIDGE_BUILT" "$TMP_LINK"
            sudo chmod +x "$TMP_LINK"
            sudo mv -f "$TMP_LINK" "$BRIDGE_BIN_LINK"
            BRIDGE_VERSION=$("$BRIDGE_BIN_LINK" --version 2>/dev/null || echo "unknown")
            echo "Installed: $BRIDGE_VERSION"
        else
            echo "rush-ps-bridge publish did not produce $BRIDGE_BUILT (skipped install)"
        fi
    else
        echo "rush-ps-bridge: unsupported OS for auto-publish ($(uname -s)); skipping"
    fi
fi

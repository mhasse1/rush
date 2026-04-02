#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LINK=false
[[ "${1:-}" == "--link" ]] && LINK=true

TARGET="$HOME/.vscode/extensions/rush-shell.rush-lang-0.1.0"

if $LINK; then
    # For dev mode, symlink the entire directory
    rm -rf "$TARGET"
    mkdir -p "$(dirname "$TARGET")"
    ln -sf "$SCRIPT_DIR" "$TARGET"
    echo "Rush extension linked → $TARGET"
else
    # Remove old symlink install if present (avoids cp same-file error)
    [[ -L "$TARGET" ]] && rm -f "$TARGET"
    mkdir -p "$TARGET/syntaxes"
    cp "$SCRIPT_DIR/package.json"                  "$TARGET/"
    cp "$SCRIPT_DIR/language-configuration.json"    "$TARGET/"
    cp "$SCRIPT_DIR/syntaxes/rush.tmLanguage.json"  "$TARGET/syntaxes/"
    echo "Rush extension copied → $TARGET/"
fi

echo ""
echo "Restart VS Code to activate."

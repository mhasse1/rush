#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LINK=false
[[ "${1:-}" == "--link" ]] && LINK=true

# Detect platform
if [[ "$(uname)" == "Darwin" ]]; then
    TARGET="$HOME/Library/Application Support/Sublime Text/Packages/Rush"
else
    TARGET="$HOME/.config/sublime-text/Packages/Rush"
fi

mkdir -p "$TARGET"

if $LINK; then
    ln -sf "$SCRIPT_DIR/Rush.sublime-syntax" "$TARGET/Rush.sublime-syntax"
    echo "Rush syntax linked → $TARGET/"
else
    cp "$SCRIPT_DIR/Rush.sublime-syntax" "$TARGET/Rush.sublime-syntax"
    echo "Rush syntax copied → $TARGET/"
fi

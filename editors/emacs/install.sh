#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LINK=false
[[ "${1:-}" == "--link" ]] && LINK=true

TARGET="$HOME/.emacs.d/lisp"
mkdir -p "$TARGET"

if $LINK; then
    ln -sf "$SCRIPT_DIR/rush-mode.el" "$TARGET/rush-mode.el"
    echo "Rush mode linked → $TARGET/rush-mode.el"
else
    rm -f "$TARGET/rush-mode.el"
    cp "$SCRIPT_DIR/rush-mode.el" "$TARGET/rush-mode.el"
    echo "Rush mode copied → $TARGET/rush-mode.el"
fi

echo ""
echo "Add to your Emacs config:"
echo ""
echo "  (add-to-list 'load-path \"$TARGET\")"
echo "  (require 'rush-mode)"

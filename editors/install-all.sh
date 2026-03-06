#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
FLAG="${1:-}"
installed=0

echo "Installing Rush editor plugins..."
echo ""

# Vim / Neovim
if command -v vim &>/dev/null || command -v nvim &>/dev/null; then
    bash "$SCRIPT_DIR/vim/install.sh" $FLAG
    installed=$((installed + 1))
    echo ""
fi

# Emacs
if command -v emacs &>/dev/null; then
    bash "$SCRIPT_DIR/emacs/install.sh" $FLAG
    installed=$((installed + 1))
    echo ""
fi

# VS Code
if command -v code &>/dev/null; then
    bash "$SCRIPT_DIR/vscode/install.sh" $FLAG
    installed=$((installed + 1))
    echo ""
fi

# Sublime Text
if command -v subl &>/dev/null; then
    bash "$SCRIPT_DIR/sublime/install.sh" $FLAG
    installed=$((installed + 1))
    echo ""
fi

if [[ $installed -eq 0 ]]; then
    echo "No supported editors found (vim, nvim, emacs, code, subl)."
    echo "Run individual scripts in editors/<editor>/install.sh directly."
    exit 1
fi

echo "Done — $installed editor(s) configured."

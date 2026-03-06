#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LINK=false
[[ "${1:-}" == "--link" ]] && LINK=true

installed=()

install_to() {
    local base="$1"
    mkdir -p "$base/ftdetect" "$base/syntax"

    if $LINK; then
        ln -sf "$SCRIPT_DIR/ftdetect/rush.vim" "$base/ftdetect/rush.vim"
        ln -sf "$SCRIPT_DIR/syntax/rush.vim"   "$base/syntax/rush.vim"
    else
        cp "$SCRIPT_DIR/ftdetect/rush.vim" "$base/ftdetect/rush.vim"
        cp "$SCRIPT_DIR/syntax/rush.vim"   "$base/syntax/rush.vim"
    fi
    installed+=("$base")
}

# Vim
if [[ -d "$HOME/.vim" ]] || command -v vim &>/dev/null; then
    install_to "$HOME/.vim"
fi

# Neovim
if [[ -d "$HOME/.config/nvim" ]] || command -v nvim &>/dev/null; then
    install_to "$HOME/.config/nvim"
fi

if [[ ${#installed[@]} -eq 0 ]]; then
    echo "No Vim or Neovim installation found."
    exit 1
fi

mode="copied"
$LINK && mode="linked"

for dir in "${installed[@]}"; do
    echo "Rush syntax $mode → $dir/"
done

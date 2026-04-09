#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Build Rust rush-cli on multiple hosts
# ═══════════════════════════════════════════════════════════════════════
set -euo pipefail

log() { echo "$(date +%H:%M:%S) $1"; }

# ── macOS (local) ────────────────────────────────────────────────────
build_local() {
    log "Building locally (macOS)..."
    cargo build --release -q
    local version=$(target/release/rush-cli -c 'puts $rush_version' 2>/dev/null || echo "unknown")
    log "  ✓ macOS arm64: $(ls -lh target/release/rush-cli | awk '{print $5}') (v${version})"
}

# ── Linux (trinity) ──────────────────────────────────────────────────
build_trinity() {
    log "Building on trinity (Linux x64)..."
    ssh trinity 'export PATH=$HOME/.cargo/bin:$PATH
cd ~/src/rush
git pull --quiet
cargo build --release -q 2>&1 | tail -1
ls -lh target/release/rush-cli' 2>/dev/null
    log "  ✓ trinity built"
}

# ── Deploy ───────────────────────────────────────────────────────────
deploy_trinity() {
    log "Deploying to trinity..."
    ssh trinity 'sudo cp ~/src/rush/target/release/rush-cli /usr/local/bin/rush-rust && sudo chmod +x /usr/local/bin/rush-rust' 2>/dev/null
    log "  ✓ trinity: installed as rush-rust"
}

deploy_local() {
    log "Installing locally..."
    sudo cp target/release/rush-cli /usr/local/bin/rush-rust
    log "  ✓ local: installed as rush-rust"
}

# ── Main ─────────────────────────────────────────────────────────────
case "${1:-build}" in
    build)
        build_local
        ;;
    trinity)
        build_trinity
        ;;
    deploy)
        build_local
        deploy_local
        build_trinity
        deploy_trinity
        ;;
    *)
        echo "Usage: build-rust.sh [build|trinity|deploy]"
        ;;
esac

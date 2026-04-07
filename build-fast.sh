#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Fast parallel build — all platforms simultaneously
# Wall time = slowest host (~9 min buster), not sum
# ═══════════════════════════════════════════════════════════════════════
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STAGING="$SCRIPT_DIR/dist/native"
mkdir -p "$STAGING"

log() { echo "$(date +%H:%M:%S) $1"; }

# ── macOS (rocinante) ────────────────────────────────────────────────
build_mac() {
    cd "$SCRIPT_DIR"
    export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
    export PATH="/opt/homebrew/opt/dotnet/bin:$PATH"
    git pull --quiet 2>/dev/null || true
    # Non-single-file for local install (avoids signing issues)
    sudo rm -rf /usr/local/lib/rush
    sudo mkdir -p /usr/local/lib/rush
    dotnet publish -c Release -r osx-arm64 -p:SkipCleanCheck=true -o /usr/local/lib/rush > /dev/null 2>&1
    sudo rm -f /usr/local/bin/rush
    sudo ln -sf /usr/local/lib/rush/rush /usr/local/bin/rush
    log "  rocinante: ✓ $(/usr/local/bin/rush --version 2>/dev/null)"
}

# ── Linux (trinity) ──────────────────────────────────────────────────
build_linux() {
    ssh trinity 'export PATH=$HOME/.dotnet:$PATH; cd ~/src/rush; git pull --quiet 2>/dev/null || true
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o /tmp/rush-build-x64 > /dev/null 2>&1 &
dotnet publish -c Release -r linux-arm64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o /tmp/rush-build-arm64 > /dev/null 2>&1 &
wait' 2>/dev/null
    scp -q trinity:/tmp/rush-build-x64/rush "$STAGING/rush-linux-x64"
    scp -q trinity:/tmp/rush-build-arm64/rush "$STAGING/rush-linux-arm64"
    chmod +x "$STAGING/rush-linux-x64" "$STAGING/rush-linux-arm64"
    log "  trinity: ✓ linux-x64 + linux-arm64"
}

# ── Windows (buster) ─────────────────────────────────────────────────
build_win() {
    ssh buster "\$env:PATH = \"\$env:LOCALAPPDATA\\Microsoft\\dotnet;C:\\Program Files\\Git\\cmd;\$env:PATH\"
cd C:\\src\\rush
& 'C:\\Program Files\\Git\\cmd\\git.exe' pull --quiet 2>\$null
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o C:\\temp\\rush-build-x64 2>&1 | Out-Null
dotnet publish -c Release -r win-arm64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o C:\\temp\\rush-build-arm64 2>&1 | Out-Null
" 2>/dev/null
    scp -q buster:C:/temp/rush-build-x64/rush.exe "$STAGING/rush-win-x64.exe"
    scp -q buster:C:/temp/rush-build-arm64/rush.exe "$STAGING/rush-win-arm64.exe"
    log "  buster: ✓ win-x64 + win-arm64"
}

# ── Run all in parallel ──────────────────────────────────────────────
# Pre-auth sudo before backgrounding (can't prompt from background)
sudo true

log "Building on 3 hosts in parallel..."

build_mac &
pid_mac=$!
build_linux &
pid_lin=$!
build_win &
pid_win=$!

failed=0
wait $pid_mac || { log "  rocinante: ✗ FAILED"; failed=$((failed+1)); }
wait $pid_lin || { log "  trinity: ✗ FAILED"; failed=$((failed+1)); }
wait $pid_win || { log "  buster: ✗ FAILED"; failed=$((failed+1)); }

# ── Deploy ────────────────────────────────────────────────────────────
if [[ $failed -eq 0 ]]; then
    log "Deploying..."

    # Trinity
    scp -q "$STAGING/rush-linux-x64" trinity:/tmp/rush-new
    ssh trinity "sudo cp /tmp/rush-new /usr/local/bin/rush && sudo chmod +x /usr/local/bin/rush" 2>/dev/null
    log "  trinity: deployed"

    # OCI
    scp -q "$STAGING/rush-linux-arm64" oci:/tmp/rush-new
    ssh oci "sudo cp /tmp/rush-new /usr/local/bin/rush && sudo chmod +x /usr/local/bin/rush" 2>/dev/null
    log "  oci: deployed"

    # Buster
    ssh buster 'Copy-Item C:\temp\rush-build-x64\rush.exe C:\bin\rush.exe -Force' 2>/dev/null
    log "  buster: deployed"

    # Faust + COI: deploy via Datto (not SSH accessible)
    # Binaries in dist/native/ for manual staging

    log "✓ All done"
else
    log "✗ $failed host(s) failed"
    exit 1
fi

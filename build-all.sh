#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Rush Native Build — build on real hardware, no CI artifacts
#
# Builds in parallel on 3 hosts:
#   rocinante  → osx-arm64
#   trinity    → linux-x64, linux-arm64
#   buster     → win-x64, win-arm64
#
# Usage:
#   ./build-all.sh              # build + stage all platforms
#   ./build-all.sh --deploy     # build + stage + deploy to all hosts
#   ./build-all.sh --test       # build + stage + deploy + test
# ═��═════════════════════════════════════════════════════════════════════

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
STAGING="$SCRIPT_DIR/dist/native"
DEPLOY=false
TEST=false

for arg in "$@"; do
    case "$arg" in
        --deploy) DEPLOY=true ;;
        --test) DEPLOY=true; TEST=true ;;
    esac
done

mkdir -p "$STAGING"

RESULTS_DIR="/tmp/rush-build-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RESULTS_DIR"

log() { echo "$(date +%H:%M:%S) $1"; }

# ── Build functions ───────────────────────────────────────────────────

build_rocinante() {
    local logfile="$RESULTS_DIR/rocinante.log"
    log "rocinante: building osx-arm64..."
    (
        cd "$SCRIPT_DIR"
        export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
        export PATH="/opt/homebrew/opt/dotnet/bin:$PATH"
        git pull --quiet 2>/dev/null || true
        dotnet publish -c Release -r osx-arm64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o "$STAGING" 2>&1
        mv "$STAGING/rush" "$STAGING/rush-osx-arm64"
        chmod +x "$STAGING/rush-osx-arm64"
        echo "OK: $(shasum -a 256 "$STAGING/rush-osx-arm64" | cut -d' ' -f1)"
    ) > "$logfile" 2>&1

    if grep -q "^OK:" "$logfile"; then
        local hash=$(grep "^OK:" "$logfile" | cut -d' ' -f2)
        log "rocinante: ✓ osx-arm64 ($hash)"
    else
        log "rocinante: ✗ FAILED (see $logfile)"
        return 1
    fi
}

build_trinity() {
    local logfile="$RESULTS_DIR/trinity.log"
    log "trinity: building linux-x64 + linux-arm64..."
    (
        ssh trinity 'export PATH=$HOME/.dotnet:$PATH
cd ~/src/rush
git pull --quiet 2>/dev/null || true

echo "=== linux-x64 ==="
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o /tmp/rush-build-x64 2>&1
sha256sum /tmp/rush-build-x64/rush

echo "=== linux-arm64 ==="
dotnet publish -c Release -r linux-arm64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o /tmp/rush-build-arm64 2>&1
sha256sum /tmp/rush-build-arm64/rush
'
        # Download the built binaries
        scp -q trinity:/tmp/rush-build-x64/rush "$STAGING/rush-linux-x64"
        scp -q trinity:/tmp/rush-build-arm64/rush "$STAGING/rush-linux-arm64"
        chmod +x "$STAGING/rush-linux-x64" "$STAGING/rush-linux-arm64"

        echo "OK: linux-x64 $(shasum -a 256 "$STAGING/rush-linux-x64" | cut -d' ' -f1)"
        echo "OK: linux-arm64 $(shasum -a 256 "$STAGING/rush-linux-arm64" | cut -d' ' -f1)"
    ) > "$logfile" 2>&1

    if grep -q "^OK: linux-x64" "$logfile" && grep -q "^OK: linux-arm64" "$logfile"; then
        log "trinity: ✓ linux-x64 + linux-arm64"
    else
        log "trinity: ✗ FAILED (see $logfile)"
        return 1
    fi
}

build_buster() {
    local logfile="$RESULTS_DIR/buster.log"
    log "buster: building win-x64 + win-arm64..."
    (
        ssh buster "\$env:PATH = \"\$env:LOCALAPPDATA\\Microsoft\\dotnet;C:\\Program Files\\Git\\cmd;\$env:PATH\"
cd C:\\src\\rush
& 'C:\\Program Files\\Git\\cmd\\git.exe' pull --quiet 2>\$null

Write-Host '=== win-x64 ==='
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o C:\\temp\\rush-build-x64 2>&1
(Get-FileHash C:\\temp\\rush-build-x64\\rush.exe -Algorithm SHA256).Hash

Write-Host '=== win-arm64 ==='
dotnet publish -c Release -r win-arm64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o C:\\temp\\rush-build-arm64 2>&1
(Get-FileHash C:\\temp\\rush-build-arm64\\rush.exe -Algorithm SHA256).Hash
"
        # Download the built binaries
        scp -q buster:C:/temp/rush-build-x64/rush.exe "$STAGING/rush-win-x64.exe"
        scp -q buster:C:/temp/rush-build-arm64/rush.exe "$STAGING/rush-win-arm64.exe"

        echo "OK: win-x64 $(shasum -a 256 "$STAGING/rush-win-x64.exe" | cut -d' ' -f1)"
        echo "OK: win-arm64 $(shasum -a 256 "$STAGING/rush-win-arm64.exe" | cut -d' ' -f1)"
    ) > "$logfile" 2>&1

    if grep -q "^OK: win-x64" "$logfile" && grep -q "^OK: win-arm64" "$logfile"; then
        log "buster: ✓ win-x64 + win-arm64"
    else
        log "buster: ✗ FAILED (see $logfile)"
        return 1
    fi
}

# ── Build all in parallel ─────────────────────────────────────────────

log "Starting native builds on 3 hosts..."
echo ""

build_rocinante &
pid_mac=$!
build_trinity &
pid_lin=$!
build_buster &
pid_win=$!

failed=0
wait $pid_mac || failed=$((failed + 1))
wait $pid_lin || failed=$((failed + 1))
wait $pid_win || failed=$((failed + 1))

echo ""
log "Build results:"
ls -lh "$STAGING/" 2>/dev/null

# Generate checksums
echo ""
echo "SHA-256 checksums:"
(cd "$STAGING" && shasum -a 256 *)

if [[ $failed -gt 0 ]]; then
    log "✗ $failed host(s) failed"
    echo "Logs: $RESULTS_DIR/"
    exit 1
fi

log "✓ All 5 binaries built"

# ── Deploy ──────────��─────────────────────────────────────────────────

if [[ "$DEPLOY" == true ]]; then
    echo ""
    log "Deploying..."

    # Local (macOS) — rebuild as directory publish (matches install.sh)
    # Single-file binary conflicts with the existing directory layout
    log "  rocinante: installing (local build)..."
    (
        cd "$SCRIPT_DIR"
        export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
        export PATH="/opt/homebrew/opt/dotnet/bin:$PATH"
        sudo rm -rf /usr/local/lib/rush
        sudo mkdir -p /usr/local/lib/rush
        dotnet publish -c Release -r osx-arm64 -p:SkipCleanCheck=true -o /usr/local/lib/rush 2>&1
        sudo rm -f /usr/local/bin/rush
        sudo ln -sf /usr/local/lib/rush/rush /usr/local/bin/rush
    ) > "$RESULTS_DIR/rocinante-deploy.log" 2>&1 && \
        log "  rocinante: installed ($(/usr/local/bin/rush --version 2>/dev/null))" || \
        log "  rocinante: FAILED (see $RESULTS_DIR/rocinante-deploy.log)"

    # Trinity
    scp -q "$STAGING/rush-linux-x64" trinity:/tmp/rush-new && \
        ssh trinity "sudo cp /tmp/rush-new /usr/local/bin/rush && sudo chmod +x /usr/local/bin/rush" 2>/dev/null && \
        log "  trinity: installed ($(ssh trinity '/usr/local/bin/rush --version' 2>/dev/null))" || \
        log "  trinity: FAILED"

    # OCI
    scp -q "$STAGING/rush-linux-arm64" oci:/tmp/rush-new && \
        ssh oci "sudo cp /tmp/rush-new /usr/local/bin/rush && sudo chmod +x /usr/local/bin/rush" 2>/dev/null && \
        log "  oci: installed ($(ssh oci '/usr/local/bin/rush --version' 2>/dev/null))" || \
        log "  oci: FAILED"

    # Buster
    scp -q "$STAGING/rush-win-x64.exe" buster:C:/temp/rush-new.exe && \
        ssh buster 'Copy-Item C:\temp\rush-new.exe C:\bin\rush.exe -Force' 2>/dev/null && \
        log "  buster: installed ($(ssh buster 'C:\bin\rush.exe --version' 2>/dev/null | tr -d '\r'))" || \
        log "  buster: FAILED"

    log "Deploy complete"
fi

# ── Test ──────────────────────────────────────────────────────────────

if [[ "$TEST" == true ]]; then
    echo ""
    log "Running pipeline tests..."
    bash "$SCRIPT_DIR/pipeline.sh" 3-4 2>&1
fi

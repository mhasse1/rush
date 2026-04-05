#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Rush Development Pipeline
# Full cross-platform build, test, and deploy workflow.
#
# Usage:
#   ./pipeline.sh              # full pipeline (all phases)
#   ./pipeline.sh --phase 1-3  # specific phases
#   ./pipeline.sh --skip-ci    # skip CI wait + artifact download
#   ./pipeline.sh --dry-run    # show what would happen
#
# Phases:
#   1. Source sync    — git pull on all platforms
#   2. Build + xUnit  — dotnet build + test (parallel)
#   3. Integration    — portability, LLM, MCP tests (parallel)
#   4. Cross-platform — MCP-SSH gateway tests
#   5. CI artifacts   — wait for CI, download binaries
#   6. Deploy         — install to all hosts + staging
#
# Hosts:
#   rocinante  macOS/arm64   ~/src/rush           local
#   trinity    Linux/x64     ~/src/rush            ssh
#   buster     Windows/x64   C:\src\rush           ssh
#   oci        Linux/arm64   (deploy only)         ssh (humanfirsttalent.com)
# ═══════════════════════════════════════════════════════════════════════

set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO="mhasse1/rush"
RESULTS_DIR="/tmp/rush-pipeline-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RESULTS_DIR"

# ── Config ────────────────────────────────────────────────────────────

LOCAL_SRC="$HOME/src/rush"
LOCAL_DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
LOCAL_DOTNET_PATH="/opt/homebrew/opt/dotnet/bin"

TRINITY_DOTNET='export PATH=$HOME/.dotnet:$PATH'
TRINITY_SRC='cd ~/src/rush'
TRINITY_GIT='git'
TRINITY_RUSH='/usr/local/bin/rush'

BUSTER_DOTNET='$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;C:\Program Files\Git\cmd;$env:PATH"'
BUSTER_SRC='cd C:\src\rush'
BUSTER_GIT='"C:\Program Files\Git\cmd\git.exe"'
BUSTER_RUSH='C:/bin/rush.exe'

STAGING="$SCRIPT_DIR/bin"
COI_RUSH="$HOME/Resilio/coi/_rush"

# ── Args ──────────────────────────────────────────────────────────────

SKIP_CI=false
DRY_RUN=false
PHASE_START=1
PHASE_END=6

for arg in "$@"; do
    case "$arg" in
        --skip-ci) SKIP_CI=true ;;
        --dry-run) DRY_RUN=true ;;
        --phase)   ;; # handled below
        [1-6]-[1-6])
            PHASE_START="${arg%-*}"
            PHASE_END="${arg#*-}"
            ;;
        [1-6])
            PHASE_START="$arg"
            PHASE_END="$arg"
            ;;
    esac
done

# ── Utilities ─────────────────────────────────────────────────────────

PASS=0
FAIL=0
PHASE_FAILED=false

log()  { echo "$(date +%H:%M:%S) $*"; }
pass() { echo "  ✓ $1"; PASS=$((PASS + 1)); }
fail() { echo "  ✗ $1 — $2"; FAIL=$((FAIL + 1)); PHASE_FAILED=true; }
banner() {
    echo ""
    echo "═══════════════════════════════════════════════════════════"
    echo "  Phase $1: $2"
    echo "═══════════════════════════════════════════════════════════"
}

check_host() {
    local host=$1
    ssh -o ConnectTimeout=5 -o BatchMode=yes "$host" 'echo ok' >/dev/null 2>&1
}

should_run() { [[ $1 -ge $PHASE_START && $1 -le $PHASE_END ]]; }

gate() {
    if [[ "$PHASE_FAILED" == true ]]; then
        echo ""
        echo "  ✗ Phase $1 FAILED — stopping pipeline"
        echo ""
        summary
        exit 1
    fi
}

summary() {
    echo ""
    echo "═══════════════════════════════════════════════════════════"
    echo "  Pipeline Results: $PASS passed, $FAIL failed"
    echo "  Logs: $RESULTS_DIR/"
    echo "═══════════════════════════════════════════════════════════"
}

# ── Phase 1: Source Sync ──────────────────────────────────────────────

phase1() {
    banner 1 "Source Sync"

    log "Syncing rocinante..."
    (cd "$LOCAL_SRC" && git pull --quiet) && pass "rocinante: git pull" || fail "rocinante" "git pull failed"

    log "Syncing trinity..."
    if check_host trinity; then
        ssh trinity "$TRINITY_DOTNET; $TRINITY_SRC; git pull --quiet" 2>/dev/null \
            && pass "trinity: git pull" || fail "trinity" "git pull failed"
    else
        fail "trinity" "host unreachable"
    fi

    log "Syncing buster..."
    if check_host buster; then
        ssh buster "$BUSTER_DOTNET; $BUSTER_SRC; & $BUSTER_GIT pull --quiet 2>\$null" 2>/dev/null \
            && pass "buster: git pull" || fail "buster" "git pull failed"
    else
        fail "buster" "host unreachable"
    fi
}

# ── Phase 2: Build + xUnit ───────────────────────────────────────────

phase2_local() {
    local log_file="$RESULTS_DIR/xunit-rocinante.log"
    log "rocinante: building + testing..."
    (
        cd "$LOCAL_SRC"
        export DOTNET_ROOT="$LOCAL_DOTNET_ROOT"
        export PATH="$LOCAL_DOTNET_PATH:$PATH"
        dotnet build --nologo -v quiet 2>&1
        dotnet test Rush.Tests --nologo 2>&1
    ) > "$log_file" 2>&1

    if grep -q "Passed!" "$log_file" && grep -q "Failed:     0" "$log_file"; then
        local count
        count=$(grep -o 'Passed: *[0-9]*' "$log_file" | head -1 | grep -o '[0-9]*$')
        pass "rocinante: $count xUnit tests"
    else
        fail "rocinante" "xUnit failures (see $log_file)"
    fi
}

phase2_trinity() {
    local log_file="$RESULTS_DIR/xunit-trinity.log"
    if ! check_host trinity; then
        fail "trinity" "host unreachable"
        return
    fi
    log "trinity: building + testing..."
    ssh trinity "$TRINITY_DOTNET; $TRINITY_SRC; dotnet build --nologo -v quiet 2>&1; dotnet test Rush.Tests --nologo 2>&1" \
        > "$log_file" 2>&1

    if grep -q "Passed!" "$log_file" && grep -q "Failed:     0" "$log_file"; then
        local count
        count=$(grep -o 'Passed: *[0-9]*' "$log_file" | head -1 | grep -o '[0-9]*$')
        pass "trinity: $count xUnit tests"
    else
        fail "trinity" "xUnit failures (see $log_file)"
    fi
}

phase2_buster() {
    local log_file="$RESULTS_DIR/xunit-buster.log"
    if ! check_host buster; then
        fail "buster" "host unreachable"
        return
    fi
    log "buster: building + testing..."
    ssh buster "$BUSTER_DOTNET; $BUSTER_SRC; dotnet build --nologo -v quiet 2>&1; dotnet test Rush.Tests --nologo 2>&1" \
        > "$log_file" 2>&1

    if grep -q "Passed!" "$log_file" && grep -q "Failed:     0" "$log_file"; then
        local count
        count=$(grep -o 'Passed: *[0-9]*' "$log_file" | head -1 | grep -o '[0-9]*$')
        pass "buster: $count xUnit tests"
    else
        fail "buster" "xUnit failures (see $log_file)"
    fi
}

phase2() {
    banner 2 "Build + xUnit Tests (parallel)"
    phase2_local &
    local pid_local=$!
    phase2_trinity &
    local pid_trinity=$!
    phase2_buster &
    local pid_buster=$!

    wait $pid_local
    wait $pid_trinity
    wait $pid_buster
}

# ── Phase 3: Integration Tests ───────────────────────────────────────

phase3_host() {
    local host="${1:-}" os="${2:-}"
    local log_file="$RESULTS_DIR/integration-$host.log"

    if [[ "$host" != "rocinante" ]] && ! check_host "$host"; then
        fail "$host" "host unreachable"
        return
    fi

    log "$host: integration tests..."

    if [[ "$os" == "linux" ]]; then
        scp -q "$LOCAL_SRC/Rush.Tests/Fixtures/portability-test.rush" \
               "$LOCAL_SRC/Rush.Tests/Fixtures/llm-mode-test.sh" \
               "$LOCAL_SRC/Rush.Tests/Fixtures/mcp-mode-test.sh" \
               "$host:/tmp/" 2>/dev/null
        ssh "$host" "$TRINITY_RUSH /tmp/portability-test.rush 2>&1; echo '---'; bash /tmp/llm-mode-test.sh $TRINITY_RUSH 2>&1; echo '---'; bash /tmp/mcp-mode-test.sh $TRINITY_RUSH 2>&1" \
            > "$log_file" 2>&1
    elif [[ "$os" == "windows" ]]; then
        ssh "$host" 'if (-not (Test-Path C:\temp)) { New-Item -ItemType Directory C:\temp -Force | Out-Null }' 2>/dev/null
        scp -q "$LOCAL_SRC/Rush.Tests/Fixtures/portability-test.rush" \
               "$LOCAL_SRC/Rush.Tests/Fixtures/llm-mode-test.ps1" \
               "$LOCAL_SRC/Rush.Tests/Fixtures/mcp-mode-test.ps1" \
               "$host:C:/temp/" 2>/dev/null
        ssh "$host" "$BUSTER_RUSH C:/temp/portability-test.rush 2>&1; Write-Host '---'; pwsh -ExecutionPolicy Bypass -File C:/temp/llm-mode-test.ps1 -Rush $BUSTER_RUSH 2>&1; Write-Host '---'; pwsh -ExecutionPolicy Bypass -File C:/temp/mcp-mode-test.ps1 -Rush $BUSTER_RUSH 2>&1" \
            > "$log_file" 2>&1
    elif [[ "$os" == "macos" ]]; then
        (
            cd "$LOCAL_SRC"
            rush Rush.Tests/Fixtures/portability-test.rush 2>&1
            echo '---'
            bash Rush.Tests/Fixtures/llm-mode-test.sh 2>&1
            echo '---'
            bash Rush.Tests/Fixtures/mcp-mode-test.sh 2>&1
        ) > "$log_file" 2>&1
    fi

    local pass_count fail_count
    pass_count=$(grep -c "^PASS:" "$log_file" 2>/dev/null || true)
    pass_count=${pass_count:-0}
    fail_count=$(grep -c "^FAIL:" "$log_file" 2>/dev/null || true)
    fail_count=${fail_count:-0}

    if [[ "$fail_count" -eq 0 && "$pass_count" -gt 0 ]]; then
        pass "$host: $pass_count integration assertions"
    else
        fail "$host" "$fail_count integration failures (see $log_file)"
    fi
}

phase3() {
    banner 3 "Integration Tests (parallel)"
    phase3_host rocinante macos &
    local pid_mac=$!
    phase3_host trinity linux &
    local pid_lin=$!
    phase3_host buster windows &
    local pid_win=$!

    wait $pid_mac
    wait $pid_lin
    wait $pid_win
}

# ── Phase 4: Cross-Platform MCP-SSH ──────────────────────────────────

phase4() {
    banner 4 "Cross-Platform MCP-SSH Tests"
    local log_file="$RESULTS_DIR/mcp-ssh.log"

    local hosts=()
    check_host trinity && hosts+=(trinity)
    check_host buster && hosts+=(buster)

    if [[ ${#hosts[@]} -eq 0 ]]; then
        fail "mcp-ssh" "no remote hosts reachable"
        return
    fi

    log "Testing MCP-SSH → ${hosts[*]}..."
    bash "$LOCAL_SRC/Rush.Tests/Fixtures/mcp-ssh-test.sh" "${hosts[@]}" > "$log_file" 2>&1

    local pass_count fail_count
    pass_count=$(grep -c "^PASS:" "$log_file" 2>/dev/null || true)
    pass_count=${pass_count:-0}
    fail_count=$(grep -c "^FAIL:" "$log_file" 2>/dev/null || true)
    fail_count=${fail_count:-0}

    if [[ "$fail_count" -eq 0 && "$pass_count" -gt 0 ]]; then
        pass "mcp-ssh: $pass_count cross-platform assertions (${hosts[*]})"
    else
        fail "mcp-ssh" "$fail_count failures (see $log_file)"
    fi
}

# ── Phase 5: CI Artifacts ────────────────────────────────────────────

phase5() {
    banner 5 "CI Artifacts"

    if [[ "$SKIP_CI" == true ]]; then
        log "Skipping CI (--skip-ci)"
        return
    fi

    # Get latest commit
    local head_sha
    head_sha=$(cd "$LOCAL_SRC" && git rev-parse --short HEAD)
    log "Checking CI for $head_sha..."

    # Find the run for this commit
    local run_id status
    for i in $(seq 1 30); do
        run_id=$(gh run list -R "$REPO" --branch main --json databaseId,headSha,status,conclusion \
            --jq "[.[] | select(.headSha | startswith(\"$(cd "$LOCAL_SRC" && git rev-parse HEAD | cut -c1-7)\"))][0].databaseId" 2>/dev/null)

        if [[ -z "$run_id" || "$run_id" == "null" ]]; then
            log "  Waiting for CI to start... ($i/30)"
            sleep 10
            continue
        fi

        status=$(gh run view "$run_id" -R "$REPO" --json status,conclusion --jq '.status + "/" + .conclusion' 2>/dev/null)
        case "$status" in
            completed/success)
                pass "CI green ($head_sha)"
                break
                ;;
            completed/failure)
                fail "CI" "failed ($head_sha) — check GitHub Actions"
                return
                ;;
            *)
                log "  CI status: $status ($i/30)"
                sleep 20
                ;;
        esac
    done

    if [[ -z "$run_id" || "$run_id" == "null" ]]; then
        fail "CI" "no run found for $head_sha after 5 minutes"
        return
    fi

    # Download artifacts
    log "Downloading artifacts..."
    mkdir -p "$STAGING"
    local downloaded=0
    for artifact in rush-linux-x64 rush-linux-arm64 rush-osx-arm64 rush-win-x64 rush-win-arm64; do
        local tmpdir
        tmpdir=$(mktemp -d)
        if gh run download "$run_id" -R "$REPO" -n "$artifact" -D "$tmpdir" 2>/dev/null; then
            case "$artifact" in
                rush-linux-x64)    mv "$tmpdir/rush" "$STAGING/rush-linux-x64"; chmod +x "$STAGING/rush-linux-x64" ;;
                rush-linux-arm64)  mv "$tmpdir/rush" "$STAGING/rush-linux-arm64"; chmod +x "$STAGING/rush-linux-arm64" ;;
                rush-osx-arm64)    mv "$tmpdir/rush" "$STAGING/rush-osx-arm64"; chmod +x "$STAGING/rush-osx-arm64" ;;
                rush-win-x64)      mv "$tmpdir/rush.exe" "$STAGING/rush-win-x64.exe" ;;
                rush-win-arm64)    mv "$tmpdir/rush.exe" "$STAGING/rush-win-arm64.exe" ;;
            esac
            downloaded=$((downloaded + 1))
        fi
        rm -rf "$tmpdir"
    done

    if [[ $downloaded -eq 5 ]]; then
        pass "Downloaded $downloaded CI artifacts"
    else
        fail "CI artifacts" "only $downloaded/5 downloaded"
    fi
}

# ── Phase 6: Deploy ──────────────────────────────────────────────────

phase6() {
    banner 6 "Deploy"

    if [[ ! -d "$STAGING" ]] || [[ -z "$(ls "$STAGING" 2>/dev/null)" ]]; then
        fail "deploy" "no binaries in $STAGING — run phase 5 first"
        return
    fi

    # Local install
    if [[ -f "$STAGING/rush-osx-arm64" ]]; then
        log "Installing locally..."
        sudo cp "$STAGING/rush-osx-arm64" /usr/local/bin/rush 2>/dev/null \
            && pass "rocinante: installed" || fail "rocinante" "sudo cp failed (run manually)"
    fi

    # Trinity
    if check_host trinity && [[ -f "$STAGING/rush-linux-x64" ]]; then
        log "Deploying to trinity..."
        scp -q "$STAGING/rush-linux-x64" trinity:/tmp/rush-new
        ssh trinity "sudo cp /tmp/rush-new /usr/local/bin/rush && sudo chmod +x /usr/local/bin/rush" 2>/dev/null \
            && pass "trinity: deployed ($(ssh trinity '/usr/local/bin/rush --version' 2>/dev/null))" \
            || fail "trinity" "install failed"
    fi

    # Buster
    if check_host buster && [[ -f "$STAGING/rush-win-x64.exe" ]]; then
        log "Deploying to buster..."
        ssh buster 'if (-not (Test-Path C:\temp)) { New-Item -ItemType Directory C:\temp -Force | Out-Null }' 2>/dev/null
        scp -q "$STAGING/rush-win-x64.exe" buster:C:/temp/rush-new.exe
        ssh buster 'if (-not (Test-Path C:\bin)) { New-Item -ItemType Directory C:\bin -Force | Out-Null }; Copy-Item C:\temp\rush-new.exe C:\bin\rush.exe -Force' 2>/dev/null \
            && pass "buster: deployed ($(ssh buster 'C:\bin\rush.exe --version' 2>/dev/null | tr -d '\r'))" \
            || fail "buster" "install failed"
    fi

    # OCI (Oracle Cloud — Linux ARM64)
    if check_host oci && [[ -f "$STAGING/rush-linux-arm64" ]]; then
        log "Deploying to oci (prod01)..."
        scp -q "$STAGING/rush-linux-arm64" oci:/tmp/rush-new
        ssh oci "sudo cp /tmp/rush-new /usr/local/bin/rush && sudo chmod +x /usr/local/bin/rush" 2>/dev/null \
            && pass "oci: deployed ($(ssh oci '/usr/local/bin/rush --version' 2>/dev/null))" \
            || fail "oci" "install failed"
    fi

    # COI Resilio staging
    if [[ -d "$COI_RUSH" ]]; then
        log "Updating COI Resilio staging..."
        cp "$STAGING/rush-linux-x64" "$COI_RUSH/" 2>/dev/null
        cp "$STAGING/rush-linux-arm64" "$COI_RUSH/" 2>/dev/null
        cp "$STAGING/rush-osx-arm64" "$COI_RUSH/rush_arm64" 2>/dev/null
        cp "$STAGING/rush-win-x64.exe" "$COI_RUSH/rush_x64.exe" "$COI_RUSH/rush.exe" 2>/dev/null
        cp "$STAGING/rush-win-arm64.exe" "$COI_RUSH/rush_arm64.exe" 2>/dev/null
        pass "COI staging updated"
    fi

    # Docs
    log "Syncing docs..."
    cp "$LOCAL_SRC/docs/rush-lang-spec.yaml" "$SCRIPT_DIR/docs/" 2>/dev/null
    cp "$LOCAL_SRC/docs/user-manual.md" "$SCRIPT_DIR/docs/" 2>/dev/null
    cp "$LOCAL_SRC/docs/rush-help.yaml" "$SCRIPT_DIR/docs/" 2>/dev/null

    # Test scripts
    cp "$LOCAL_SRC/Rush.Tests/Fixtures/portability-test.rush" "$SCRIPT_DIR/scripts/" 2>/dev/null
    cp "$LOCAL_SRC/Rush.Tests/Fixtures/llm-mode-test.sh" "$SCRIPT_DIR/scripts/" 2>/dev/null
    cp "$LOCAL_SRC/Rush.Tests/Fixtures/mcp-mode-test.sh" "$SCRIPT_DIR/scripts/" 2>/dev/null
    cp "$LOCAL_SRC/Rush.Tests/Fixtures/llm-mode-test.ps1" "$SCRIPT_DIR/scripts/" 2>/dev/null
    cp "$LOCAL_SRC/Rush.Tests/Fixtures/mcp-mode-test.ps1" "$SCRIPT_DIR/scripts/" 2>/dev/null
    cp "$LOCAL_SRC/Rush.Tests/Fixtures/mcp-ssh-test.sh" "$SCRIPT_DIR/scripts/" 2>/dev/null
    pass "Docs + scripts synced"
}

# ── Main ──────────────────────────────────────────────────────────────

echo "# Rush Development Pipeline"
echo "# $(date '+%Y-%m-%d %H:%M:%S')"
echo "# Phases: $PHASE_START-$PHASE_END"
echo "# Results: $RESULTS_DIR/"

if [[ "$DRY_RUN" == true ]]; then
    echo ""
    echo "DRY RUN — would execute phases $PHASE_START through $PHASE_END"
    echo "  Hosts: rocinante (local), trinity (ssh), buster (ssh)"
    exit 0
fi

should_run 1 && { phase1; gate 1; }
should_run 2 && { phase2; gate 2; }
should_run 3 && { phase3; gate 3; }
should_run 4 && { phase4; gate 4; }
should_run 5 && { phase5; gate 5; }
should_run 6 && { phase6; }

summary
[[ $FAIL -eq 0 ]]

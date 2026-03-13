#!/bin/bash
# UNC SSH test suite — tests //ssh:host/path file operations
# Usage: ./tests/unc-ssh-test.sh [host]
# Requires: ssh access to host, rush installed locally and on remote
set -uo pipefail

HOST="${1:-trinity}"
SSH_OPTS="-o LogLevel=ERROR -o BatchMode=yes -o ConnectTimeout=10"
SCRIPT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
RUSH_BIN="$SCRIPT_DIR/bin/Debug/net8.0/osx-arm64/rush"
if [[ ! -f "$RUSH_BIN" ]]; then
    RUSH_BIN="rush"
fi
PASS=0
FAIL=0
ERRORS=()

# ── Helpers ──────────────────────────────────────────

pass() {
    ((PASS++))
    printf "  \033[32m✓\033[0m %s\n" "$1"
}

fail() {
    ((FAIL++))
    ERRORS+=("$1: $2")
    printf "  \033[31m✗\033[0m %s\n" "$1"
    printf "    %s\n" "$2"
}

# ── Detect Remote OS ─────────────────────────────────

REMOTE_OS=$(ssh $SSH_OPTS "$HOST" "uname -s" 2>/dev/null | tr -d '\r')
# If uname fails (Windows PS 5.1), REMOTE_OS will be empty → falls through to Windows
if [[ -z "$REMOTE_OS" ]]; then
    REMOTE_OS="Windows"
fi
if [[ "$REMOTE_OS" == *"Linux"* ]] || [[ "$REMOTE_OS" == *"Darwin"* ]]; then
    IS_WINDOWS=false
    TEST_DIR="/tmp/rush-unc-test"
    TEST_FILE="$TEST_DIR/unc-test.txt"
    TEST_FILE2="$TEST_DIR/unc-test2.txt"
    TEST_SUBDIR="$TEST_DIR/unc-subdir"
else
    IS_WINDOWS=true
    TEST_DIR='C:\rush-unc-test'
    TEST_FILE='C:\rush-unc-test\unc-test.txt'
    TEST_FILE2='C:\rush-unc-test\unc-test2.txt'
    TEST_SUBDIR='C:\rush-unc-test\unc-subdir'
fi

# Convert paths to UNC format (forward slashes, ensure leading /)
unc_path() {
    local p
    p=$(echo "$1" | tr '\\' '/')
    # Ensure leading / (Windows paths like C:/... need it for //ssh:host/C:/...)
    if [[ "$p" != /* ]]; then
        p="/$p"
    fi
    echo "$p"
}

TEST_DIR_UNC=$(unc_path "$TEST_DIR")
TEST_FILE_UNC=$(unc_path "$TEST_FILE")
TEST_FILE2_UNC=$(unc_path "$TEST_FILE2")
TEST_SUBDIR_UNC=$(unc_path "$TEST_SUBDIR")

# Forward-slash paths for direct SSH verify calls (no leading / prefix)
fwd_path() { echo "$1" | tr '\\' '/'; }
TEST_DIR_FWD=$(fwd_path "$TEST_DIR")
TEST_FILE2_FWD=$(fwd_path "$TEST_FILE2")

# ── Setup ────────────────────────────────────────────

echo "Setting up test fixtures on $HOST ($REMOTE_OS)..."
# Use rush on remote for mkdir (works cross-platform)
ssh $SSH_OPTS "$HOST" "rush -c \"mkdir $TEST_DIR_FWD\"" 2>/dev/null || true
# Deploy test file via scp
LOCAL_SETUP=$(mktemp)
printf 'unc-test-content-42' > "$LOCAL_SETUP"
scp $SSH_OPTS "$LOCAL_SETUP" "$HOST:$TEST_DIR_FWD/unc-test.txt" 2>/dev/null \
    || echo "  Warning: could not deploy test file"
rm -f "$LOCAL_SETUP"

# ── Header ───────────────────────────────────────────

echo ""
echo "═══════════════════════════════════════════"
echo " Rush UNC SSH Test Suite"
echo " Host: $HOST ($REMOTE_OS)"
echo " Rush: $RUSH_BIN"
echo "═══════════════════════════════════════════"
echo ""

# ── Test: ls ─────────────────────────────────────────
echo "── ls ──"

OUTPUT=$("$RUSH_BIN" -c "ls //ssh:$HOST$TEST_DIR_UNC" 2>&1)
RC=$?
if [[ $RC -eq 0 ]] && echo "$OUTPUT" | grep -qF "unc-test"; then
    pass "ls //ssh:host/dir"
else
    fail "ls //ssh:host/dir" "exit=$RC output='$OUTPUT'"
fi

OUTPUT=$("$RUSH_BIN" -c "ls //ssh:$HOST/no-such-dir-xyz" 2>&1)
RC=$?
if [[ $RC -ne 0 ]]; then
    pass "ls missing dir: error exit"
else
    fail "ls missing dir: error exit" "expected non-zero exit, got $RC"
fi

# ── Test: cat ────────────────────────────────────────
echo ""
echo "── cat ──"

OUTPUT=$("$RUSH_BIN" -c "cat //ssh:$HOST$TEST_FILE_UNC" 2>&1)
RC=$?
if [[ $RC -eq 0 ]] && echo "$OUTPUT" | grep -qF "unc-test-content-42"; then
    pass "cat //ssh:host/file"
else
    fail "cat //ssh:host/file" "exit=$RC output='$OUTPUT'"
fi

OUTPUT=$("$RUSH_BIN" -c "cat //ssh:$HOST/no-such-file-xyz.txt" 2>&1)
RC=$?
if [[ $RC -ne 0 ]]; then
    pass "cat missing file: error exit"
else
    fail "cat missing file: error exit" "expected non-zero exit, got $RC"
fi

# ── Test: cp (remote → local) ────────────────────────
echo ""
echo "── cp ──"

LOCAL_TMP=$(mktemp -d)
"$RUSH_BIN" -c "cp //ssh:$HOST$TEST_FILE_UNC $LOCAL_TMP/downloaded.txt" 2>&1
RC=$?
if [[ $RC -eq 0 ]] && [[ -f "$LOCAL_TMP/downloaded.txt" ]]; then
    CONTENT=$(cat "$LOCAL_TMP/downloaded.txt")
    if echo "$CONTENT" | grep -qF "unc-test-content-42"; then
        pass "cp remote → local"
    else
        fail "cp remote → local" "file exists but content='$CONTENT'"
    fi
else
    fail "cp remote → local" "exit=$RC, file missing"
fi

# ── Test: cp (local → remote) ────────────────────────

LOCAL_UPLOAD="$LOCAL_TMP/upload.txt"
printf 'uploaded-from-local-99' > "$LOCAL_UPLOAD"
"$RUSH_BIN" -c "cp $LOCAL_UPLOAD //ssh:$HOST$TEST_FILE2_UNC" 2>&1
RC=$?
if [[ $RC -eq 0 ]]; then
    # Verify on remote
    REMOTE_CONTENT=$(ssh $SSH_OPTS "$HOST" "rush -c 'cat $TEST_FILE2_FWD'" 2>/dev/null | tr -d '\r')
    if echo "$REMOTE_CONTENT" | grep -qF "uploaded-from-local-99"; then
        pass "cp local → remote"
    else
        fail "cp local → remote" "scp succeeded but remote content='$REMOTE_CONTENT'"
    fi
else
    fail "cp local → remote" "exit=$RC"
fi

# ── Test: rm ─────────────────────────────────────────
echo ""
echo "── rm ──"

"$RUSH_BIN" -c "rm //ssh:$HOST$TEST_FILE2_UNC" 2>&1
RC=$?
if [[ $RC -eq 0 ]]; then
    # Verify gone
    ssh $SSH_OPTS "$HOST" "rush -c 'cat $TEST_FILE2_FWD'" 2>/dev/null
    VERIFY_RC=$?
    if [[ $VERIFY_RC -ne 0 ]]; then
        pass "rm //ssh:host/file"
    else
        fail "rm //ssh:host/file" "rm returned 0 but file still exists"
    fi
else
    fail "rm //ssh:host/file" "exit=$RC"
fi

# ── Test: mkdir ──────────────────────────────────────
echo ""
echo "── mkdir / rmdir ──"

"$RUSH_BIN" -c "mkdir //ssh:$HOST$TEST_SUBDIR_UNC" 2>&1
RC=$?
if [[ $RC -eq 0 ]]; then
    # Verify exists
    ssh $SSH_OPTS "$HOST" "rush -c 'ls $TEST_DIR_FWD'" 2>/dev/null | grep -qF "unc-subdir"
    if [[ $? -eq 0 ]]; then
        pass "mkdir //ssh:host/dir"
    else
        fail "mkdir //ssh:host/dir" "mkdir returned 0 but dir not found in ls"
    fi
else
    fail "mkdir //ssh:host/dir" "exit=$RC"
fi

# ── Test: rmdir ──────────────────────────────────────

"$RUSH_BIN" -c "rmdir //ssh:$HOST$TEST_SUBDIR_UNC" 2>&1
RC=$?
if [[ $RC -eq 0 ]]; then
    # Verify gone
    ssh $SSH_OPTS "$HOST" "rush -c 'ls $TEST_DIR_FWD'" 2>/dev/null | grep -qF "unc-subdir"
    if [[ $? -ne 0 ]]; then
        pass "rmdir //ssh:host/dir"
    else
        fail "rmdir //ssh:host/dir" "rmdir returned 0 but dir still exists"
    fi
else
    fail "rmdir //ssh:host/dir" "exit=$RC"
fi

# ── Test: mv ─────────────────────────────────────────
echo ""
echo "── mv ──"

# Create a file to move (use scp — works cross-platform)
MV_TMP=$(mktemp)
printf 'mv-content-77' > "$MV_TMP"
scp $SSH_OPTS "$MV_TMP" "$HOST:$TEST_DIR_FWD/mv-src.txt" 2>/dev/null
rm -f "$MV_TMP"

"$RUSH_BIN" -c "mv //ssh:$HOST$TEST_DIR_UNC/mv-src.txt //ssh:$HOST$TEST_DIR_UNC/mv-dst.txt" 2>&1
RC=$?
if [[ $RC -eq 0 ]]; then
    # Verify source gone and dest exists
    DST_CONTENT=$(ssh $SSH_OPTS "$HOST" "rush -c 'cat $TEST_DIR_FWD/mv-dst.txt'" 2>/dev/null | tr -d '\r')
    SRC_CHECK=$(ssh $SSH_OPTS "$HOST" "rush -c 'cat $TEST_DIR_FWD/mv-src.txt'" 2>/dev/null)
    SRC_RC=$?
    if echo "$DST_CONTENT" | grep -qF "mv-content-77" && [[ $SRC_RC -ne 0 ]]; then
        pass "mv //ssh:host/a → //ssh:host/b (same host)"
    else
        fail "mv //ssh:host/a → //ssh:host/b" "dst='$DST_CONTENT', src_rc=$SRC_RC"
    fi
else
    fail "mv //ssh:host/a → //ssh:host/b" "exit=$RC"
fi

# ── Test: non-interactive (rush -c) ──────────────────
echo ""
echo "── non-interactive ──"

OUTPUT=$("$RUSH_BIN" -c "cat //ssh:$HOST$TEST_FILE_UNC" 2>&1)
RC=$?
if [[ $RC -eq 0 ]] && echo "$OUTPUT" | grep -qF "unc-test-content-42"; then
    pass "rush -c 'cat //ssh:host/file'"
else
    fail "rush -c 'cat //ssh:host/file'" "exit=$RC output='$OUTPUT'"
fi

# ── Cleanup ──────────────────────────────────────────
rm -rf "$LOCAL_TMP" 2>/dev/null || true

# Clean up remote test dir using rush (cross-platform)
ssh $SSH_OPTS "$HOST" "rush -c \"rm -r $TEST_DIR_FWD\"" 2>/dev/null || true

# ── Summary ──────────────────────────────────────────
echo ""
echo "═══════════════════════════════════════════"
TOTAL=$((PASS + FAIL))
if [[ $FAIL -eq 0 ]]; then
    printf " \033[32mAll %d tests passed\033[0m\n" "$TOTAL"
else
    printf " \033[32m%d passed\033[0m, \033[31m%d failed\033[0m (of %d)\n" "$PASS" "$FAIL" "$TOTAL"
    echo ""
    echo " Failures:"
    for err in "${ERRORS[@]}"; do
        printf "   \033[31m✗\033[0m %s\n" "$err"
    done
fi
echo "═══════════════════════════════════════════"

exit $FAIL

#!/bin/bash
# MCP SSH gateway test suite — tests rush --mcp-ssh against a remote host
# Usage: ./tests/remote-mcp-ssh-test.sh [host]
# Requires: jq, ssh access to host, rush installed locally and on remote
set -uo pipefail

HOST="${1:?Usage: $0 <hostname>}"
SSH_OPTS="-o LogLevel=ERROR"
SCRIPT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
RUSH_BIN="$SCRIPT_DIR/bin/Debug/net8.0/osx-arm64/rush"
if [[ ! -f "$RUSH_BIN" ]]; then
    # Fall back to installed rush
    RUSH_BIN="rush"
fi
PASS=0
FAIL=0
ERRORS=()

# ── Helpers ──────────────────────────────────────────

assert_field() {
    local desc="$1" response="$2" jq_expr="$3" expected="$4"
    local actual
    actual=$(echo "$response" | jq -r "$jq_expr" 2>/dev/null)
    if [[ "$actual" == "$expected" ]]; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: expected '$expected', got '$actual'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
        printf "    expected: %s\n" "$expected"
        printf "    got:      %s\n" "$actual"
    fi
}

assert_contains() {
    local desc="$1" response="$2" jq_expr="$3" substr="$4"
    local actual
    actual=$(echo "$response" | jq -r "$jq_expr" 2>/dev/null)
    if echo "$actual" | grep -qiF "$substr"; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: expected to contain '$substr', got '$actual'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
        printf "    expected to contain: %s\n" "$substr"
        printf "    got: %s\n" "$actual"
    fi
}

# Extract a field from the nested tool result JSON
# Tool results: .result.content[0].text = JSON string → parse inner JSON → extract field
tool_field() {
    local response="$1" inner_jq="$2"
    echo "$response" | jq -r '.result.content[0].text' 2>/dev/null | jq -r "$inner_jq" 2>/dev/null
}

assert_tool_field() {
    local desc="$1" response="$2" inner_jq="$3" expected="$4"
    local actual
    actual=$(tool_field "$response" "$inner_jq")
    if [[ "$actual" == "$expected" ]]; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: expected '$expected', got '$actual'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
        printf "    expected: %s\n" "$expected"
        printf "    got:      %s\n" "$actual"
    fi
}

assert_tool_contains() {
    local desc="$1" response="$2" inner_jq="$3" substr="$4"
    local actual
    actual=$(tool_field "$response" "$inner_jq")
    if echo "$actual" | grep -qiF "$substr"; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: expected to contain '$substr', got '$actual'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
        printf "    expected to contain: %s\n" "$substr"
        printf "    got: %s\n" "$actual"
    fi
}

assert_tool_nonempty() {
    local desc="$1" response="$2" inner_jq="$3"
    local actual
    actual=$(tool_field "$response" "$inner_jq")
    if [[ -n "$actual" ]] && [[ "$actual" != "null" ]]; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: expected non-empty, got '$actual'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
    fi
}

# ── Detect Remote OS ─────────────────────────────────

REMOTE_OS=$(ssh $SSH_OPTS "$HOST" "uname -s 2>/dev/null || echo Windows" 2>/dev/null | tr -d '\r')
if [[ "$REMOTE_OS" == *"Linux"* ]] || [[ "$REMOTE_OS" == *"Darwin"* ]]; then
    IS_WINDOWS=false
    TEST_DIR="/tmp/rush-mcp-test"
    TEST_FILE="$TEST_DIR/mcp-test.txt"
    MISSING_FILE="/no-such-file-xyz.txt"
else
    IS_WINDOWS=true
    TEST_DIR='C:\rush-test'
    TEST_FILE='C:\rush-test\mcp-test.txt'
    MISSING_FILE='C:\no-such-file-xyz.txt'
fi

# ── Setup ────────────────────────────────────────────

echo "Setting up test fixtures on $HOST ($REMOTE_OS)..."
if [[ "$IS_WINDOWS" == true ]]; then
    ssh $SSH_OPTS "$HOST" "mkdir C:\\rush-test 2>nul" 2>/dev/null || true
    ssh $SSH_OPTS "$HOST" "Set-Content -Path '$TEST_FILE' -Value 'mcp-test-content-42' -NoNewline" 2>/dev/null \
        || echo "  Warning: could not deploy test file"
else
    ssh $SSH_OPTS "$HOST" "mkdir -p $TEST_DIR && printf 'mcp-test-content-42' > $TEST_FILE" 2>/dev/null \
        || echo "  Warning: could not deploy test file"
fi

# ── Header ───────────────────────────────────────────

echo ""
echo "═══════════════════════════════════════════"
echo " Rush MCP SSH Gateway Test Suite"
echo " Host: $HOST"
echo "═══════════════════════════════════════════"
echo ""

# ── Batch A: Protocol Tests (no SSH) ────────────────
echo "── Protocol ──"

RESPONSES=$(printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
    '{"jsonrpc":"2.0","id":3,"method":"bogus/method","params":{}}' \
    '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{}}' \
    '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"bogus_tool","arguments":{}}}' \
    | "$RUSH_BIN" --mcp-ssh 2>/dev/null)

R1=$(echo "$RESPONSES" | sed -n '1p')
R2=$(echo "$RESPONSES" | sed -n '2p')
R3=$(echo "$RESPONSES" | sed -n '3p')
R4=$(echo "$RESPONSES" | sed -n '4p')
R5=$(echo "$RESPONSES" | sed -n '5p')

assert_field  "init: protocolVersion"   "$R1" '.result.protocolVersion'    '2024-11-05'
assert_field  "init: serverInfo.name"   "$R1" '.result.serverInfo.name'    'rush-ssh'
assert_field  "tools/list: count"       "$R2" '.result.tools | length'     '3'
assert_field  "tools/list: rush_execute"     "$R2" '.result.tools[0].name' 'rush_execute'
assert_field  "tools/list: rush_read_file"   "$R2" '.result.tools[1].name' 'rush_read_file'
assert_field  "tools/list: rush_context"     "$R2" '.result.tools[2].name' 'rush_context'
assert_field  "unknown method: -32601"  "$R3" '.error.code'               '-32601'
assert_field  "missing tool name: -32602"  "$R4" '.error.code'            '-32602'
assert_field  "unknown tool: -32602"    "$R5" '.error.code'               '-32602'

# ── Batch B: rush_execute ───────────────────────────
echo ""
echo "── rush_execute ──"

RESPONSES=$(printf '%s\n' \
    "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"echo hello\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"hostname\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"rush -c 'echo from-rush'\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"rush -c 'x = 2 + 3; echo \$x'\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"no-such-cmd-xyz123\"}}}" \
    '{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"echo orphan"}}}' \
    "{\"jsonrpc\":\"2.0\",\"id\":16,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\"}}}" \
    | "$RUSH_BIN" --mcp-ssh 2>/dev/null)

R10=$(echo "$RESPONSES" | sed -n '1p')
R11=$(echo "$RESPONSES" | sed -n '2p')
R12=$(echo "$RESPONSES" | sed -n '3p')
R13=$(echo "$RESPONSES" | sed -n '4p')
R14=$(echo "$RESPONSES" | sed -n '5p')
R15=$(echo "$RESPONSES" | sed -n '6p')
R16=$(echo "$RESPONSES" | sed -n '7p')

assert_tool_field    "echo hello"         "$R10" '.stdout'  'hello'
assert_tool_nonempty "hostname"           "$R11" '.stdout'
assert_tool_contains "rush -c echo"       "$R12" '.stdout'  'from-rush'
assert_tool_contains "rush -c arithmetic" "$R13" '.stdout'  '5'
assert_tool_field    "bad command: error"  "$R14" '.status'  'error'
assert_field         "missing host: -32602"  "$R15" '.error.code'  '-32602'
assert_field         "missing command: -32602" "$R16" '.error.code' '-32602'

# ── Batch C: rush_read_file ─────────────────────────
echo ""
echo "── rush_read_file ──"

# Escape backslashes for JSON
TEST_FILE_JSON="${TEST_FILE//\\/\\\\}"
MISSING_FILE_JSON="${MISSING_FILE//\\/\\\\}"

RESPONSES=$(printf '%s\n' \
    "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"$TEST_FILE_JSON\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"$MISSING_FILE_JSON\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\"}}}" \
    | "$RUSH_BIN" --mcp-ssh 2>/dev/null)

R20=$(echo "$RESPONSES" | sed -n '1p')
R21=$(echo "$RESPONSES" | sed -n '2p')
R22=$(echo "$RESPONSES" | sed -n '3p')

assert_tool_contains "read test file"       "$R20" '.content'  'mcp-test-content-42'
assert_tool_field    "read missing: error"   "$R21" '.status'   'error'
assert_field         "missing path: -32602"  "$R22" '.error.code' '-32602'

# ── Batch D: rush_context ───────────────────────────
echo ""
echo "── rush_context ──"

RESPONSES=$(printf '%s\n' \
    "{\"jsonrpc\":\"2.0\",\"id\":30,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_context\",\"arguments\":{\"host\":\"$HOST\"}}}" \
    | "$RUSH_BIN" --mcp-ssh 2>/dev/null)

R30=$(echo "$RESPONSES" | sed -n '1p')

assert_tool_nonempty "context: hostname"  "$R30" '.hostname'
assert_tool_nonempty "context: cwd"       "$R30" '.cwd'

# ── Cleanup ──────────────────────────────────────────
if [[ "$IS_WINDOWS" == true ]]; then
    ssh $SSH_OPTS "$HOST" "Remove-Item -Force '$TEST_FILE' -ErrorAction SilentlyContinue" 2>/dev/null || true
else
    ssh $SSH_OPTS "$HOST" "rm -f '$TEST_FILE' && rmdir '$TEST_DIR' 2>/dev/null" 2>/dev/null || true
fi

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

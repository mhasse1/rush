#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Rush --mcp Mode Test Suite
# Tests the MCP (Model Context Protocol) JSON-RPC 2.0 server.
# Run: bash mcp-mode-test.sh [path-to-rush]
# Requires: jq
# ═══════════════════════════════════════════════════════════════════════

RUSH="${1:-rush}"
PASS=0
FAIL=0
TMPDIR="${TMPDIR:-/tmp}"
TEST_DIR="$TMPDIR/rush-mcp-test-$$"
mkdir -p "$TEST_DIR"

# ── Helpers ───────────────────────────────────────────────────────────

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

# Send JSON-RPC requests to rush --mcp, capture all output
# Usage: output=$(mcp_session '{"jsonrpc":"2.0",...}' '{"jsonrpc":"2.0",...}')
mcp_session() {
    local input=""
    for req in "$@"; do
        input+="$req"$'\n'
    done
    echo "$input" | "$RUSH" --mcp 2>/dev/null
}

# Extract the Nth JSON line (1-based for readability)
json_line() {
    echo "$1" | sed -n "${2}p"
}

# Shorthand for jq on a string
jf() {
    echo "$1" | jq -r "$2" 2>/dev/null
}

# Extract the text content from an MCP tool result (nested JSON)
tool_text() {
    echo "$1" | jq -r '.result.content[0].text' 2>/dev/null
}

# Extract a field from the nested tool result JSON
tool_field() {
    local text
    text=$(tool_text "$1")
    echo "$text" | jq -r "$2" 2>/dev/null
}

# ── Tests ─────────────────────────────────────────────────────────────

echo "# Rush --mcp Mode Tests"
echo ""

# ── 1. Initialize ────────────────────────────────────────────────────
echo "## 1. Initialize"

output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}')
init=$(json_line "$output" 1)

if [ "$(jf "$init" '.result.protocolVersion')" = "2024-11-05" ]; then
    pass "init: protocolVersion=2024-11-05"
else
    fail "init: protocolVersion" "got $(jf "$init" '.result.protocolVersion')"
fi

if [ "$(jf "$init" '.result.serverInfo.name')" = "rush-local" ]; then
    pass "init: server name=rush-local"
else
    fail "init: server name" "got $(jf "$init" '.result.serverInfo.name')"
fi

if [ -n "$(jf "$init" '.result.serverInfo.version')" ]; then
    pass "init: version present ($(jf "$init" '.result.serverInfo.version'))"
else
    fail "init: version" "missing"
fi

if [ "$(jf "$init" '.result.capabilities.tools')" != "null" ]; then
    pass "init: tools capability"
else
    fail "init: tools" "missing"
fi

if [ "$(jf "$init" '.result.capabilities.resources')" != "null" ]; then
    pass "init: resources capability"
else
    fail "init: resources" "missing"
fi

if [ -n "$(jf "$init" '.result.instructions')" ] && [ "$(jf "$init" '.result.instructions')" != "null" ]; then
    pass "init: instructions present"
else
    fail "init: instructions" "missing"
fi

# ── 2. Tools List ────────────────────────────────────────────────────
echo ""
echo "## 2. Tools List"

output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
tools_resp=$(json_line "$output" 2)

tool_count=$(jf "$tools_resp" '.result.tools | length')
if [ "$tool_count" -ge 3 ]; then
    pass "tools/list: $tool_count tools returned"
else
    fail "tools/list" "got $tool_count tools, expected >= 3"
fi

# Check for expected tools
for tool_name in rush_execute rush_read_file rush_context; do
    if jf "$tools_resp" ".result.tools[] | select(.name == \"$tool_name\") | .name" | grep -q "$tool_name"; then
        pass "tools/list: $tool_name present"
    else
        fail "tools/list" "$tool_name missing"
    fi
done

# Check tool schemas have required fields
schema=$(jf "$tools_resp" '.result.tools[] | select(.name == "rush_execute") | .inputSchema')
if echo "$schema" | jq -e '.properties.command' >/dev/null 2>&1; then
    pass "tools/list: rush_execute has command param"
else
    fail "tools/list: rush_execute schema" "missing command"
fi

# ── 3. rush_execute ──────────────────────────────────────────────────
echo ""
echo "## 3. rush_execute"

output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"echo mcp hello"}}}')
exec_resp=$(json_line "$output" 2)

if [ "$(tool_field "$exec_resp" '.status')" = "success" ]; then
    pass "rush_execute: status=success"
else
    fail "rush_execute: status" "got $(tool_field "$exec_resp" '.status') — $(tool_field "$exec_resp" '.stderr')"
fi

if tool_field "$exec_resp" '.stdout' | grep -q "mcp hello"; then
    pass "rush_execute: stdout correct"
else
    fail "rush_execute: stdout" "got $(tool_field "$exec_resp" '.stdout')"
fi

if [ "$(jf "$exec_resp" '.result.isError')" = "false" ]; then
    pass "rush_execute: isError=false"
else
    fail "rush_execute: isError" "got $(jf "$exec_resp" '.result.isError')"
fi

# Execute Rush syntax
output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"puts \"rush syntax via mcp\""}}}')
exec_resp=$(json_line "$output" 2)

if tool_field "$exec_resp" '.stdout' | grep -q "rush syntax via mcp"; then
    pass "rush_execute: Rush syntax works"
else
    fail "rush_execute: Rush syntax" "got $(tool_field "$exec_resp" '.stdout')"
fi

# Execute with error
output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"nonexistent_cmd_xyz_123"}}}')
exec_resp=$(json_line "$output" 2)

if [ "$(tool_field "$exec_resp" '.status')" = "error" ]; then
    pass "rush_execute: error command returns error status"
else
    fail "rush_execute: error status" "got $(tool_field "$exec_resp" '.status')"
fi

if [ "$(jf "$exec_resp" '.result.isError')" = "true" ]; then
    pass "rush_execute: isError=true for failed command"
else
    fail "rush_execute: isError" "got $(jf "$exec_resp" '.result.isError')"
fi

# ── 4. rush_read_file ────────────────────────────────────────────────
echo ""
echo "## 4. rush_read_file"

echo "mcp file content" > "$TEST_DIR/mcp-read-test.txt"

output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"path\":\"$TEST_DIR/mcp-read-test.txt\"}}}")
read_resp=$(json_line "$output" 2)

if [ "$(tool_field "$read_resp" '.status')" = "success" ]; then
    pass "rush_read_file: status=success"
else
    fail "rush_read_file: status" "got $(tool_field "$read_resp" '.status')"
fi

if tool_field "$read_resp" '.content' | grep -q "mcp file content"; then
    pass "rush_read_file: content correct"
else
    fail "rush_read_file: content" "got $(tool_field "$read_resp" '.content')"
fi

if [ "$(tool_field "$read_resp" '.mime')" = "text/plain" ]; then
    pass "rush_read_file: mime=text/plain"
else
    fail "rush_read_file: mime" "got $(tool_field "$read_resp" '.mime')"
fi

if [ "$(tool_field "$read_resp" '.encoding')" = "utf8" ]; then
    pass "rush_read_file: encoding=utf8"
else
    fail "rush_read_file: encoding" "got $(tool_field "$read_resp" '.encoding')"
fi

# Read missing file
output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_read_file","arguments":{"path":"/nonexistent/path/file.txt"}}}')
read_resp=$(json_line "$output" 2)

if [ "$(tool_field "$read_resp" '.status')" = "error" ]; then
    pass "rush_read_file: missing file returns error"
else
    fail "rush_read_file: missing file" "got $(tool_field "$read_resp" '.status')"
fi

# ── 5. rush_context ──────────────────────────────────────────────────
echo ""
echo "## 5. rush_context"

output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_context","arguments":{}}}')
ctx_resp=$(json_line "$output" 2)

if [ "$(tool_field "$ctx_resp" '.ready')" = "true" ]; then
    pass "rush_context: ready=true"
else
    fail "rush_context: ready" "got $(tool_field "$ctx_resp" '.ready')"
fi

if [ -n "$(tool_field "$ctx_resp" '.host')" ] && [ "$(tool_field "$ctx_resp" '.host')" != "null" ]; then
    pass "rush_context: host present ($(tool_field "$ctx_resp" '.host'))"
else
    fail "rush_context: host" "missing"
fi

if [ -n "$(tool_field "$ctx_resp" '.cwd')" ] && [ "$(tool_field "$ctx_resp" '.cwd')" != "null" ]; then
    pass "rush_context: cwd present"
else
    fail "rush_context: cwd" "missing"
fi

if [ "$(tool_field "$ctx_resp" '.shell')" = "rush" ]; then
    pass "rush_context: shell=rush"
else
    fail "rush_context: shell" "got $(tool_field "$ctx_resp" '.shell')"
fi

# ── 6. Resources ─────────────────────────────────────────────────────
echo ""
echo "## 6. Resources"

output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"resources/list"}')
res_resp=$(json_line "$output" 2)

res_count=$(jf "$res_resp" '.result.resources | length')
if [ "$res_count" -ge 1 ]; then
    pass "resources/list: $res_count resources"
else
    fail "resources/list" "got $res_count"
fi

if jf "$res_resp" '.result.resources[].uri' | grep -q "rush://lang-spec"; then
    pass "resources/list: rush://lang-spec present"
else
    fail "resources/list" "rush://lang-spec missing"
fi

# Read the lang spec resource
output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"rush://lang-spec"}}')
spec_resp=$(json_line "$output" 2)

if jf "$spec_resp" '.result.contents[0].text' | grep -q "Rush Language"; then
    pass "resources/read: lang-spec has content"
else
    fail "resources/read: lang-spec" "no content"
fi

if [ "$(jf "$spec_resp" '.result.contents[0].mimeType')" = "text/yaml" ]; then
    pass "resources/read: mimeType=text/yaml"
else
    fail "resources/read: mimeType" "got $(jf "$spec_resp" '.result.contents[0].mimeType')"
fi

# ── 7. Error Handling ────────────────────────────────────────────────
echo ""
echo "## 7. JSON-RPC Error Handling"

# Unknown method
output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"nonexistent/method"}')
err_resp=$(json_line "$output" 2)

if [ "$(jf "$err_resp" '.error.code')" = "-32601" ]; then
    pass "error: unknown method returns -32601"
else
    fail "error: unknown method" "got code $(jf "$err_resp" '.error.code')"
fi

# Unknown tool
output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"nonexistent_tool","arguments":{}}}')
err_resp=$(json_line "$output" 2)

if [ "$(jf "$err_resp" '.error.code')" = "-32602" ]; then
    pass "error: unknown tool returns -32602"
else
    fail "error: unknown tool" "got code $(jf "$err_resp" '.error.code')"
fi

# Missing required parameter
output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{}}}')
err_resp=$(json_line "$output" 2)

if [ "$(jf "$err_resp" '.error.code')" = "-32602" ]; then
    pass "error: missing param returns -32602"
else
    fail "error: missing param" "got code $(jf "$err_resp" '.error.code')"
fi

# ── 8. State Persistence ─────────────────────────────────────────────
echo ""
echo "## 8. State Persistence"

# Variables should persist across tool calls in the same session
output=$(mcp_session \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"x = 99"}}}' \
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"puts x"}}}')
persist_resp=$(json_line "$output" 3)

if tool_field "$persist_resp" '.stdout' | grep -q "99"; then
    pass "state: variables persist across calls"
else
    fail "state: variable persistence" "got $(tool_field "$persist_resp" '.stdout')"
fi

# ── Cleanup ──────────────────────────────────────────────────────────
rm -rf "$TEST_DIR"

# ── Summary ──────────────────────────────────────────────────────────
echo ""
TOTAL=$((PASS + FAIL))
echo "# MCP Mode Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi

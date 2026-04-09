#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# MCP-Local ps5 Reliability Test (#142)
# Tests ps5 blocks through rush --mcp (local) instead of rush --mcp-ssh.
# Run on Windows only (ps5 blocks need Windows PowerShell 5.1).
# Run: bash mcp-local-ps5-test.sh [path-to-rush]
# ═══════════════════════════════════════════════════════════════════════

RUSH="${1:-rush}"
PASS=0
FAIL=0

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

mcp_local() {
    local input=""
    for req in "$@"; do input+="$req"$'\n'; done
    echo "$input" | "$RUSH" --mcp 2>/dev/null
}

jf() { echo "$1" | jq -r "$2" 2>/dev/null; }

json_line() { echo "$1" | sed -n "$((${2} + 1))p"; }

find_resp() {
    local lines="$1" id="$2"
    echo "$lines" | while IFS= read -r line; do
        local rid=$(echo "$line" | jq -r '.id // empty' 2>/dev/null)
        [[ "$rid" == "$id" ]] && echo "$line" && return
    done
}

tool_field() {
    local text=$(jf "$1" '.result.content[0].text')
    echo "$text" | jq -r "$2" 2>/dev/null
}

INIT='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'

echo "# MCP-Local ps5 Reliability Tests"
echo ""

# Check if we're on Windows
os_check=$("$RUSH" -c 'puts os' 2>/dev/null)
if [[ "$os_check" != "windows" ]]; then
    echo "SKIP: not Windows (ps5 blocks need Windows PS 5.1)"
    echo "# MCP-Local ps5 Tests: skipped (not Windows)"
    exit 0
fi

# ═══════════════════════════════════════════════════════════════════════
echo "## 1. Simple ps5 block via MCP-local"
# ═══════════════════════════════════════════════════════════════════════

output=$(mcp_local "$INIT" \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"ps5\n  $env:MCP_PS5_1 = \"mcp-local-works\"\nend\nputs env.MCP_PS5_1"}}}')
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "mcp-local-works" ]]; then
    pass "mcp-local ps5: basic block"
else
    fail "mcp-local ps5: basic" "got $stdout — $(tool_field "$resp" '.stderr')"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 2. ps5 with pipeline via MCP-local"
# ═══════════════════════════════════════════════════════════════════════

output=$(mcp_local "$INIT" \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"ps5\n  $env:MCP_PS5_2 = (1..5 | ForEach-Object { $_ * 3 }) -join \",\"\nend\nputs env.MCP_PS5_2"}}}')
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "3,6,9,12,15" ]]; then
    pass "mcp-local ps5: pipeline"
else
    fail "mcp-local ps5: pipeline" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 3. ps5 error handling via MCP-local"
# ═══════════════════════════════════════════════════════════════════════

output=$(mcp_local "$INIT" \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"ps5\n  try { throw \"test-error\" } catch { $env:MCP_PS5_3 = \"caught\" }\nend\nputs env.MCP_PS5_3"}}}')
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "caught" ]]; then
    pass "mcp-local ps5: try/catch"
else
    fail "mcp-local ps5: try/catch" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 4. ps5 JSON output via MCP-local"
# ═══════════════════════════════════════════════════════════════════════

output=$(mcp_local "$INIT" \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"ps5\n  $env:MCP_PS5_4 = (@{name=\"test\";count=42} | ConvertTo-Json -Compress)\nend\nputs env.MCP_PS5_4"}}}')
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | jq -e '.name' >/dev/null 2>&1; then
    pass "mcp-local ps5: JSON output"
else
    fail "mcp-local ps5: JSON" "got $stdout"
fi

# Cleanup
mcp_local "$INIT" \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"ps\n  @(\"MCP_PS5_1\",\"MCP_PS5_2\",\"MCP_PS5_3\",\"MCP_PS5_4\") | ForEach-Object { Remove-Item \"Env:\\$_\" -ErrorAction SilentlyContinue }\nend"}}}' >/dev/null 2>&1

echo ""
TOTAL=$((PASS + FAIL))
echo "# MCP-Local ps5 Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
[[ $FAIL -gt 0 ]] && exit 1

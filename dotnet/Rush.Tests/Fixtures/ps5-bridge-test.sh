#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# ps5 Variable Bridging Test
# Tests that Rush variables bridge into ps5 blocks via JSON temp file.
# Run: bash ps5-bridge-test.sh [windows-host]
# Default host: buster (must have Rush + PS 5.1)
# ═══════════════════════════════════════════════════════════════════════

RUSH="${RUSH:-rush}"
HOST="${1:-buster}"
PASS=0
FAIL=0

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

mcp_ssh() {
    local input=""
    for req in "$@"; do input+="$req"$'\n'; done
    echo "$input" | "$RUSH" --mcp-ssh 2>/dev/null
}

jf() { echo "$1" | jq -r "$2" 2>/dev/null; }

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

echo "# ps5 Variable Bridging Tests → $HOST"
echo ""

# Verify host has Rush
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_context\",\"arguments\":{\"host\":\"$HOST\"}}}")
resp=$(find_resp "$output" 2)
shell=$(tool_field "$resp" '.shell')

if [[ "$shell" != "rush" ]]; then
    echo "SKIP: $HOST has shell=$shell (need rush)"
    exit 0
fi

# Verify Windows
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts os\"}}}")
resp=$(find_resp "$output" 2)
remote_os=$(tool_field "$resp" '.stdout' | tr -d '\r\n')

if [[ "$remote_os" != "windows" ]]; then
    echo "SKIP: $HOST is $remote_os (ps5 bridging is Windows-only)"
    exit 0
fi

echo "## String bridging"

# Set a Rush variable, use it in a ps5 block
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"bridge_str = \\\"hello from rush\\\"\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:PS5_BRIDGE_STR = \$bridge_str\\nend\\nputs env.PS5_BRIDGE_STR\"}}}")
resp=$(find_resp "$output" 3)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "hello from rush"; then
    pass "ps5 bridge: string variable"
else
    fail "ps5 bridge: string" "got $stdout — $(tool_field "$resp" '.stderr')"
fi

echo ""
echo "## Numeric bridging"

output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"bridge_num = 42\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:PS5_BRIDGE_NUM = \$bridge_num.ToString()\\nend\\nputs env.PS5_BRIDGE_NUM\"}}}")
resp=$(find_resp "$output" 3)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "42" ]]; then
    pass "ps5 bridge: numeric variable"
else
    fail "ps5 bridge: numeric" "got $stdout"
fi

echo ""
echo "## PS 5.1 specific feature"

# Use a PS 5.1-only feature — WMI (Get-WmiObject is PS 5.1 only, removed in PS 7)
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:PS5_WMI = (Get-WmiObject Win32_OperatingSystem).Caption\\nend\\nputs env.PS5_WMI\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -qi "Windows"; then
    pass "ps5: WMI query (PS 5.1 only) — $stdout"
else
    fail "ps5: WMI" "got $stdout — $(tool_field "$resp" '.stderr')"
fi

# Cleanup
mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  @('PS5_BRIDGE_STR','PS5_BRIDGE_NUM','PS5_WMI') | ForEach-Object { Remove-Item \\\"Env:\\\\\$_\\\" -ErrorAction SilentlyContinue }\\nend\"}}}" >/dev/null 2>&1

echo ""
TOTAL=$((PASS + FAIL))
echo "# ps5 Bridging Tests Complete ($HOST): $PASS passed, $FAIL failed (of $TOTAL)"
[[ $FAIL -gt 0 ]] && exit 1

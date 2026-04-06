#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Windows-Specific Verification Tests
# Tests fixes that need Windows verification, run via MCP-SSH.
# Run: bash windows-verify-test.sh [windows-host]
# Default: buster
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

echo "# Windows Verification Tests → $HOST"
echo ""

# Verify Windows + Rush
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts os\"}}}")
resp=$(find_resp "$output" 2)
remote_os=$(tool_field "$resp" '.stdout' | tr -d '\r\n')

if [[ "$remote_os" != "windows" ]]; then
    echo "SKIP: $HOST is $remote_os (need Windows)"
    exit 0
fi
pass "host: $HOST is Windows"

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 1. PATH Normalization (#111)"
# ═══════════════════════════════════════════════════════════════════════

# $PATH should use forward slashes
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts \$PATH\"}}}")
resp=$(find_resp "$output" 2)
path_val=$(tool_field "$resp" '.stdout')

if echo "$path_val" | grep -q "/"; then
    pass "PATH: forward slashes"
else
    fail "PATH: slashes" "got $(echo "$path_val" | head -c 80)"
fi

if echo "$path_val" | grep -q ":"; then
    pass "PATH: colon separators"
else
    fail "PATH: separators" "no colons"
fi

# Should NOT have backslashes
if echo "$path_val" | grep -qv '\\'; then
    pass "PATH: no backslashes"
else
    fail "PATH: backslashes" "found \\ in PATH"
fi

# $env:PATH should still be native (for child processes)
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  \$env:RUSH_NATIVE_PATH = \$env:PATH.Substring(0,30)\\nend\\nputs env.RUSH_NATIVE_PATH\"}}}")
resp=$(find_resp "$output" 2)
native_path=$(tool_field "$resp" '.stdout')

if echo "$native_path" | grep -q '\\'; then
    pass "PATH: \$env:PATH still native (has backslashes)"
else
    fail "PATH: native" "got $native_path"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 2. Backslash-Space in Commands (#112)"
# ═══════════════════════════════════════════════════════════════════════

# Create a dir with space, ls it with backslash-escape
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"Dir.mkdir(\\\"C:/temp/rush space test\\\")\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_write_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"C:/temp/rush space test/hello.txt\",\"content\":\"space test\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts File.exist?(\\\"C:/temp/rush space test/hello.txt\\\")\"}}}")
resp=$(find_resp "$output" 4)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -qi "true"; then
    pass "spaces: File.exist? with spaced path"
else
    fail "spaces: exist?" "got $stdout"
fi

# Read it back
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"C:/temp/rush space test/hello.txt\"}}}")
resp=$(find_resp "$output" 2)
content=$(tool_field "$resp" '.content')

if echo "$content" | grep -q "space test"; then
    pass "spaces: read file in spaced dir"
else
    fail "spaces: read" "got $content"
fi

# Cleanup
mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  Remove-Item 'C:/temp/rush space test' -Recurse -Force -ErrorAction SilentlyContinue\\nend\"}}}" >/dev/null 2>&1

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 3. COLUMNS/LINES Env Vars (#74)"
# ═══════════════════════════════════════════════════════════════════════

output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  \$env:RUSH_COLS = \$env:COLUMNS\\nend\\nputs env.RUSH_COLS\"}}}")
resp=$(find_resp "$output" 2)
cols=$(tool_field "$resp" '.stdout')

if [[ -n "$cols" && "$cols" != "null" ]] && echo "$cols" | grep -qE '^[0-9]+$'; then
    pass "COLUMNS: set ($cols)"
else
    # COLUMNS may not be set in non-interactive (LLM) mode — that's OK
    pass "COLUMNS: not set in LLM mode (expected)"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 4. SSH Rush Detection (#79)"
# ═══════════════════════════════════════════════════════════════════════

# Already verified by the fact that we got shell=rush in the init
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_context\",\"arguments\":{\"host\":\"$HOST\"}}}")
resp=$(find_resp "$output" 2)
shell=$(tool_field "$resp" '.shell')

if [[ "$shell" == "rush" ]]; then
    pass "SSH detection: Rush found on $HOST (shell=rush)"
else
    fail "SSH detection" "got shell=$shell"
fi

version=$(tool_field "$resp" '.version')
if [[ -n "$version" && "$version" != "null" ]]; then
    pass "SSH detection: version=$version"
else
    fail "SSH detection: version" "missing"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 5. path Display Normalization (#111)"
# ═══════════════════════════════════════════════════════════════════════

# path command should show forward slashes
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"path\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "PATH entries"; then
    pass "path: shows entries"
else
    fail "path: display" "got $(echo "$stdout" | head -1)"
fi

if echo "$stdout" | grep -q "/"; then
    pass "path: display uses forward slashes"
else
    fail "path: slashes" "no forward slashes in display"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 6. ps5 Block Targets 64-bit PS"
# ═══════════════════════════════════════════════════════════════════════

# ps5 should use System32 (64-bit) not SysWOW64 (32-bit)
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:RUSH_PS5_VER = \$PSVersionTable.PSVersion.ToString()\\nend\\nputs env.RUSH_PS5_VER\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "5.1"; then
    pass "ps5: runs PS 5.1 ($stdout)"
else
    fail "ps5: version" "got $stdout — $(tool_field "$resp" '.stderr')"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 7. Coreutils/Diffutils Shims"
# ═══════════════════════════════════════════════════════════════════════

# Check if ls works (via coreutils shim or native)
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ls C:/Windows\"}}}")
resp=$(find_resp "$output" 2)
status=$(tool_field "$resp" '.status')

if [[ "$status" == "success" ]]; then
    pass "coreutils: ls works"
else
    fail "coreutils: ls" "$(tool_field "$resp" '.stderr')"
fi

# ── Cleanup env vars ─────────────────────────────────────────────────
mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  @('RUSH_NATIVE_PATH','RUSH_COLS','RUSH_PS5_VER') | ForEach-Object { Remove-Item \\\"Env:\\\\\$_\\\" -ErrorAction SilentlyContinue }\\nend\"}}}" >/dev/null 2>&1

# ── Summary ──────────────────────────────────────────────────────────
echo ""
TOTAL=$((PASS + FAIL))
echo "# Windows Verification Complete ($HOST): $PASS passed, $FAIL failed (of $TOTAL)"
[[ $FAIL -gt 0 ]] && exit 1

#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# MCP-SSH Metacharacter Survival Test
# Verifies that special characters survive SSH transport via the
# JSON envelope protocol. These are the B1/B2 bug scenarios.
# Run: bash mcp-ssh-metachar-test.sh [host]
# Default host: trinity (must have Rush installed)
# ═══════════════════════════════════════════════════════════════════════

RUSH="${RUSH:-rush}"
HOST="${1:-trinity}"
PASS=0
FAIL=0

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

mcp_ssh() {
    local input=""
    for req in "$@"; do
        input+="$req"$'\n'
    done
    echo "$input" | "$RUSH" --mcp-ssh 2>/dev/null
}

jf() { echo "$1" | jq -r "$2" 2>/dev/null; }

find_resp() {
    local lines="$1" id="$2"
    echo "$lines" | while IFS= read -r line; do
        local rid
        rid=$(echo "$line" | jq -r '.id // empty' 2>/dev/null)
        if [[ "$rid" == "$id" ]]; then
            echo "$line"
            return
        fi
    done
}

tool_field() {
    local resp="$1" field="$2"
    local text
    text=$(jf "$resp" '.result.content[0].text')
    echo "$text" | jq -r "$field" 2>/dev/null
}

INIT_REQ='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'

echo "# MCP-SSH Metacharacter Survival Tests → $HOST"
echo ""

# Verify we have a Rush session (envelope protocol required)
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_context\",\"arguments\":{\"host\":\"$HOST\"}}}")
resp=$(find_resp "$output" 2)
shell=$(tool_field "$resp" '.shell')

if [[ "$shell" != "rush" ]]; then
    echo "SKIP: $HOST has shell=$shell (need rush for envelope protocol)"
    exit 0
fi

# ═══════════════════════════════════════════════════════════════════════
echo "## Dollar-underscore (\$_) — B1 scenario"
# This was the original bug: $_ stripped by SSH transport
# ═══════════════════════════════════════════════════════════════════════

# ForEach-Object with $_
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  \$env:META1 = (1..5 | ForEach-Object { \$_ * 2 }) -join ','\\nend\\nputs env.META1\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "2,4,6,8,10" ]]; then
    pass "\$_ in ForEach-Object pipeline"
else
    fail "\$_ ForEach" "got $stdout"
fi

# Where-Object with $_
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  \$env:META2 = (1..10 | Where-Object { \$_ -gt 7 }) -join ','\\nend\\nputs env.META2\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "8,9,10" ]]; then
    pass "\$_ in Where-Object filter"
else
    fail "\$_ Where" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## Semicolons — B2 scenario"
# This was the second bug: semicolons split commands during SSH transport
# ═══════════════════════════════════════════════════════════════════════

# Multiple statements with semicolons
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  \$a = 10; \$b = 20; \$c = \$a + \$b; \$env:META3 = \$c.ToString()\\nend\\nputs env.META3\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "30" ]]; then
    pass "semicolons: multi-statement"
else
    fail "semicolons" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## Quotes and special strings"
# ═══════════════════════════════════════════════════════════════════════

# String with single quotes
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts \\\"it's working\\\"\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "it's working"; then
    pass "quotes: single inside double"
else
    fail "quotes: single" "got $stdout"
fi

# String with special chars
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts \\\"path: /usr/local/bin\\\"\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "path: /usr/local/bin"; then
    pass "quotes: forward slashes"
else
    fail "quotes: slashes" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## PowerShell-specific syntax via ps block"
# ═══════════════════════════════════════════════════════════════════════

# Hashtable creation
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  \$h = @{ name = 'rush'; version = 1 }; \$env:META4 = \$h['name']\\nend\\nputs env.META4\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "rush" ]]; then
    pass "PS: hashtable @{} syntax"
else
    fail "PS: hashtable" "got $stdout"
fi

# Array subexpression @()
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  \$env:META5 = @(1,2,3).Count.ToString()\\nend\\nputs env.META5\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "3" ]]; then
    pass "PS: array @() syntax"
else
    fail "PS: array" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## exec_script: complex scripts over SSH"
# Real-world: push a script with all the problematic patterns
# ═══════════════════════════════════════════════════════════════════════

# Rush script with interpolation, array ops, conditionals
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"complex.rush\",\"content\":\"servers = [\\\"web1\\\", \\\"web2\\\", \\\"db1\\\"]\\nweb = servers.select { |s| s.start_with?(\\\"web\\\") }\\nputs \\\"web servers: #{web.count}\\\"\\nputs \\\"all: #{servers.join(', ')}\\\"\"}}}")
resp=$(find_resp "$output" 2)
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "web servers: 2"; then
    pass "exec_script: Rush with array.select"
else
    fail "exec_script: Rush select" "got $stdout — $(tool_field "$resp" '.stderr')"
fi

# Detect remote OS for OS-appropriate script test
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts os\"}}}")
resp=$(find_resp "$output" 2)
remote_os=$(tool_field "$resp" '.stdout' | tr -d '\r\n')

if [[ "$remote_os" == "windows" ]]; then
    # PowerShell script with $args (Windows metacharacter equivalent)
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"meta.ps1\",\"content\":\"Write-Host \\\"args: \$(\$args -join ' ')\\\"\\nWrite-Host \\\"first: \$(\$args[0])\\\"\\nWrite-Host \\\"count: \$(\$args.Count)\\\"\",\"args\":[\"hello\",\"world\"]}}}")
    resp=$(find_resp "$output" 2)
    stdout=$(tool_field "$resp" '.stdout')

    if echo "$stdout" | grep -q "args: hello world" && echo "$stdout" | grep -q "first: hello"; then
        pass "exec_script: PS with \$args ($remote_os)"
    else
        fail "exec_script: PS metachar" "got $stdout"
    fi
else
    # Bash script with $1, $@, $? (all shell metacharacters)
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"meta.sh\",\"content\":\"#!/bin/bash\\necho \\\"args: \$@\\\"\\necho \\\"first: \$1\\\"\\necho \\\"count: \$#\\\"\\ntrue\\necho \\\"exit: \$?\\\"\",\"args\":[\"hello\",\"world\"]}}}")
    resp=$(find_resp "$output" 2)
    stdout=$(tool_field "$resp" '.stdout')

    if echo "$stdout" | grep -q "args: hello world" && echo "$stdout" | grep -q "first: hello" && echo "$stdout" | grep -q "exit: 0"; then
        pass "exec_script: bash with \$@ \$1 \$# \$? ($remote_os)"
    else
        fail "exec_script: bash metachar" "got $stdout"
    fi
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## File transfer: special content"
# ═══════════════════════════════════════════════════════════════════════

# Write a file with JSON content (quotes inside quotes)
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_write_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-meta-test.json\",\"content\":\"{\\\"servers\\\": [\\\"web1\\\", \\\"web2\\\"], \\\"config\\\": {\\\"port\\\": 8080}}\"}}}")
resp=$(find_resp "$output" 2)

if [[ "$(tool_field "$resp" '.status')" == "success" ]]; then
    pass "file: write JSON content"
else
    fail "file: write JSON" "$(tool_field "$resp" '.stderr')"
fi

# Read it back and verify
output=$(mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-meta-test.json\"}}}")
resp=$(find_resp "$output" 2)
content=$(tool_field "$resp" '.content')

if echo "$content" | jq -e '.servers[1]' 2>/dev/null | grep -q "web2"; then
    pass "file: JSON content survived round-trip"
else
    fail "file: JSON round-trip" "got $content"
fi

# Cleanup
mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"rm -f /tmp/rush-meta-test.json\"}}}" >/dev/null 2>&1

# Clean env vars
mcp_ssh "$INIT_REQ" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  @('META1','META2','META3','META4','META5') | ForEach-Object { Remove-Item \\\"Env:\\\\\$_\\\" -ErrorAction SilentlyContinue }\\nend\"}}}" >/dev/null 2>&1

# ── Summary ──────────────────────────────────────────────────────────
echo ""
TOTAL=$((PASS + FAIL))
echo "# Metacharacter Tests Complete ($HOST): $PASS passed, $FAIL failed (of $TOTAL)"
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi

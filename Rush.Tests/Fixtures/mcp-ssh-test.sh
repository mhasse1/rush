#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Rush --mcp-ssh Integration Test Suite
# Tests the MCP SSH gateway against real remote hosts.
# Run: bash mcp-ssh-test.sh [host1] [host2] ...
# Default hosts: trinity buster
# Requires: jq, rush on local machine, rush on remote hosts
# Non-destructive — uses temp files on remote, cleans up.
# ═══════════════════════════════════════════════════════════════════════

RUSH="${RUSH:-rush}"
HOSTS=("$@")
[[ ${#HOSTS[@]} -eq 0 ]] && HOSTS=(trinity buster)

PASS=0
FAIL=0

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

# Send JSON-RPC requests to rush --mcp-ssh, return all output lines
mcp_ssh() {
    local input=""
    for req in "$@"; do
        input+="$req"$'\n'
    done
    echo "$input" | "$RUSH" --mcp-ssh 2>/dev/null
}

# Extract field from JSON
jf() { echo "$1" | jq -r "$2" 2>/dev/null; }

# Find response by id
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

# Extract tool result text (nested JSON in content[0].text) and parse a field
tool_field() {
    local resp="$1" field="$2"
    local text
    text=$(jf "$resp" '.result.content[0].text')
    echo "$text" | jq -r "$field" 2>/dev/null
}

INIT_REQ='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'

echo "# Rush MCP-SSH Integration Tests"
echo ""

# ── 1. Initialize ────────────────────────────────────────────────────
echo "## 1. Initialize"

output=$(mcp_ssh "$INIT_REQ")
init=$(find_resp "$output" 1)

if [[ "$(jf "$init" '.result.serverInfo.name')" == "rush-ssh" ]]; then
    pass "init: server name=rush-ssh"
else
    fail "init: server name" "got $(jf "$init" '.result.serverInfo.name')"
fi

# ── 2. Tools List ────────────────────────────────────────────────────
echo ""
echo "## 2. Tools List"

output=$(mcp_ssh "$INIT_REQ" '{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
tools=$(find_resp "$output" 2)

tool_count=$(jf "$tools" '.result.tools | length')
if [[ "$tool_count" -ge 5 ]]; then
    pass "tools/list: $tool_count tools (expected >= 5)"
else
    fail "tools/list" "got $tool_count tools"
fi

for name in rush_execute rush_read_file rush_context rush_write_file rush_exec_script; do
    if jf "$tools" ".result.tools[] | select(.name == \"$name\") | .name" | grep -q "$name"; then
        pass "tools/list: $name present"
    else
        fail "tools/list" "$name missing"
    fi
done

# ── Per-host tests ───────────────────────────────────────────────────

for HOST in "${HOSTS[@]}"; do
    echo ""
    echo "═══════════════════════════════════════════════════════════"
    echo "## Host: $HOST"
    echo "═══════════════════════════════════════════════════════════"

    # ── 3. rush_context ──────────────────────────────────────────
    echo ""
    echo "### rush_context"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_context\",\"arguments\":{\"host\":\"$HOST\"}}}")
    resp=$(find_resp "$output" 2)

    shell=$(tool_field "$resp" '.shell')
    if [[ "$shell" == "rush" ]]; then
        pass "$HOST context: shell=rush (persistent session)"
    elif [[ "$shell" == "raw" ]]; then
        pass "$HOST context: shell=raw (fallback mode)"
        echo "  Note: Rush not detected on $HOST — some tests may fail"
    else
        fail "$HOST context: shell" "got $shell"
    fi

    hostname=$(tool_field "$resp" '.hostname // .host')
    if [[ -n "$hostname" && "$hostname" != "null" ]]; then
        pass "$HOST context: hostname=$hostname"
    else
        fail "$HOST context: hostname" "empty"
    fi

    # ── 4. rush_execute — simple command ─────────────────────────
    echo ""
    echo "### rush_execute"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"echo hello from $HOST\"}}}")
    resp=$(find_resp "$output" 2)

    status=$(tool_field "$resp" '.status')
    if [[ "$status" == "success" ]]; then
        pass "$HOST execute: status=success"
    else
        fail "$HOST execute: status" "got $status — $(tool_field "$resp" '.stderr')"
    fi

    stdout=$(tool_field "$resp" '.stdout')
    if echo "$stdout" | grep -q "hello from $HOST"; then
        pass "$HOST execute: stdout correct"
    else
        fail "$HOST execute: stdout" "got $stdout"
    fi

    # ── 5. rush_execute — Rush syntax ────────────────────────────
    echo ""
    echo "### rush_execute (Rush syntax)"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts \\\"rush on $HOST\\\"\"}}}")
    resp=$(find_resp "$output" 2)

    stdout=$(tool_field "$resp" '.stdout')
    if echo "$stdout" | grep -q "rush on $HOST"; then
        pass "$HOST execute Rush: stdout correct"
    else
        fail "$HOST execute Rush" "got $stdout"
    fi

    # ── 6. rush_execute — envelope with env vars ─────────────────
    echo ""
    echo "### rush_execute (envelope: env vars)"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"echo \$env:RUSH_SSH_TEST\",\"env\":{\"RUSH_SSH_TEST\":\"envelope_works\"}}}}")
    resp=$(find_resp "$output" 2)

    stdout=$(tool_field "$resp" '.stdout')
    if echo "$stdout" | grep -q "envelope_works"; then
        pass "$HOST execute env: value passed"
    else
        fail "$HOST execute env" "got $stdout"
    fi

    # ── 7. Variable persistence ──────────────────────────────────
    echo ""
    echo "### Variable persistence"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"test_var = 42\"}}}" \
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts test_var\"}}}")
    resp=$(find_resp "$output" 3)

    stdout=$(tool_field "$resp" '.stdout')
    if echo "$stdout" | grep -q "42"; then
        pass "$HOST persistence: variable survives"
    else
        fail "$HOST persistence" "got $stdout"
    fi

    # ── 8. rush_write_file + rush_read_file ──────────────────────
    echo ""
    echo "### File transfer (write + read)"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_write_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-ssh-test.txt\",\"content\":\"hello from mcp-ssh\"}}}")
    resp=$(find_resp "$output" 2)

    status=$(tool_field "$resp" '.status')
    if [[ "$status" == "success" ]]; then
        pass "$HOST write_file: status=success"
    else
        fail "$HOST write_file" "got $status — $(tool_field "$resp" '.stderr')"
    fi

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-ssh-test.txt\"}}}")
    resp=$(find_resp "$output" 2)

    content=$(tool_field "$resp" '.content')
    if echo "$content" | grep -q "hello from mcp-ssh"; then
        pass "$HOST read_file: content matches"
    else
        fail "$HOST read_file" "got $content"
    fi

    # ── 9. rush_exec_script ──────────────────────────────────────
    echo ""
    echo "### exec_script"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"test.rush\",\"content\":\"puts \\\"script on $HOST\\\"\"}}}")
    resp=$(find_resp "$output" 2)

    stdout=$(tool_field "$resp" '.stdout')
    if echo "$stdout" | grep -q "script on $HOST"; then
        pass "$HOST exec_script: stdout correct"
    else
        fail "$HOST exec_script" "got $stdout — $(tool_field "$resp" '.stderr')"
    fi

    # ── 10. Cleanup ──────────────────────────────────────────────
    mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"rm -f /tmp/rush-ssh-test.txt\"}}}" >/dev/null 2>&1

done

# ── Summary ──────────────────────────────────────────────────────────
echo ""
echo ""
TOTAL=$((PASS + FAIL))
echo "# MCP-SSH Integration Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
if [[ "$FAIL" -gt 0 ]]; then
    exit 1
fi

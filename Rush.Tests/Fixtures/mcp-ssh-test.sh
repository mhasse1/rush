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

if [[ -n "$(jf "$init" '.result.instructions')" ]]; then
    pass "init: instructions present"
else
    fail "init: instructions" "missing"
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

# Verify rush_execute schema includes envelope params
exec_schema=$(jf "$tools" '.result.tools[] | select(.name == "rush_execute") | .inputSchema.properties')
for param in host command cwd timeout env; do
    if echo "$exec_schema" | jq -e ".$param" >/dev/null 2>&1; then
        pass "tools/list: rush_execute has $param param"
    else
        fail "tools/list: rush_execute schema" "missing $param"
    fi
done

# ── Per-host tests ───────────────────────────────────────────────────

for HOST in "${HOSTS[@]}"; do
    echo ""
    echo "═══════════════════════════════════════════════════════════"
    echo "## Host: $HOST"
    echo "═══════════════════════════════════════════════════════════"

    # ── 3. Context ───────────────────────────────────────────────
    echo ""
    echo "### Context"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_context\",\"arguments\":{\"host\":\"$HOST\"}}}")
    resp=$(find_resp "$output" 2)

    shell=$(tool_field "$resp" '.shell')
    if [[ "$shell" == "rush" ]]; then
        pass "$HOST context: shell=rush (persistent session)"
    elif [[ "$shell" == "raw" ]]; then
        pass "$HOST context: shell=raw (fallback mode)"
    else
        fail "$HOST context: shell" "got $shell"
    fi

    hostname=$(tool_field "$resp" '.hostname // .host')
    if [[ -n "$hostname" && "$hostname" != "null" ]]; then
        pass "$HOST context: hostname=$hostname"
    else
        fail "$HOST context: hostname" "empty"
    fi

    cwd=$(tool_field "$resp" '.cwd')
    if [[ -n "$cwd" && "$cwd" != "null" ]]; then
        pass "$HOST context: cwd=$cwd"
    else
        fail "$HOST context: cwd" "empty"
    fi

    # ── 4. Simple command ────────────────────────────────────────
    echo ""
    echo "### Simple command"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"echo hello from $HOST\"}}}")
    resp=$(find_resp "$output" 2)

    if [[ "$(tool_field "$resp" '.status')" == "success" ]]; then
        pass "$HOST execute: status=success"
    else
        fail "$HOST execute" "$(tool_field "$resp" '.stderr')"
    fi

    if tool_field "$resp" '.stdout' | grep -q "hello from $HOST"; then
        pass "$HOST execute: stdout correct"
    else
        fail "$HOST execute: stdout" "got $(tool_field "$resp" '.stdout')"
    fi

    # ── 5. Rush syntax ───────────────────────────────────────────
    echo ""
    echo "### Rush syntax"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts \\\"rush on $HOST\\\"\"}}}")
    resp=$(find_resp "$output" 2)

    if tool_field "$resp" '.stdout' | grep -q "rush on $HOST"; then
        pass "$HOST Rush syntax: puts"
    else
        fail "$HOST Rush syntax" "got $(tool_field "$resp" '.stdout')"
    fi

    # Real-world: compute something with Rush
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"result = [1,2,3,4,5].count; puts result\"}}}")
    resp=$(find_resp "$output" 2)

    if [[ "$(tool_field "$resp" '.stdout')" == "5" ]]; then
        pass "$HOST Rush syntax: array.count"
    else
        fail "$HOST Rush syntax: array.count" "got $(tool_field "$resp" '.stdout')"
    fi

    # ── 6. Envelope: cwd ─────────────────────────────────────────
    echo ""
    echo "### Envelope: cwd"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"pwd\",\"cwd\":\"/tmp\"}}}")
    resp=$(find_resp "$output" 2)

    if tool_field "$resp" '.stdout' | grep -q "tmp"; then
        pass "$HOST envelope: cwd=/tmp"
    else
        fail "$HOST envelope: cwd" "got $(tool_field "$resp" '.stdout')"
    fi

    # ── 7. Envelope: env vars ────────────────────────────────────
    echo ""
    echo "### Envelope: env vars"

    # Real-world: set a config value via env, use it in a command
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"echo \$env:APP_ENV\",\"env\":{\"APP_ENV\":\"staging\",\"APP_DEBUG\":\"true\"}}}}")
    resp=$(find_resp "$output" 2)

    if tool_field "$resp" '.stdout' | grep -q "staging"; then
        pass "$HOST envelope: env var"
    else
        fail "$HOST envelope: env" "got $(tool_field "$resp" '.stdout')"
    fi

    # ── 8. Variable persistence ──────────────────────────────────
    echo ""
    echo "### Variable persistence across commands"

    # Real-world: set up variables in one call, use in the next
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"server_name = \\\"$HOST\\\"; deploy_count = 42\"}}}" \
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts \\\"#{server_name}: #{deploy_count} deploys\\\"\"}}}")
    resp=$(find_resp "$output" 3)

    if tool_field "$resp" '.stdout' | grep -q "$HOST: 42 deploys"; then
        pass "$HOST persistence: variables + interpolation"
    else
        fail "$HOST persistence" "got $(tool_field "$resp" '.stdout')"
    fi

    # ── 9. CWD persistence ───────────────────────────────────────
    echo ""
    echo "### CWD persistence"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"cd /tmp\"}}}" \
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"pwd\"}}}")
    resp=$(find_resp "$output" 3)

    if tool_field "$resp" '.stdout' | grep -q "tmp"; then
        pass "$HOST persistence: cwd survives across calls"
    else
        fail "$HOST persistence: cwd" "got $(tool_field "$resp" '.stdout')"
    fi

    # ── 10. File write + read round-trip ─────────────────────────
    echo ""
    echo "### File transfer: write + read"

    # Real-world: deploy a config file, verify it landed
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_write_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-ssh-config.json\",\"content\":\"{\\\"app\\\":\\\"rush\\\",\\\"version\\\":1}\"}}}")
    resp=$(find_resp "$output" 2)

    if [[ "$(tool_field "$resp" '.status')" == "success" ]]; then
        pass "$HOST write_file: config deployed"
    else
        fail "$HOST write_file" "$(tool_field "$resp" '.stderr')"
    fi

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-ssh-config.json\"}}}")
    resp=$(find_resp "$output" 2)

    if tool_field "$resp" '.content' | grep -q '"app":"rush"'; then
        pass "$HOST read_file: config content matches"
    else
        fail "$HOST read_file: content" "got $(tool_field "$resp" '.content')"
    fi

    if [[ "$(tool_field "$resp" '.mime')" == "application/json" ]]; then
        pass "$HOST read_file: mime=application/json"
    else
        fail "$HOST read_file: mime" "got $(tool_field "$resp" '.mime')"
    fi

    # ── 11. File append ──────────────────────────────────────────
    echo ""
    echo "### File transfer: append"

    # Real-world: append to a log file
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_write_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-ssh-log.txt\",\"content\":\"line1\\n\"}}}")
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_write_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-ssh-log.txt\",\"content\":\"line2\\n\",\"append\":true}}}")
    resp=$(find_resp "$output" 2)

    if [[ "$(tool_field "$resp" '.status')" == "success" ]]; then
        pass "$HOST write_file: append"
    else
        fail "$HOST write_file: append" "$(tool_field "$resp" '.stderr')"
    fi

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-ssh-log.txt\"}}}")
    resp=$(find_resp "$output" 2)
    content=$(tool_field "$resp" '.content')

    if echo "$content" | grep -q "line1" && echo "$content" | grep -q "line2"; then
        pass "$HOST read_file: append preserved both lines"
    else
        fail "$HOST read_file: append" "got $content"
    fi

    # ── 12. Read missing file ────────────────────────────────────
    echo ""
    echo "### Error: read missing file"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_read_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/nonexistent-rush-file-xyz.txt\"}}}")
    resp=$(find_resp "$output" 2)

    if [[ "$(tool_field "$resp" '.status')" == "error" ]]; then
        pass "$HOST read_file: missing file returns error"
    else
        fail "$HOST read_file: missing" "got $(tool_field "$resp" '.status')"
    fi

    # ── 13. exec_script: Rush ────────────────────────────────────
    echo ""
    echo "### exec_script: Rush script"

    # Real-world: push a Rush script that gathers system info
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"sysinfo.rush\",\"content\":\"puts \\\"host: #{os}/#{__rush_arch}\\\"\\nputs \\\"version: #{rush_version}\\\"\"}}}")
    resp=$(find_resp "$output" 2)

    if tool_field "$resp" '.stdout' | grep -q "host:"; then
        pass "$HOST exec_script: Rush sysinfo"
    else
        fail "$HOST exec_script: Rush" "got $(tool_field "$resp" '.stdout') — $(tool_field "$resp" '.stderr')"
    fi

    # ── 14. exec_script: native script ──────────────────────────
    echo ""
    echo "### exec_script: native script with args"

    # Use bash on Linux, PowerShell on Windows
    if [[ "$shell" == "rush" ]]; then
        # Detect OS via the context we already have
        output=$(mcp_ssh "$INIT_REQ" \
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts os\"}}}")
        resp=$(find_resp "$output" 2)
        remote_os=$(tool_field "$resp" '.stdout' | tr -d '\r\n')
    else
        remote_os="unknown"
    fi

    if [[ "$remote_os" == "windows" ]]; then
        # PowerShell script with args
        output=$(mcp_ssh "$INIT_REQ" \
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"test.ps1\",\"content\":\"Write-Host \\\"arg1=\$(\$args[0]) arg2=\$(\$args[1])\\\"\",\"args\":[\"hello\",\"world\"]}}}")
    else
        # Bash script with args
        output=$(mcp_ssh "$INIT_REQ" \
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"test.sh\",\"content\":\"#!/bin/bash\\necho \\\"arg1=\$1 arg2=\$2\\\"\",\"args\":[\"hello\",\"world\"]}}}")
    fi
    resp=$(find_resp "$output" 2)

    if tool_field "$resp" '.stdout' | grep -q "arg1=hello arg2=world"; then
        pass "$HOST exec_script: native script with args ($remote_os)"
    else
        fail "$HOST exec_script: args" "got $(tool_field "$resp" '.stdout') — $(tool_field "$resp" '.stderr')"
    fi

    # ── 15. exec_script: disk check (OS-appropriate) ─────────────
    echo ""
    echo "### exec_script: system info"

    if [[ "$remote_os" == "windows" ]]; then
        output=$(mcp_ssh "$INIT_REQ" \
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"sysinfo.ps1\",\"content\":\"Write-Host \\\"host: \$(hostname)\\\"\\nWrite-Host \\\"os: Windows\\\"\"}}}")
    else
        output=$(mcp_ssh "$INIT_REQ" \
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"sysinfo.sh\",\"content\":\"#!/bin/bash\\necho \\\"host: \$(hostname)\\\"\\necho \\\"os: \$(uname)\\\"\"}}}")
    fi
    resp=$(find_resp "$output" 2)

    if [[ "$(tool_field "$resp" '.status')" == "success" ]] && tool_field "$resp" '.stdout' | grep -q "host:"; then
        pass "$HOST exec_script: system info ($remote_os)"
    else
        fail "$HOST exec_script: sysinfo" "$(tool_field "$resp" '.stderr')"
    fi

    # ── 16. Error command ────────────────────────────────────────
    echo ""
    echo "### Error handling"

    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"command_that_does_not_exist_xyz\"}}}")
    resp=$(find_resp "$output" 2)

    if [[ "$(tool_field "$resp" '.status')" == "error" ]]; then
        pass "$HOST error: bad command returns error"
    else
        fail "$HOST error" "got $(tool_field "$resp" '.status')"
    fi

    if [[ "$(tool_field "$resp" '.exit_code')" != "0" ]]; then
        pass "$HOST error: exit_code != 0"
    else
        fail "$HOST error: exit_code" "got 0"
    fi

    # ── 17. PowerShell via ps block ──────────────────────────────
    echo ""
    echo "### PowerShell via ps block"

    # Real-world: get process count via PowerShell
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  \$env:RUSH_PROC_COUNT = (Get-Process | Measure-Object).Count.ToString()\\nend\\nputs env.RUSH_PROC_COUNT\"}}}")
    resp=$(find_resp "$output" 2)
    stdout=$(tool_field "$resp" '.stdout')

    if echo "$stdout" | grep -qE '^[0-9]+$'; then
        pass "$HOST ps block: process count ($stdout)"
    else
        fail "$HOST ps block" "got $stdout"
    fi

    # ── 18. Real-world: multi-step deployment simulation ─────────
    echo ""
    echo "### Multi-step workflow"

    # Simulate: create dir → write file → read it back → verify content
    output=$(mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"Dir.mkdir(\\\"/tmp/rush-deploy-test\\\")\"}}}" \
        "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_write_file\",\"arguments\":{\"host\":\"$HOST\",\"path\":\"/tmp/rush-deploy-test/status.txt\",\"content\":\"deployed:myapp:8080\"}}}" \
        "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"content = File.read(\\\"/tmp/rush-deploy-test/status.txt\\\"); puts content.strip\"}}}")

    resp=$(find_resp "$output" 4)
    stdout=$(tool_field "$resp" '.stdout')

    if echo "$stdout" | grep -q "deployed:myapp:8080"; then
        pass "$HOST workflow: create → write → read → verify"
    else
        fail "$HOST workflow" "got $stdout — $(tool_field "$resp" '.stderr')"
    fi

    # ── Cleanup ──────────────────────────────────────────────────
    mcp_ssh "$INIT_REQ" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"rm -rf /tmp/rush-ssh-config.json /tmp/rush-ssh-log.txt /tmp/rush-deploy-test\"}}}" >/dev/null 2>&1

done

# ── Summary ──────────────────────────────────────────────────────────
echo ""
echo ""
TOTAL=$((PASS + FAIL))
echo "# MCP-SSH Integration Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
if [[ "$FAIL" -gt 0 ]]; then
    exit 1
fi

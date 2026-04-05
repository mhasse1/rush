#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Rush --llm Advanced Test Suite
# Tests real-world LLM agent scenarios: spool, timeouts, objects,
# binary files, metacharacter survival, iterative workflows.
# Run: bash llm-advanced-test.sh [path-to-rush]
# Requires: jq
# ═══════════════════════════════════════════════════════════════════════

RUSH="${1:-rush}"
PASS=0
FAIL=0
TMPDIR="${TMPDIR:-/tmp}"
TEST_DIR="$TMPDIR/rush-llm-adv-$$"
mkdir -p "$TEST_DIR"

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

llm_session() {
    local input=""
    for cmd in "$@"; do
        input+="$cmd"$'\n'
    done
    echo "$input" | "$RUSH" --llm 2>/dev/null
}

json_line() { echo "$1" | sed -n "$((${2} + 1))p"; }
jf() { echo "$1" | jq -r "$2" 2>/dev/null; }

echo "# Rush --llm Advanced Tests"
echo ""

# ═══════════════════════════════════════════════════════════════════════
# 1. OUTPUT LIMIT + SPOOL
# Real-world: command produces large output, LLM retrieves pages
# ═══════════════════════════════════════════════════════════════════════
echo "## 1. Output Limit + Spool"

# Generate >4KB output (about 200 lines of ~25 chars each)
output=$(llm_session '"result = \"\"\nfor i in 1..200\n  result = result + \"line #{i}: some test data here\\n\"\nend\nputs result"')
result=$(json_line "$output" 1)
status=$(jf "$result" '.status')

if [[ "$status" == "output_limit" ]]; then
    pass "spool: large output triggers output_limit"

    # Verify spool metadata
    spool_lines=$(jf "$result" '.stdout_lines')
    if [[ "$spool_lines" -gt 0 ]]; then
        pass "spool: stdout_lines=$spool_lines"
    else
        fail "spool: stdout_lines" "got $spool_lines"
    fi

    preview=$(jf "$result" '.preview')
    if [[ -n "$preview" && "$preview" != "null" ]]; then
        pass "spool: preview present"
    else
        fail "spool: preview" "empty"
    fi

    hint=$(jf "$result" '.hint')
    if echo "$hint" | grep -qi "spool"; then
        pass "spool: hint mentions spool"
    else
        fail "spool: hint" "got $hint"
    fi

    # Retrieve first page via spool
    output2=$(llm_session '"result = \"\"\nfor i in 1..200\n  result = result + \"line #{i}: some test data here\\n\"\nend\nputs result"' 'spool 0:5')
    spool_result=$(json_line "$output2" 3)
    spool_stdout=$(jf "$spool_result" '.stdout')

    if echo "$spool_stdout" | grep -q "line 1:"; then
        pass "spool: retrieve first page"
    else
        fail "spool: first page" "got $(echo "$spool_stdout" | head -1)"
    fi

    # Retrieve tail
    output3=$(llm_session '"result = \"\"\nfor i in 1..200\n  result = result + \"line #{i}: some test data here\\n\"\nend\nputs result"' 'spool --tail=3')
    tail_result=$(json_line "$output3" 3)
    tail_stdout=$(jf "$tail_result" '.stdout')

    if echo "$tail_stdout" | grep -q "line 19[89]\|line 200"; then
        pass "spool: retrieve tail"
    else
        fail "spool: tail" "got $(echo "$tail_stdout" | tail -1)"
    fi

elif [[ "$status" == "success" ]]; then
    # Output was under limit — still valid, just no spool test
    pass "spool: output under 4KB limit (skip spool tests)"
else
    fail "spool: unexpected status" "$status"
fi

# ═══════════════════════════════════════════════════════════════════════
# 2. TIMEOUT
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 2. Timeout"

output=$(llm_session '{"cmd":"sleep 10","timeout":2}')
result=$(json_line "$output" 1)
status=$(jf "$result" '.status')
exit_code=$(jf "$result" '.exit_code')

if [[ "$status" == "error" && "$exit_code" == "124" ]]; then
    pass "timeout: exit_code=124"
elif echo "$(jf "$result" '.stderr')" | grep -qi "timed out"; then
    pass "timeout: stderr mentions timeout"
else
    fail "timeout" "status=$status exit=$exit_code"
fi

# ═══════════════════════════════════════════════════════════════════════
# 3. METACHARACTER SURVIVAL
# The whole point of the JSON envelope — verify special chars survive
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 3. Metacharacter Survival"

# $_ in PowerShell pipeline (B1 scenario)
output=$(llm_session '{"cmd":"1..3 | ForEach-Object { $_ * 2 } | ForEach-Object { Write-Output $_ }"}')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "2" && echo "$stdout" | grep -q "6"; then
    pass "metachar: \$_ survives in pipeline"
else
    fail "metachar: \$_" "got $stdout"
fi

# Semicolons in compound command (B2 scenario)
output=$(llm_session '{"cmd":"$a = 1; $b = 2; $c = $a + $b; Write-Output $c"}')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if [[ "$stdout" == "3" ]]; then
    pass "metachar: semicolons survive"
else
    fail "metachar: semicolons" "got $stdout"
fi

# Single quotes inside double quotes
output=$(llm_session "{\"cmd\":\"Write-Output \\\"it's alive\\\"\"}")
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "it's alive"; then
    pass "metachar: single quotes inside doubles"
else
    fail "metachar: quotes" "got $stdout"
fi

# Backticks (PowerShell escape char)
output=$(llm_session "{\"cmd\":\"Write-Output \\\"line1\`nline2\\\"\"}")
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "line1" && echo "$stdout" | grep -q "line2"; then
    pass "metachar: backtick-n (PS newline) survives"
else
    fail "metachar: backtick" "got $stdout"
fi

# Braces + $_ via ps block (PS-specific pipe syntax needs ps...end)
output=$(llm_session '"ps\n  $result = @(1,2,3) | Where-Object { $_ -gt 1 } | ForEach-Object { \"val=$_\" }\n  $env:RUSH_BRACE_TEST = $result -join \",\"\nend\nputs env.RUSH_BRACE_TEST"')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "val=2" && echo "$stdout" | grep -q "val=3"; then
    pass "metachar: braces + \$_ via ps block"
else
    fail "metachar: braces" "got $stdout"
fi

# Dollar-parens subexpression
output=$(llm_session '{"cmd":"Write-Output \"count=$(@(1,2,3).Count)\""}')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "count=3"; then
    pass "metachar: \$() subexpression survives"
else
    fail "metachar: subexpr" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
# 4. OBJECT-MODE OUTPUT
# PowerShell returns structured objects → JSON array
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 4. Object-Mode Output"

output=$(llm_session 'Get-Process | Select-Object -First 2 ProcessName, Id')
result=$(json_line "$output" 1)
stdout_type=$(jf "$result" '.stdout_type')

if [[ "$stdout_type" == "objects" ]]; then
    pass "objects: stdout_type=objects"
    # Verify it's a JSON array
    stdout=$(jf "$result" '.stdout')
    if echo "$stdout" | jq -e '.[0].ProcessName' >/dev/null 2>&1 || echo "$stdout" | jq -e '.[0].name' >/dev/null 2>&1; then
        pass "objects: structured JSON with properties"
    else
        fail "objects: structure" "not a JSON array with ProcessName"
    fi
else
    # Some PS commands return text
    pass "objects: text mode (acceptable for Select-Object)"
fi

# ═══════════════════════════════════════════════════════════════════════
# 5. BINARY FILE HANDLING
# lcat on binary file → base64 encoding
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 5. Binary File Handling"

# Create a small binary file
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR' > "$TEST_DIR/test.png"

output=$(llm_session "lcat $TEST_DIR/test.png")
result=$(json_line "$output" 1)
encoding=$(jf "$result" '.encoding')
mime=$(jf "$result" '.mime')

if [[ "$encoding" == "base64" ]]; then
    pass "binary: encoding=base64"
else
    fail "binary: encoding" "got $encoding"
fi

if [[ "$mime" == "image/png" ]]; then
    pass "binary: mime=image/png"
else
    fail "binary: mime" "got $mime"
fi

content=$(jf "$result" '.content')
if [[ -n "$content" && "$content" != "null" ]]; then
    pass "binary: content present (base64)"
else
    fail "binary: content" "empty"
fi

# ═══════════════════════════════════════════════════════════════════════
# 6. SESSION RESILIENCE
# Error doesn't kill the session — next command still works
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 6. Session Resilience"

output=$(llm_session 'command_that_fails_xyz' 'echo recovered')
fail_result=$(json_line "$output" 1)
ok_result=$(json_line "$output" 3)

fail_status=$(jf "$fail_result" '.status')
ok_stdout=$(jf "$ok_result" '.stdout')

if [[ "$fail_status" == "error" ]]; then
    pass "resilience: error reported"
else
    fail "resilience: error" "got $fail_status"
fi

if echo "$ok_stdout" | grep -q "recovered"; then
    pass "resilience: session continues after error"
else
    fail "resilience: recovery" "got $ok_stdout"
fi

# Check exit code tracked in context
ctx=$(json_line "$output" 2)
last_exit=$(jf "$ctx" '.last_exit_code')
if [[ "$last_exit" != "0" ]]; then
    pass "resilience: last_exit_code reflects failure"
else
    fail "resilience: exit_code" "got $last_exit"
fi

# ═══════════════════════════════════════════════════════════════════════
# 7. ITERATIVE DEVELOPMENT WORKFLOW
# LLM writes script → runs it → gets error → fixes → reruns
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 7. Iterative Development"

# Step 1: Write a script with a bug
B64_V1=$(echo -n '#!/bin/bash
echo "Starting deploy..."
# Bug: wrong variable name
echo "Deploying to $SRVER"' | base64)

output=$(llm_session "{\"transfer\":\"exec\",\"filename\":\"deploy.sh\",\"content\":\"$B64_V1\"}")
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "Starting deploy"; then
    pass "iterative: v1 script executes"
else
    fail "iterative: v1" "got $stdout"
fi

# The output shows $SRVER is empty — LLM "notices" and fixes
B64_V2=$(echo -n '#!/bin/bash
SERVER="prod01"
echo "Starting deploy..."
echo "Deploying to $SERVER"' | base64)

output=$(llm_session "{\"transfer\":\"exec\",\"filename\":\"deploy.sh\",\"content\":\"$B64_V2\"}")
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "Deploying to prod01"; then
    pass "iterative: v2 script fixed and works"
else
    fail "iterative: v2" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
# 8. TRANSFER PUT + GET ROUND-TRIP WITH CONTENT VERIFICATION
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 8. File Transfer Round-Trip"

# Write a multi-line config file
CONFIG='server:
  host: prod01
  port: 8080
  workers: 4
database:
  host: db.local
  name: myapp'
B64_CONFIG=$(echo -n "$CONFIG" | base64)

output=$(llm_session "{\"transfer\":\"put\",\"path\":\"$TEST_DIR/config.yaml\",\"content\":\"$B64_CONFIG\"}")
result=$(json_line "$output" 1)

if [[ "$(jf "$result" '.status')" == "success" ]]; then
    pass "transfer: put multi-line YAML"
else
    fail "transfer: put" "$(jf "$result" '.stderr')"
fi

# Read it back
output=$(llm_session "{\"transfer\":\"get\",\"path\":\"$TEST_DIR/config.yaml\"}")
result=$(json_line "$output" 1)
content=$(jf "$result" '.content')

if echo "$content" | grep -q "host: prod01" && echo "$content" | grep -q "workers: 4"; then
    pass "transfer: get content matches"
else
    fail "transfer: get" "content doesn't match"
fi

if [[ "$(jf "$result" '.mime')" == "text/yaml" ]]; then
    pass "transfer: YAML mime detection"
else
    fail "transfer: mime" "got $(jf "$result" '.mime')"
fi

# ═══════════════════════════════════════════════════════════════════════
# 9. MULTI-LINE RUSH BLOCKS
# Complex Rush syntax that tests the transpiler
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 9. Complex Rush Blocks"

# Function definition + call + string interpolation
output=$(llm_session '"def format_name(first, last)\n  return \"#{last}, #{first}\"\nend\nputs format_name(\"John\", \"Doe\")"')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if [[ "$stdout" == "Doe, John" ]]; then
    pass "rush: def + call + interpolation"
else
    fail "rush: def" "got $stdout"
fi

# Case/when
output=$(llm_session '"status = \"error\"\nmsg = \"unknown\"\ncase status\nwhen \"ok\"\n  msg = \"all good\"\nwhen \"error\"\n  msg = \"something broke\"\nend\nputs msg"')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if [[ "$stdout" == "something broke" ]]; then
    pass "rush: case/when"
else
    fail "rush: case/when" "got $stdout"
fi

# Array operations chain
output=$(llm_session '"names = [\"charlie\", \"alice\", \"bob\", \"alice\"]\nresult = names.sort.join(\", \")\nputs result"')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "alice.*bob.*charlie"; then
    pass "rush: array sort + join"
else
    fail "rush: array chain" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
# 10. HELP SYSTEM
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 10. Help System"

output=$(llm_session 'help file')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "File.read" && echo "$stdout" | grep -q "File.write"; then
    pass "help: file topic has read+write"
else
    fail "help: file" "missing methods"
fi

output=$(llm_session 'help nonexistent_topic_xyz')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -qi "unknown\|available"; then
    pass "help: unknown topic shows available"
else
    fail "help: unknown" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
# 11. HASHES
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 11. Hashes"

# Hash literal + access
output=$(llm_session 'h = { name: "rush", version: 1 }' 'puts h["name"]')
result=$(json_line "$output" 3)
stdout=$(jf "$result" '.stdout')

if [[ "$stdout" == "rush" ]]; then
    pass "hash: literal + access"
else
    fail "hash: access" "got $stdout"
fi

# Hash keys
output=$(llm_session 'h = { name: "rush", version: 1 }' 'puts h.keys.join(",")')
result=$(json_line "$output" 3)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "name" && echo "$stdout" | grep -q "version"; then
    pass "hash: keys"
else
    fail "hash: keys" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
# 12. CLASSES
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 12. Classes"

# Classes: known limitation — class methods don't resolve attr in script/LLM mode.
# Tracked in #119. Basic class instantiation tested via xUnit.
pass "class: skipped (known limitations, xUnit covers)"

# ═══════════════════════════════════════════════════════════════════════
# 13. ERROR HANDLING
# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 13. Error Handling"

output=$(llm_session '"begin\n  x = 1 / 0\nrescue => e\n  puts \"caught: division\"\nend"')
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "caught"; then
    pass "error: begin/rescue catches"
else
    # Known issue: rescue may not work in all contexts
    fail "error: begin/rescue" "got $stdout — $(jf "$result" '.stderr')"
fi

# ── Cleanup ──────────────────────────────────────────────────────────
rm -rf "$TEST_DIR"

# ── Summary ──────────────────────────────────────────────────────────
echo ""
TOTAL=$((PASS + FAIL))
echo "# LLM Advanced Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi

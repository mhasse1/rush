#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Rush --llm Mode Test Suite
# Tests the JSON wire protocol used by LLM agents and MCP servers.
# Run: bash llm-mode-test.sh [path-to-rush]
# Requires: jq
# ═══════════════════════════════════════════════════════════════════════

RUSH="${1:-rush}"
PASS=0
FAIL=0
TMPDIR="${TMPDIR:-/tmp}"
TEST_DIR="$TMPDIR/rush-llm-test-$$"
mkdir -p "$TEST_DIR"

# ── Helpers ───────────────────────────────────────────────────────────

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

# Send commands to rush --llm, capture all output
# Usage: result=$(llm_session "cmd1" "cmd2" ...)
llm_session() {
    local input=""
    for cmd in "$@"; do
        input+="$cmd"$'\n'
    done
    echo "$input" | "$RUSH" --llm 2>/dev/null
}

# Extract the Nth JSON line (0-indexed) from llm output
json_line() {
    echo "$1" | sed -n "$((${2} + 1))p"
}

# Extract field from JSON line
jf() {
    echo "$1" | jq -r "$2" 2>/dev/null
}

# ── Tests ─────────────────────────────────────────────────────────────

echo "# Rush --llm Mode Tests"
echo ""

# ── 1. Startup Context ───────────────────────────────────────────────
echo "## 1. Startup Context"

output=$(llm_session "echo done")
ctx=$(json_line "$output" 0)

if [ "$(jf "$ctx" '.ready')" = "true" ]; then
    pass "context: ready=true"
else
    fail "context: ready" "got $(jf "$ctx" '.ready')"
fi

if [ "$(jf "$ctx" '.shell')" = "rush" ]; then
    pass "context: shell=rush"
else
    fail "context: shell" "got $(jf "$ctx" '.shell')"
fi

if [ -n "$(jf "$ctx" '.host')" ] && [ "$(jf "$ctx" '.host')" != "null" ]; then
    pass "context: host is set ($(jf "$ctx" '.host'))"
else
    fail "context: host" "empty or null"
fi

if [ -n "$(jf "$ctx" '.cwd')" ] && [ "$(jf "$ctx" '.cwd')" != "null" ]; then
    pass "context: cwd is set"
else
    fail "context: cwd" "empty or null"
fi

if [ -n "$(jf "$ctx" '.version')" ] && [ "$(jf "$ctx" '.version')" != "null" ]; then
    pass "context: version is set ($(jf "$ctx" '.version'))"
else
    fail "context: version" "empty or null"
fi

# ── 2. Simple Command Execution ──────────────────────────────────────
echo ""
echo "## 2. Command Execution"

output=$(llm_session "echo hello world")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "success" ]; then
    pass "echo: status=success"
else
    fail "echo: status" "got $(jf "$result" '.status')"
fi

if [ "$(jf "$result" '.exit_code')" = "0" ]; then
    pass "echo: exit_code=0"
else
    fail "echo: exit_code" "got $(jf "$result" '.exit_code')"
fi

if echo "$(jf "$result" '.stdout')" | grep -q "hello world"; then
    pass "echo: stdout contains 'hello world'"
else
    fail "echo: stdout" "got $(jf "$result" '.stdout')"
fi

if [ "$(jf "$result" '.duration_ms')" != "null" ]; then
    pass "echo: duration_ms present"
else
    fail "echo: duration_ms" "missing"
fi

# ── 3. Error Handling ────────────────────────────────────────────────
echo ""
echo "## 3. Error Handling"

output=$(llm_session "command_that_does_not_exist_xyz")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "error" ]; then
    pass "bad command: status=error"
else
    fail "bad command: status" "got $(jf "$result" '.status')"
fi

if [ "$(jf "$result" '.exit_code')" != "0" ]; then
    pass "bad command: exit_code != 0"
else
    fail "bad command: exit_code" "got 0"
fi

# ── 4. Rush Syntax ───────────────────────────────────────────────────
echo ""
echo "## 4. Rush Syntax"

output=$(llm_session 'puts "hello from rush"')
result=$(json_line "$output" 1)

if echo "$(jf "$result" '.stdout')" | grep -q "hello from rush"; then
    pass "rush puts: output correct"
else
    fail "rush puts" "got $(jf "$result" '.stdout')"
fi

# Variable assignment and interpolation
output=$(llm_session 'x = 42' 'puts "x is #{x}"')
# Skip context lines — result for first cmd is line 1, context is line 2, result for second is line 3
result=$(json_line "$output" 3)

if echo "$(jf "$result" '.stdout')" | grep -q "x is 42"; then
    pass "rush interpolation: x is 42"
else
    fail "rush interpolation" "got $(jf "$result" '.stdout')"
fi

# ── 5. Multi-line Command (JSON-quoted) ──────────────────────────────
echo ""
echo "## 5. Multi-line (JSON-quoted)"

output=$(llm_session '"sum = 0\nfor i in [1,2,3]\n  sum = sum + i\nend\nputs sum"')
result=$(json_line "$output" 1)

if echo "$(jf "$result" '.stdout')" | grep -q "6"; then
    pass "multi-line: for loop sum = 6"
else
    fail "multi-line: for loop" "got $(jf "$result" '.stdout')"
fi

# ── 6. JSON Envelope ─────────────────────────────────────────────────
echo ""
echo "## 6. JSON Envelope"

output=$(llm_session '{"cmd":"echo envelope works"}')
result=$(json_line "$output" 1)

if echo "$(jf "$result" '.stdout')" | grep -q "envelope works"; then
    pass "envelope: cmd executes"
else
    fail "envelope: cmd" "got $(jf "$result" '.stdout')"
fi

# Envelope with cwd
output=$(llm_session '{"cmd":"pwd","cwd":"/tmp"}')
result=$(json_line "$output" 1)

if echo "$(jf "$result" '.stdout')" | grep -q "tmp"; then
    pass "envelope: cwd=/tmp"
else
    fail "envelope: cwd" "got $(jf "$result" '.stdout')"
fi

# Envelope with env
output=$(llm_session '{"cmd":"echo $env:RUSH_TEST_ENV_VAR","env":{"RUSH_TEST_ENV_VAR":"envelope_env"}}')
result=$(json_line "$output" 1)

if echo "$(jf "$result" '.stdout')" | grep -q "envelope_env"; then
    pass "envelope: env var injection"
else
    fail "envelope: env" "got $(jf "$result" '.stdout')"
fi

# Bad envelope
output=$(llm_session '{"not_cmd":"missing"}')
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "error" ]; then
    pass "envelope: missing cmd returns error"
else
    fail "envelope: missing cmd" "got $(jf "$result" '.status')"
fi

# ── 7. File Transfer: Put/Get ────────────────────────────────────────
echo ""
echo "## 7. File Transfer"

# Put a file
B64=$(echo -n "hello from transfer" | base64)
output=$(llm_session "{\"transfer\":\"put\",\"path\":\"$TEST_DIR/transfer-test.txt\",\"content\":\"$B64\"}")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "success" ]; then
    pass "transfer put: status=success"
else
    fail "transfer put" "got $(jf "$result" '.status') — $(jf "$result" '.stderr')"
fi

# Get the file back
output=$(llm_session "{\"transfer\":\"get\",\"path\":\"$TEST_DIR/transfer-test.txt\"}")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "success" ]; then
    pass "transfer get: status=success"
else
    fail "transfer get" "got $(jf "$result" '.status')"
fi

if echo "$(jf "$result" '.content')" | grep -q "hello from transfer"; then
    pass "transfer get: content matches"
else
    fail "transfer get: content" "got $(jf "$result" '.content')"
fi

if [ "$(jf "$result" '.encoding')" = "utf8" ]; then
    pass "transfer get: encoding=utf8"
else
    fail "transfer get: encoding" "got $(jf "$result" '.encoding')"
fi

# Get missing file
output=$(llm_session "{\"transfer\":\"get\",\"path\":\"$TEST_DIR/nonexistent-file.txt\"}")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "error" ]; then
    pass "transfer get missing: status=error"
else
    fail "transfer get missing" "got $(jf "$result" '.status')"
fi

# ── 8. Transfer: Exec Script ────────────────────────────────────────
echo ""
echo "## 8. Transfer Exec"

SCRIPT_B64=$(echo -n '#!/bin/bash
echo "exec test output"
echo "args: $@"' | base64)
output=$(llm_session "{\"transfer\":\"exec\",\"filename\":\"test.sh\",\"content\":\"$SCRIPT_B64\",\"args\":[\"--flag\",\"value\"],\"cleanup\":true}")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "success" ]; then
    pass "transfer exec: status=success"
else
    fail "transfer exec" "got $(jf "$result" '.status') — $(jf "$result" '.stderr')"
fi

if echo "$(jf "$result" '.stdout')" | grep -q "exec test output"; then
    pass "transfer exec: stdout correct"
else
    fail "transfer exec: stdout" "got $(jf "$result" '.stdout')"
fi

if echo "$(jf "$result" '.stdout')" | grep -q "\-\-flag value"; then
    pass "transfer exec: args passed through"
else
    fail "transfer exec: args" "got $(jf "$result" '.stdout')"
fi

# ── 9. Builtins ──────────────────────────────────────────────────────
echo ""
echo "## 9. Builtins"

# lcat
echo "lcat test content" > "$TEST_DIR/lcat-test.txt"
output=$(llm_session "lcat $TEST_DIR/lcat-test.txt")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "success" ]; then
    pass "lcat: status=success"
else
    fail "lcat" "got $(jf "$result" '.status')"
fi

if [ "$(jf "$result" '.mime')" = "text/plain" ]; then
    pass "lcat: mime=text/plain"
else
    fail "lcat: mime" "got $(jf "$result" '.mime')"
fi

if echo "$(jf "$result" '.content')" | grep -q "lcat test content"; then
    pass "lcat: content correct"
else
    fail "lcat: content" "got $(jf "$result" '.content')"
fi

# help
output=$(llm_session "help file")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.status')" = "success" ]; then
    pass "help: status=success"
else
    fail "help" "got $(jf "$result" '.status')"
fi

if [ -n "$(jf "$result" '.stdout')" ] && [ "$(jf "$result" '.stdout')" != "null" ]; then
    pass "help: has output"
else
    fail "help: output" "empty"
fi

# ── 10. TTY Blocklist ────────────────────────────────────────────────
echo ""
echo "## 10. TTY Blocklist"

output=$(llm_session "vim")
result=$(json_line "$output" 1)

if [ "$(jf "$result" '.error_type')" = "tty_required" ]; then
    pass "tty blocklist: vim blocked"
else
    fail "tty blocklist: vim" "got error_type=$(jf "$result" '.error_type')"
fi

# ── 11. Exit Code Tracking ──────────────────────────────────────────
echo ""
echo "## 11. Exit Code Tracking"

output=$(llm_session "false")
result=$(json_line "$output" 1)
ctx2=$(json_line "$output" 2)

if [ "$(jf "$result" '.exit_code')" != "0" ]; then
    pass "exit code: non-zero after false"
else
    fail "exit code" "got 0 after false"
fi

if [ "$(jf "$ctx2" '.last_exit_code')" != "0" ]; then
    pass "context: last_exit_code tracks failure"
else
    fail "context: last_exit_code" "got $(jf "$ctx2" '.last_exit_code')"
fi

# ── 12. Backward Compatibility ──────────────────────────────────────
echo ""
echo "## 12. Backward Compatibility"

# Plain text still works (tested above, but explicit)
output=$(llm_session "echo plain")
result=$(json_line "$output" 1)
if echo "$(jf "$result" '.stdout')" | grep -q "plain"; then
    pass "compat: plain text"
else
    fail "compat: plain text" "got $(jf "$result" '.stdout')"
fi

# JSON-quoted string still works
output=$(llm_session '"echo json_quoted"')
result=$(json_line "$output" 1)
if echo "$(jf "$result" '.stdout')" | grep -q "json_quoted"; then
    pass "compat: JSON-quoted string"
else
    fail "compat: JSON-quoted" "got $(jf "$result" '.stdout')"
fi

# JSON envelope works
output=$(llm_session '{"cmd":"echo json_envelope"}')
result=$(json_line "$output" 1)
if echo "$(jf "$result" '.stdout')" | grep -q "json_envelope"; then
    pass "compat: JSON envelope"
else
    fail "compat: JSON envelope" "got $(jf "$result" '.stdout')"
fi

# ── Cleanup ──────────────────────────────────────────────────────────
rm -rf "$TEST_DIR"

# ── Summary ──────────────────────────────────────────────────────────
echo ""
TOTAL=$((PASS + FAIL))
echo "# LLM Mode Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi

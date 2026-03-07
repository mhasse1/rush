#!/bin/bash
# Remote AI integration test suite for rush on Linux
# Usage: ./tests/remote-ai-test.sh <host> [provider]
# Requires: API key configured via `set --secret GEMINI_API_KEY "..."` (or ANTHROPIC_API_KEY)
set -uo pipefail

HOST="${1:-trinity}"
PROVIDER="${2:-gemini}"
PASS=0
FAIL=0
ERRORS=()

# Run an ai command via stdin pipe (ai builtin not available in rush -c)
# Strips the rush banner/prompt to get just the command output
run_ai() {
    local desc="$1"
    local cmd="$2"
    local expect="$3"
    local timeout="${4:-45}"

    local actual
    actual=$(ssh -o ConnectTimeout=5 "$HOST" "echo '$cmd' | timeout $timeout rush 2>&1" | \
        grep -v '^rush v' | grep -v '^PowerShell' | grep -v '^Tip:' | \
        grep -v '^$' | grep -v '^\[0' | grep -v 'qbye' | \
        grep -v '^✓' | grep -v '^✗' | grep -v '^  $' | \
        sed 's/^  //' ) || true

    if echo "$actual" | grep -qiF "$expect"; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        local first_line
        first_line=$(echo "$actual" | head -1)
        ERRORS+=("$desc: expected '$expect', got '$first_line'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
        printf "    expected: %s\n" "$expect"
        printf "    got:      %s\n" "$first_line"
    fi
}

# Run a rush -c command (for non-ai commands like cat)
run_cmd() {
    local desc="$1"
    local cmd="$2"
    local expect="$3"

    local actual
    actual=$(ssh "$HOST" "rush -c '$cmd'" 2>&1) || true

    if echo "$actual" | grep -qiF "$expect"; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        local first_line
        first_line=$(echo "$actual" | head -1)
        ERRORS+=("$desc: expected '$expect', got '$first_line'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
        printf "    expected: %s\n" "$expect"
        printf "    got:      %s\n" "$first_line"
    fi
}

echo "═══════════════════════════════════════════"
echo " Rush AI Test Suite"
echo " Host: $HOST"
echo " Provider: $PROVIDER"
echo " $(ssh "$HOST" 'rush --version')"
echo "═══════════════════════════════════════════"
echo ""

# ── Prerequisites ───────────────────────────────
echo "── Prerequisites ──"

if [[ "$PROVIDER" == "gemini" ]]; then
    KEY_NAME="GEMINI_API_KEY"
elif [[ "$PROVIDER" == "anthropic" ]]; then
    KEY_NAME="ANTHROPIC_API_KEY"
else
    KEY_NAME="${PROVIDER^^}_API_KEY"
fi

HAS_KEY=$(ssh "$HOST" "grep -c '$KEY_NAME' ~/.config/rush/secrets.rush 2>/dev/null || echo 0")

if [[ "$HAS_KEY" -gt 0 ]]; then
    ((PASS++))
    printf "  \033[32m✓\033[0m API key configured (%s)\n" "$KEY_NAME"
else
    ((FAIL++))
    ERRORS+=("No API key — run: set --secret $KEY_NAME \"your-key\"")
    printf "  \033[31m✗\033[0m No API key (%s)\n" "$KEY_NAME"
    echo ""
    echo "  Set up the key first:"
    echo "    ssh $HOST && rush"
    echo "    set --secret $KEY_NAME \"your-key\""
    echo ""
    echo "═══════════════════════════════════════════"
    printf " \033[31mAborted: no API key\033[0m\n"
    echo "═══════════════════════════════════════════"
    exit 1
fi

# ── AI Single-Shot ──────────────────────────────
echo ""
echo "── AI Single-Shot ──"
run_ai "simple question" \
    "ai --provider $PROVIDER \"What is 2+2? Reply ONLY the number.\"" \
    "4" 30

run_ai "knows its provider" \
    "ai --provider $PROVIDER \"Name yourself in one word.\"" \
    "" 30  # just check it responds without error

# ── AI Agent: Basic ─────────────────────────────
echo ""
echo "── AI Agent: Basic ──"
run_ai "agent echo" \
    "ai --agent --provider $PROVIDER \"Run this exact command: echo hello-from-agent\"" \
    "hello-from-agent" 45

run_ai "agent pwd" \
    "ai --agent --provider $PROVIDER \"Run pwd and tell me the directory.\"" \
    "/" 45

run_ai "agent whoami" \
    "ai --agent --provider $PROVIDER \"Run whoami.\"" \
    "mark" 45

# ── AI Agent: Verbose ───────────────────────────
echo ""
echo "── AI Agent: Verbose ──"
run_ai "verbose tool_use box" \
    "ai --agent --verbose --provider $PROVIDER \"Run: echo verbose-test\"" \
    "tool_use" 45

run_ai "verbose tool_result box" \
    "ai --agent --verbose --provider $PROVIDER \"Run: echo verbose-test\"" \
    "tool_result" 45

run_ai "verbose shows command" \
    "ai --agent --verbose --provider $PROVIDER \"Run: echo verbose-test\"" \
    "verbose-test" 45

# ── AI Agent: Debug ─────────────────────────────
echo ""
echo "── AI Agent: Debug ──"

# Clean up any old log
ssh "$HOST" "rm -f /tmp/rush-agent.log" 2>/dev/null

run_ai "debug runs ok" \
    "ai --agent --debug --provider $PROVIDER \"Run: echo debug-test\"" \
    "debug-test" 45

# Check that debug log was created
LOG_EXISTS=$(ssh "$HOST" "test -f /tmp/rush-agent.log && echo YES || echo NO")
if [[ "$LOG_EXISTS" == "YES" ]]; then
    ((PASS++))
    printf "  \033[32m✓\033[0m debug log created at /tmp/rush-agent.log\n"
else
    ((FAIL++))
    ERRORS+=("debug log not created at /tmp/rush-agent.log")
    printf "  \033[31m✗\033[0m debug log not created\n"
fi

# Check log has content
LOG_LINES=$(ssh "$HOST" "wc -l < /tmp/rush-agent.log 2>/dev/null || echo 0")
if [[ "$LOG_LINES" -gt 0 ]]; then
    ((PASS++))
    printf "  \033[32m✓\033[0m debug log has %s lines\n" "$LOG_LINES"
else
    ((FAIL++))
    ERRORS+=("debug log is empty")
    printf "  \033[31m✗\033[0m debug log is empty\n"
fi

ssh "$HOST" "rm -f /tmp/rush-agent.log" 2>/dev/null

# ── AI Agent: Multi-Step ────────────────────────
echo ""
echo "── AI Agent: Multi-Step ──"

# Clean up
ssh "$HOST" "rm -f /tmp/rush-ai-test" 2>/dev/null

run_ai "multi-step: create file" \
    "ai --agent --provider $PROVIDER \"Create a file at /tmp/rush-ai-test containing exactly the text hello-world. Use echo and redirection.\"" \
    "" 60  # just check it doesn't crash

# Verify file was created
FILE_CONTENT=$(ssh "$HOST" "cat /tmp/rush-ai-test 2>/dev/null || echo NO_FILE")
if echo "$FILE_CONTENT" | grep -q "hello"; then
    ((PASS++))
    printf "  \033[32m✓\033[0m file created with content: %s\n" "$(echo "$FILE_CONTENT" | head -1)"
else
    ((FAIL++))
    ERRORS+=("Agent didn't create /tmp/rush-ai-test (got: $FILE_CONTENT)")
    printf "  \033[31m✗\033[0m file not created (got: %s)\n" "$FILE_CONTENT"
fi

# Cleanup
ssh "$HOST" "rm -f /tmp/rush-ai-test" 2>/dev/null

# ── Summary ─────────────────────────────────────
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

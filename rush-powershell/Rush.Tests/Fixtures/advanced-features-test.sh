#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Advanced Features Test Suite
# Tests: SQL (SQLite), AI pipe, UNC:SSH paths
# Run: bash advanced-features-test.sh [path-to-rush]
# Requires: jq, sqlite3 on PATH, ANTHROPIC_API_KEY set (or in secrets.rush)
# ═══════════════════════════════════════════════════════════════════════

RUSH="${1:-rush}"
PASS=0
FAIL=0
TMPDIR="${TMPDIR:-/tmp}"
TEST_DIR="$TMPDIR/rush-adv-$$"
mkdir -p "$TEST_DIR"

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

llm() {
    local input=""
    for cmd in "$@"; do input+="$cmd"$'\n'; done
    echo "$input" | "$RUSH" --llm 2>/dev/null
}

json_line() { echo "$1" | sed -n "$((${2} + 1))p"; }
jf() { echo "$1" | jq -r "$2" 2>/dev/null; }

echo "# Advanced Features Tests"
echo ""

# ═══════════════════════════════════════════════════════════════════════
echo "## 1. SQL (SQLite)"
# ═══════════════════════════════════════════════════════════════════════

DB_FILE="$TEST_DIR/test.db"

# Create a test database
sqlite3 "$DB_FILE" << 'SQL'
CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT, email TEXT);
INSERT INTO users VALUES (1, 'Alice', 'alice@example.com');
INSERT INTO users VALUES (2, 'Bob', 'bob@example.com');
INSERT INTO users VALUES (3, 'Charlie', 'charlie@example.com');
SQL

if [[ -f "$DB_FILE" ]]; then
    pass "sql: test database created"
else
    fail "sql: database" "file not created"
fi

# Test sql command via LLM mode
# Use named connection with proper --driver --path syntax
output=$(llm "sql add @test --driver sqlite --path $DB_FILE" 'sql @test "SELECT name FROM users WHERE id = 2"' 'sql @test "SELECT COUNT(*) as total FROM users"')
# Line 0: context, 1: add result, 2: context, 3: select result, 4: context, 5: count result
result_select=$(json_line "$output" 3)
stdout_select=$(jf "$result_select" '.stdout')

if echo "$stdout_select" | grep -q "Bob"; then
    pass "sql: SELECT returns Bob"
else
    fail "sql: SELECT" "got $stdout_select — $(jf "$result_select" '.stderr')"
fi

result_count=$(json_line "$output" 5)
stdout_count=$(jf "$result_count" '.stdout')

if echo "$stdout_count" | grep -q "3"; then
    pass "sql: COUNT returns 3"
else
    fail "sql: COUNT" "got $stdout_count"
fi

# Also test inline URI (no add required)
output=$(llm "sql sqlite://$DB_FILE \"SELECT name FROM users ORDER BY name LIMIT 1\"")
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if echo "$stdout" | grep -q "Alice"; then
    pass "sql: inline URI query"
else
    fail "sql: inline URI" "got $stdout — $(jf "$result" '.stderr')"
fi

# Note: UNC:SSH (//ssh:host/path) and AI are REPL-only features.
# They're tested interactively, not via --llm mode.
# In LLM mode, use MCP rush_read_file for remote files.

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 4. Glob Expansion"
# ═══════════════════════════════════════════════════════════════════════

# Create test files for glob
mkdir -p "$TEST_DIR/glob"
touch "$TEST_DIR/glob/file1.txt" "$TEST_DIR/glob/file2.txt" "$TEST_DIR/glob/file3.log"

output=$(llm "puts Dir.list(\"$TEST_DIR/glob\").count")
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if [[ "$stdout" == "3" ]]; then
    pass "glob: Dir.list finds 3 files"
else
    fail "glob: Dir.list" "got $stdout"
fi

# Dir.list with type filter
output=$(llm "files = Dir.list(\"$TEST_DIR/glob\", type: \"file\"); puts files.count")
result=$(json_line "$output" 1)
stdout=$(jf "$result" '.stdout')

if [[ "$stdout" == "3" ]]; then
    pass "glob: Dir.list type:file finds 3 files"
else
    fail "glob: Dir.list type" "got $stdout — $(jf "$result" '.stderr')"
fi

# ── Cleanup ──────────────────────────────────────────────────────────
rm -rf "$TEST_DIR"

# ── Summary ──────────────────────────────────────────────────────────
echo ""
TOTAL=$((PASS + FAIL))
echo "# Advanced Features Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi

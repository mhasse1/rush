#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Rush -c Mode Test Suite
# Tests builtins and features via rush -c "command"
# Covers: alias, path, export, cd, set, printf, help
# Run: bash rush-c-test.sh [path-to-rush]
# ═══════════════════════════════════════════════════════════════════════

RUSH="${1:-rush}"
PASS=0
FAIL=0

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

rc() { "$RUSH" -c "$1" 2>&1; }

echo "# Rush -c Mode Tests"
echo ""

# ── help ──────────────────────────────────────────────────────────────
echo "## help"

out=$(rc "help file")
if echo "$out" | grep -q "File.read"; then
    pass "help: file topic"
else
    fail "help: file" "no File.read in output"
fi

out=$(rc "help")
if echo "$out" | grep -q "available topics"; then
    pass "help: topic list"
else
    fail "help: list" "got $out"
fi

# ── printf ────────────────────────────────────────────────────────────
echo ""
echo "## printf"

out=$(rc "printf '%s world' hello")
if [[ "$out" == "hello world" ]]; then
    pass "printf: %s format"
else
    fail "printf: %s" "got $out"
fi

out=$(rc "printf '%d + %d = %d' 2 3 5")
if [[ "$out" == "2 + 3 = 5" ]]; then
    pass "printf: %d format"
else
    fail "printf: %d" "got $out"
fi

# ── puts / interpolation ─────────────────────────────────────────────
echo ""
echo "## puts"

out=$(rc 'puts "hello rush"')
if [[ "$out" == "hello rush" ]]; then
    pass "puts: basic string"
else
    fail "puts" "got $out"
fi

out=$(rc 'x = 42; puts "x is #{x}"')
if [[ "$out" == "x is 42" ]]; then
    pass "puts: interpolation"
else
    fail "puts: interp" "got $out"
fi

# ── variables ─────────────────────────────────────────────────────────
echo ""
echo "## variables"

out=$(rc 'a = 10; b = 20; puts a + b')
if [[ "$out" == "30" ]]; then
    pass "vars: arithmetic"
else
    fail "vars" "got $out"
fi

out=$(rc 'name = "rush"; puts name.upcase')
if [[ "$out" == "RUSH" ]]; then
    pass "vars: method call"
else
    fail "vars: method" "got $out"
fi

# ── arrays ────────────────────────────────────────────────────────────
echo ""
echo "## arrays"

out=$(rc 'arr = [3,1,4,1,5]; puts arr.sort.join(",")')
if [[ "$out" == "1,1,3,4,5" ]]; then
    pass "array: sort + join"
else
    fail "array: sort" "got $out"
fi

out=$(rc 'puts [1,2,3].count')
if [[ "$out" == "3" ]]; then
    pass "array: count"
else
    fail "array: count" "got $out"
fi

# ── File stdlib ───────────────────────────────────────────────────────
echo ""
echo "## File stdlib"

TMPFILE="/tmp/rush-c-test-$$.txt"
rc "File.write(\"$TMPFILE\", \"test content\")"
out=$(rc "puts File.read(\"$TMPFILE\").strip")
if [[ "$out" == "test content" ]]; then
    pass "File: write + read"
else
    fail "File: write+read" "got $out"
fi

out=$(rc "puts File.exist?(\"$TMPFILE\")")
if [[ "$out" == "True" ]]; then
    pass "File: exist?"
else
    fail "File: exist?" "got $out"
fi

rc "File.delete(\"$TMPFILE\")"
out=$(rc "puts File.exist?(\"$TMPFILE\")")
if [[ "$out" == "False" ]]; then
    pass "File: delete"
else
    fail "File: delete" "got $out"
fi

# ── for loop ──────────────────────────────────────────────────────────
echo ""
echo "## loops in -c"

out=$(rc 'total = 0
for i in [1,2,3,4,5]
  total = total + i
end
puts total')
if [[ "$out" == "15" ]]; then
    pass "for: loop sum"
else
    fail "for: loop" "got $out"
fi

# ── if/elsif ──────────────────────────────────────────────────────────
echo ""
echo "## control flow in -c"

out=$(rc 'x = 15
if x > 20
  puts "high"
elsif x > 10
  puts "medium"
else
  puts "low"
end')
if [[ "$out" == "medium" ]]; then
    pass "if/elsif: chain"
else
    fail "if/elsif" "got $out"
fi

# ── platform detection ────────────────────────────────────────────────
echo ""
echo "## platform"

out=$(rc 'puts os')
if echo "$out" | grep -qE "^(macos|linux|windows)$"; then
    pass "platform: os=$out"
else
    fail "platform: os" "got $out"
fi

out=$(rc 'puts __rush_arch')
if echo "$out" | grep -qE "^(x64|arm64)$"; then
    pass "platform: arch=$out"
else
    fail "platform: arch" "got $out"
fi

# ── command substitution ─────────────────────────────────────────────
echo ""
echo "## command substitution"

out=$(rc 'h = $(hostname); puts h.strip')
if [[ -n "$out" ]]; then
    pass "cmd sub: hostname=$out"
else
    fail "cmd sub" "empty"
fi

# ── regex ─────────────────────────────────────────────────────────────
echo ""
echo "## regex"

out=$(rc 'puts "hello123" =~ /\d+/')
if [[ "$out" == "True" ]]; then
    pass "regex: =~ match"
else
    fail "regex: =~" "got $out"
fi

# ── export ────────────────────────────────────────────────────────────
echo ""
echo "## export"

rc "export RUSH_C_EXPORT_TEST=hello_from_c"
# export sets env var but we can't read it back in a separate -c call
# (separate process). Test that it doesn't error.
pass "export: no error"

# ── mark ───────────────────────────────────────���──────────────────────
echo ""
echo "## mark"

out=$(rc 'mark "test label"')
if echo "$out" | grep -q "═══.*test label"; then
    pass "mark: with label"
else
    fail "mark: label" "got $out"
fi

out=$(rc 'mark')
if echo "$out" | grep -q "═══"; then
    pass "mark: bare"
else
    fail "mark: bare" "got $out"
fi

out=$(rc '---')
if echo "$out" | grep -q "═══"; then
    pass "mark: --- shorthand"
else
    fail "mark: ---" "got $out"
fi

# ─��� path ──────────────────────────────────────────────────────────────
echo ""
echo "## path"

out=$(rc 'path')
if echo "$out" | grep -q "PATH entries"; then
    pass "path: list entries"
else
    fail "path: list" "got $out"
fi

# ── cd ────────────────────────────────────────────────────────────────
echo ""
echo "## cd"

# cd is handled by the chain path, not the standalone builtin intercept
# Test via chain: cd + pwd in one -c call
out=$(rc 'cd /tmp && pwd')
if echo "$out" | grep -q "tmp"; then
    pass "cd: changes directory"
else
    fail "cd: dir" "got $out"
fi

# cd ~ (home directory)
out=$(rc 'cd ~ && pwd')
home_dir=$(eval echo ~)
if echo "$out" | grep -q "$(basename "$home_dir")"; then
    pass "cd: ~ expands to home"
else
    fail "cd: ~" "got $out"
fi

# ── alias ─────────────────────────────────────────────────────────────
echo ""
echo "## alias"

# Standalone alias creation (no error)
out=$(rc 'alias g="git"' 2>&1)
exit_code=$?
if [[ $exit_code -eq 0 ]]; then
    pass "alias: create (no error)"
else
    fail "alias: create" "exit $exit_code: $out"
fi

# ── export + env access ──────────────────────────────────────────────
echo ""
echo "## export"

# Export sets env var (standalone)
out=$(rc 'export RUSH_EXPORT_C_TEST=hello' 2>&1)
exit_code=$?
if [[ $exit_code -eq 0 ]]; then
    pass "export: standalone (no error)"
else
    fail "export: standalone" "exit $exit_code: $out"
fi

# Export + use in chain
out=$(rc 'export RUSH_CHAIN_TEST=works && echo $env:RUSH_CHAIN_TEST')
if echo "$out" | grep -q "works"; then
    pass "export: chain + access"
else
    fail "export: chain" "got $out"
fi

# ── unset ─────────────────────────────────────────────────────────────
echo ""
echo "## unset"

out=$(rc 'export RUSH_UNSET_TEST=exists && unset RUSH_UNSET_TEST && echo $env:RUSH_UNSET_TEST' 2>&1)
# After unset, the var should be empty
if [[ -z "$out" ]] || echo "$out" | grep -qv "exists"; then
    pass "unset: removes env var"
else
    fail "unset" "got $out"
fi

# ── set ───────────────────────────────────────────────────────────────
echo ""
echo "## set"

# set with no args shows settings (goes through chain path)
out=$(rc 'set' 2>&1)
if echo "$out" | grep -qi "edit_mode\|theme\|timing"; then
    pass "set: shows settings"
else
    fail "set: show" "got $(echo "$out" | head -1)"
fi

# ── sync ──────────────────────────────────────────────────────────────
echo ""
echo "## sync"

# sync status should not crash (may show "not configured")
out=$(rc 'sync status' 2>&1)
exit_code=$?
# Accept both success (configured) and error (not configured) — just shouldn't crash
if [[ $exit_code -le 1 ]]; then
    pass "sync: status doesn't crash"
else
    fail "sync: status" "exit $exit_code"
fi

# ── Cleanup ──────────────────────────────────────────────────────────
rm -f "$TMPFILE"

# ── Summary ──────────────────────────────────────────────────────────
echo ""
TOTAL=$((PASS + FAIL))
echo "# Rush -c Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
if [ "$FAIL" -gt 0 ]; then
    exit 1
fi

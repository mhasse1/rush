#!/bin/bash
# Remote integration test suite for rush on Linux
# Usage: ./tests/remote-test.sh <host>
# Tests exercise `rush -c` (non-interactive mode)
set -uo pipefail

HOST="${1:-trinity}"
PASS=0
FAIL=0
SKIP=0
ERRORS=()

run() {
    local desc="$1"
    local cmd="$2"
    local expect="$3"

    local actual
    actual=$(ssh "$HOST" "rush -c '$cmd'" 2>&1) || true

    if echo "$actual" | grep -qF "$expect"; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: expected '$expect', got '$(echo "$actual" | head -1)'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
        printf "    expected: %s\n" "$expect"
        printf "    got:      %s\n" "$(echo "$actual" | head -1)"
    fi
}

run_exit() {
    local desc="$1"
    local cmd="$2"
    local expect_exit="${3:-0}"

    ssh "$HOST" "rush -c '$cmd'" >/dev/null 2>&1
    local actual_exit=$?

    if [[ "$actual_exit" == "$expect_exit" ]]; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: expected exit $expect_exit, got $actual_exit")
        printf "  \033[31m✗\033[0m %s (exit $actual_exit, expected $expect_exit)\n" "$desc"
    fi
}

run_nonempty() {
    local desc="$1"
    local cmd="$2"

    local actual
    actual=$(ssh "$HOST" "rush -c '$cmd'" 2>&1)
    local exit_code=$?

    if [[ $exit_code -eq 0 ]] && [[ -n "$actual" ]]; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: exit=$exit_code, output_len=${#actual}")
        printf "  \033[31m✗\033[0m %s (exit=%d, output_len=%d)\n" "$desc" "$exit_code" "${#actual}"
    fi
}

# Test that doesn't segfault (exit != 139)
run_nosegfault() {
    local desc="$1"
    local cmd="$2"

    ssh "$HOST" "rush -c '$cmd'" >/dev/null 2>&1
    local actual_exit=$?

    if [[ "$actual_exit" != "139" ]] && [[ "$actual_exit" != "134" ]]; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: SEGFAULT (exit $actual_exit)")
        printf "  \033[31m✗\033[0m %s (SEGFAULT exit $actual_exit)\n" "$desc"
    fi
}

# Run via rush --version etc (not -c)
run_flag() {
    local desc="$1"
    local flag="$2"
    local expect="$3"

    local actual
    actual=$(ssh "$HOST" "rush $flag" 2>&1) || true

    if echo "$actual" | grep -qF "$expect"; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: expected '$expect', got '$(echo "$actual" | head -1)'")
        printf "  \033[31m✗\033[0m %s\n" "$desc"
        printf "    expected: %s\n" "$expect"
        printf "    got:      %s\n" "$(echo "$actual" | head -1)"
    fi
}

skip() {
    local desc="$1"
    local reason="$2"
    ((SKIP++))
    printf "  \033[33m○\033[0m %s  (%s)\n" "$desc" "$reason"
}

echo "═══════════════════════════════════════════"
echo " Rush Remote Test Suite"
echo " Host: $HOST"
echo " $(ssh "$HOST" 'rush --version')"
echo "═══════════════════════════════════════════"
echo ""

# ── CLI Flags ───────────────────────────────────
echo "── CLI Flags ──"
run_flag "--version"            "--version"     "rush 1.2"
run_flag "--help"               "--help"        "Unix-style shell"

# ── Basics ──────────────────────────────────────
echo ""
echo "── Basics ──"
run "echo string"          'echo "hello world"'            "hello world"
run "echo variable"        'x = "rush"; echo $x'           "rush"
run "string interpolation" 'name = "world"; echo "hello #{name}"'  "hello world"

# ── Variables & Types ───────────────────────────
echo ""
echo "── Variables & Types ──"
run "integer"       'x = 42; echo $x'                    "42"
run "float"         'x = 3.14; echo $x'                  "3.14"
run "boolean true"  'x = true; echo $x'                  "True"
run "boolean false" 'x = false; echo $x'                 "False"
run "array literal" 'a = [1, 2, 3]; echo $a.Length'      "3"
run "hash literal"  'h = {name: "rush"}; echo $h.name'   "rush"
run "array index"   'a = [10, 20, 30]; echo $a[1]'       "20"
run "string assign"  'msg = "hello"; echo $msg'           "hello"
run "reassignment"   'x = 1; x = 2; echo $x'             "2"

# ── Arithmetic ──────────────────────────────────
echo ""
echo "── Arithmetic ──"
run "addition"       'x = 2 + 3; echo $x'                "5"
run "subtraction"    'x = 10 - 3; echo $x'               "7"
run "multiplication" 'x = 4 * 5; echo $x'                "20"
run "division"       'x = 10 / 2; echo $x'               "5"
run "modulo"         'x = 7 % 3; echo $x'                "1"

# ── String Methods (via variable, not literal chain) ──
echo ""
echo "── String Methods ──"
run "trim"           'x = "  hi  "; echo $x.Trim()'      "hi"
run "toupper"        'x = "hello"; echo $x.ToUpper()'    "HELLO"
run "tolower"        'x = "HELLO"; echo $x.ToLower()'    "hello"
run "length"         'x = "hello"; echo $x.Length'        "5"
run "contains"       'x = "hello world"; echo $x.Contains("world")'  "True"
run "replace"        'x = "hello"; echo $x.Replace("l", "r")'  "herro"
run "startswith"     'x = "hello"; echo $x.StartsWith("hel")'  "True"
run "substring"      'x = "hello"; echo $x.Substring(1,3)'     "ell"

# ── Control Flow ────────────────────────────────
echo ""
echo "── Control Flow ──"
run "if true"        'if true; echo "yes"; end'           "yes"
run "if false"       'if false; echo "yes"; else; echo "no"; end'  "no"
run "if comparison"  'x = 5; if x > 3; echo "big"; end'  "big"
run "if equals"      'x = "hi"; if x == "hi"; echo "match"; end'  "match"
run "while loop"     'i = 0; while i < 3; i = i + 1; end; echo $i'  "3"
run "for-in range"   's = ""; for i in 1..3; s = s + i.to_s; end; echo $s'  "123"
run "for-in array"   's = ""; for x in ["a","b","c"]; s = s + x; end; echo $s'  "abc"
run "unless"         'x = 5; unless x == 3; echo "not 3"; end'  "not 3"

# ── Pipes & Commands ───────────────────────────
echo ""
echo "── Pipes & Commands ──"
run_nonempty "echo pipe grep"  'echo "hello\nworld\nhello" | grep hello'
run_nonempty "pipe to head"    'echo "line1\nline2\nline3\nline4\nline5" | head -3'
run "command substitution"     'echo $(echo "inner")'     "inner"
run_nonempty "cat file"        'cat /etc/hostname'

# ── File System (ls) ───────────────────────────
echo ""
echo "── ls Builtin ──"
run_nonempty "ls basic"     'ls /tmp'
run_exit "ls exit 0"        'ls /tmp'                     0
run_nosegfault "ls -l"      'ls -l /tmp'
run_nosegfault "ls -a"      'ls -a /tmp'
run_nosegfault "ls -la"     'ls -la /tmp'
run_nosegfault "ls -lh"     'ls -lh /tmp'
run_nosegfault "ls -lah"    'ls -lah /tmp'
run_nosegfault "ls -alh"    'ls -alh /tmp'
run_nonempty "ls -lah out"  'ls -lah /tmp'
run "ls -lah perms"         'ls -lah /tmp'                "rwx"
run_exit "ls nonexistent"   'ls /nonexistent/path/xyz'    1

# ── ls piped (regression test) ──────────────────
echo ""
echo "── ls Piped (regression) ──"
run_nonempty "ls -la | head"     'ls -la /tmp | head -5'
run_nonempty "ls -lah | head"    'ls -lah /tmp | head -5'
run_nonempty "ls | grep"         'ls /tmp | head -3'

# ── File Operations ─────────────────────────────
echo ""
echo "── File Operations ──"
TMP="/tmp/rush-test-$$"
run "File.write"              "File.write(\"$TMP\", \"test-data\")"  ""
run_exit "File.write exit"    "File.write(\"$TMP\", \"test-data\")"  0
run "File.read"               "echo \$(File.read(\"$TMP\"))"  "test-data"
run "File.exists true"        "echo \$(File.exists(\"$TMP\"))"  "True"
run "File.exists false"       'echo $(File.exists("/tmp/no-such-file-xyz"))'  "False"
run_exit "File.delete"        "File.delete(\"$TMP\")"     0

# ── Dir Operations ──────────────────────────────
echo ""
echo "── Dir Operations ──"
TMPDIR="/tmp/rush-test-dir-$$"
run_exit "Dir.mkdir"          "Dir.mkdir(\"$TMPDIR\")"     0
run "Dir.exists true"         "echo \$(Dir.exists(\"$TMPDIR\"))"  "True"
run "Dir.exists false"        'echo $(Dir.exists("/tmp/no-such-dir-xyz"))'  "False"
run_exit "Dir.rmdir"          "Dir.rmdir(\"$TMPDIR\")"     0

# ── Time ────────────────────────────────────────
echo ""
echo "── Time ──"
run_nonempty "Time.now"        'Time.now.print'
run "Time.now.year"            'y = Time.now.year; echo $y'  "2026"

# ── Shell Builtins ──────────────────────────────
echo ""
echo "── Shell Commands ──"
run "whoami"         'whoami'                              "mark"
run "hostname"       'hostname'                            "$(ssh "$HOST" 'hostname')"
run_nonempty "uname" 'uname -a'
run_nonempty "env"   'env | head -5'
run_nonempty "date"  'date'
run "pwd"            'pwd'                                 "/"
run_exit "true"      'true'                                0
run_exit "false"     'false'                               1

# ── Config ──────────────────────────────────────
echo ""
echo "── Config ──"
run_exit "set --list"  'set --list'                        0

# ── Stability (no segfaults) ────────────────────
echo ""
echo "── Stability ──"
run_nosegfault "ls -l home"      'ls -l ~'
run_nosegfault "ls -la home"     'ls -la ~'
run_nosegfault "ls -lah home"    'ls -lah ~'
run_nosegfault "ls -R /tmp"      'ls -R /tmp'
run_nosegfault "empty command"   'echo ""'
run_nosegfault "long output"     'ls -la /usr/bin'

# ── Summary ─────────────────────────────────────
echo ""
echo "═══════════════════════════════════════════"
TOTAL=$((PASS + FAIL))
if [[ $FAIL -eq 0 ]]; then
    printf " \033[32mAll %d tests passed\033[0m" "$TOTAL"
    [[ $SKIP -gt 0 ]] && printf " (%d skipped)" "$SKIP"
    printf "\n"
else
    printf " \033[32m%d passed\033[0m, \033[31m%d failed\033[0m" "$PASS" "$FAIL"
    [[ $SKIP -gt 0 ]] && printf ", %d skipped" "$SKIP"
    printf " (of %d)\n" "$TOTAL"
    echo ""
    echo " Failures:"
    for err in "${ERRORS[@]}"; do
        printf "   \033[31m✗\033[0m %s\n" "$err"
    done
fi
echo "═══════════════════════════════════════════"

exit $FAIL

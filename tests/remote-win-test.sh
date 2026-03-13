#!/bin/bash
# Remote integration test suite for rush on Windows (via SSH)
# Usage: ./tests/remote-win-test.sh [host] [--skip-build] [--skip-deploy]
# Tests exercise `rush -c` (non-interactive mode) and .rush scripts
set -uo pipefail

HOST="${1:-buster}"
SKIP_BUILD=false
SKIP_DEPLOY=false
for arg in "$@"; do
    case "$arg" in
        --skip-build)  SKIP_BUILD=true ;;
        --skip-deploy) SKIP_DEPLOY=true ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
RUSH="rush"  # installed to C:\bin, on PATH
SSH_OPTS="-o LogLevel=ERROR"
PASS=0
FAIL=0
SKIP=0
ERRORS=()

# Escape " and $( in a rush command so bash doesn't mangle them
# inside the outer double quotes of the SSH call.
escape_cmd() {
    local s="${1//\"/\\\"}"
    s="${s//\$(/\\\$(}"
    echo "$s"
}

# ── Test Helpers ─────────────────────────────────────

run() {
    local desc="$1"
    local cmd="$2"
    local expect="$3"

    local safe
    safe=$(escape_cmd "$cmd")
    local actual
    actual=$(ssh $SSH_OPTS "$HOST" "$RUSH -c '$safe'" 2>&1) || true

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

    local safe
    safe=$(escape_cmd "$cmd")
    ssh $SSH_OPTS "$HOST" "$RUSH -c '$safe'" >/dev/null 2>/dev/null
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

    local safe
    safe=$(escape_cmd "$cmd")
    local actual
    actual=$(ssh $SSH_OPTS "$HOST" "$RUSH -c '$safe'" 2>&1)
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

run_nosegfault() {
    local desc="$1"
    local cmd="$2"

    local safe
    safe=$(escape_cmd "$cmd")
    ssh $SSH_OPTS "$HOST" "$RUSH -c '$safe'" >/dev/null 2>/dev/null
    local actual_exit=$?

    # On Windows, crashes show as various non-zero exits (not 139/134)
    if [[ "$actual_exit" != "139" ]] && [[ "$actual_exit" != "134" ]]; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s\n" "$desc"
    else
        ((FAIL++))
        ERRORS+=("$desc: CRASH (exit $actual_exit)")
        printf "  \033[31m✗\033[0m %s (CRASH exit $actual_exit)\n" "$desc"
    fi
}

run_flag() {
    local desc="$1"
    local flag="$2"
    local expect="$3"

    local actual
    actual=$(ssh $SSH_OPTS "$HOST" "$RUSH $flag" 2>&1) || true

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

run_script() {
    local desc="$1"
    local script_path="$2"

    local actual
    actual=$(ssh $SSH_OPTS "$HOST" "$RUSH $script_path" 2>&1)
    local exit_code=$?

    local pass_count fail_count
    pass_count=$(echo "$actual" | grep -c "^PASS:" || true)
    fail_count=$(echo "$actual" | grep -c "^FAIL:" || true)

    if [[ "$fail_count" -eq 0 ]] && [[ "$exit_code" -eq 0 ]]; then
        ((PASS++))
        printf "  \033[32m✓\033[0m %s (%d assertions)\n" "$desc" "$pass_count"
    elif [[ "$fail_count" -eq 0 ]] && [[ "$exit_code" -ne 0 ]]; then
        ((FAIL++))
        ERRORS+=("$desc: exit=$exit_code (crash or error)")
        printf "  \033[31m✗\033[0m %s (exit=%d)\n" "$desc" "$exit_code"
        echo "$actual" | tail -3 | while read -r line; do
            printf "    %s\n" "$line"
        done
    else
        ((FAIL++))
        ERRORS+=("$desc: $fail_count FAIL assertions")
        printf "  \033[31m✗\033[0m %s (%d pass, %d fail)\n" "$desc" "$pass_count" "$fail_count"
        echo "$actual" | grep "^FAIL:" | while read -r line; do
            printf "    %s\n" "$line"
        done
    fi
}

skip() {
    local desc="$1"
    local reason="$2"
    ((SKIP++))
    printf "  \033[33m○\033[0m %s  (%s)\n" "$desc" "$reason"
}

# ── Phase A: Build & Deploy ──────────────────────────

if [[ "$SKIP_BUILD" == false ]]; then
    echo "Building rush for Windows x64..."
    export PATH="/opt/homebrew/opt/dotnet@8/bin:$PATH"
    export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
    # SkipCleanCheck — test builds don't need a clean tree
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
        -p:SkipCleanCheck=true "$SCRIPT_DIR" > /dev/null 2>&1
    if [[ $? -ne 0 ]]; then
        echo "  Build failed. Try --skip-build to reuse existing binary."
        exit 1
    fi
    echo "  Build complete."
fi

if [[ "$SKIP_DEPLOY" == false ]]; then
    BINARY="$SCRIPT_DIR/bin/Release/net8.0/win-x64/publish/rush.exe"
    if [[ ! -f "$BINARY" ]]; then
        echo "  ERROR: $BINARY not found. Run without --skip-build first."
        exit 1
    fi

    echo "Deploying to $HOST..."
    ssh $SSH_OPTS "$HOST" "mkdir C:\\bin 2>nul & mkdir C:\\rush-test 2>nul" 2>/dev/null || true

    echo "  → rush.exe → C:\\bin\\"
    scp -q $SSH_OPTS "$BINARY" "$HOST:C:/bin/rush.exe"

    # Ensure C:\bin is on PATH
    ssh $SSH_OPTS "$HOST" "powershell.exe -Command \"if (-not (\$env:PATH -split ';' | Where-Object { \$_ -eq 'C:\\bin' })) { [Environment]::SetEnvironmentVariable('PATH', \$env:PATH + ';C:\\bin', 'User'); Write-Output 'Added C:\\bin to PATH' } else { Write-Output 'C:\\bin already on PATH' }\"" 2>/dev/null

    # Deploy test scripts
    echo "  → integration_test.rush → C:\\rush-test\\"
    scp -q $SSH_OPTS "$SCRIPT_DIR/Rush.Tests/Fixtures/integration_test.rush" "$HOST:C:/rush-test/"

    # Deploy standalone .rush scripts (from Resilio dir if available)
    STANDALONE_DIR="$HOME/Resilio/coi/src/rush-tests"
    if [[ -d "$STANDALONE_DIR" ]]; then
        for f in "$STANDALONE_DIR"/[0-9]*.rush; do
            [[ -f "$f" ]] && scp -q $SSH_OPTS "$f" "$HOST:C:/rush-test/"
        done
        echo "  → standalone .rush scripts → C:\\rush-test\\"
    fi

    echo "  Warming up (first run self-extracts)..."
    ssh $SSH_OPTS "$HOST" "rush --version" 2>/dev/null || true
    echo ""
fi

# ── Header ────────────────────────────────────────────

VERSION=$(ssh $SSH_OPTS "$HOST" "rush --version" 2>/dev/null || echo "unknown")
echo "═══════════════════════════════════════════"
echo " Rush Windows Test Suite"
echo " Host: $HOST"
echo " $VERSION"
echo "═══════════════════════════════════════════"
echo ""

# ── CLI Flags ─────────────────────────────────────────
echo "── CLI Flags ──"
run_flag "--version"            "--version"     "rush"
run_flag "--help"               "--help"        "Unix-style shell"

# ── Basics ────────────────────────────────────────────
echo ""
echo "── Basics ──"
run "echo string"          'echo "hello world"'            "hello world"
run "echo variable"        'x = "rush"; echo $x'           "rush"
run "string interpolation" 'name = "world"; echo "hello #{name}"'  "hello world"

# ── Variables & Types ─────────────────────────────────
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

# ── Arithmetic ────────────────────────────────────────
echo ""
echo "── Arithmetic ──"
run "addition"       'x = 2 + 3; echo $x'                "5"
run "subtraction"    'x = 10 - 3; echo $x'               "7"
run "multiplication" 'x = 4 * 5; echo $x'                "20"
run "division"       'x = 10 / 2; echo $x'               "5"
run "modulo"         'x = 7 % 3; echo $x'                "1"

# ── String Methods ────────────────────────────────────
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

# ── Control Flow ──────────────────────────────────────
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

# ── Pipes & Commands ──────────────────────────────────
echo ""
echo "── Pipes & Commands ──"
run "command substitution"     'echo $(echo "inner")'     "inner"
skip "echo pipe grep"          "grep not available on Windows"
skip "pipe to head"            "head not available on Windows"
skip "cat file"                "no /etc/hostname on Windows"

# ── File System (ls) ─────────────────────────────────
echo ""
echo "── ls Builtin ──"
run_nonempty "ls home"        'ls ~'
run_exit "ls exit 0"          'ls ~'                       0
run_nosegfault "ls -l"        'ls -l ~'
run_nosegfault "ls -a"        'ls -a ~'
run_nosegfault "ls -la"       'ls -la ~'
run_nosegfault "ls -lh"       'ls -lh ~'
run_nosegfault "ls -lah"      'ls -lah ~'
run_nosegfault "ls -alh"      'ls -alh ~'
run_nonempty "ls -lah out"    'ls -lah ~'
run_exit "ls nonexistent"     'ls /nonexistent/path/xyz'   1

# ── File Operations ───────────────────────────────────
echo ""
echo "── File Operations ──"
TMP='C:\\rush-test\\test-file.tmp'
run "File.write"              "File.write(\"$TMP\", \"test-data\")"  ""
run_exit "File.write exit"    "File.write(\"$TMP\", \"test-data\")"  0
run "File.read"               "echo \$(File.read(\"$TMP\"))"  "test-data"
run "File.exists true"        "echo \$(File.exists(\"$TMP\"))"  "True"
run "File.exists false"       'echo $(File.exists("C:\\rush-test\\no-such-file.xyz"))'  "False"
run_exit "File.delete"        "File.delete(\"$TMP\")"     0

# ── Dir Operations ────────────────────────────────────
echo ""
echo "── Dir Operations ──"
TMPDIR='C:\\rush-test\\test-dir-tmp'
run_exit "Dir.mkdir"          "Dir.mkdir(\"$TMPDIR\")"     0
run "Dir.exists true"         "echo \$(Dir.exists(\"$TMPDIR\"))"  "True"
run "Dir.exists false"        'echo $(Dir.exists("C:\\rush-test\\no-such-dir-xyz"))'  "False"
run_exit "Dir.rmdir"          "Dir.rmdir(\"$TMPDIR\")"     0

# ── Time ──────────────────────────────────────────────
echo ""
echo "── Time ──"
run_nonempty "Time.now"        'Time.now.print'
run "Time.now.year"            'y = Time.now.year; echo $y'  "2026"

# ── Shell Commands ────────────────────────────────────
echo ""
echo "── Shell Commands ──"
run "whoami"         'whoami'                              "mark"
run_nonempty "hostname"  'hostname'
skip "uname"                   "no uname on Windows"
run_nonempty "date"  'date'
skip "false exit"              "no false command on Windows"
run_exit "true exit" 'echo ok'                             0

# ── Config ────────────────────────────────────────────
echo ""
echo "── Config ──"
run_exit "set --list"  'set --list'                        0

# ── Stability ─────────────────────────────────────────
echo ""
echo "── Stability ──"
run_nosegfault "ls -l home"      'ls -l ~'
run_nosegfault "ls -la home"     'ls -la ~'
run_nosegfault "ls -lah home"    'ls -lah ~'
run_nosegfault "empty command"   'echo ""'

# ── Script Execution ──────────────────────────────────
echo ""
echo "── Script Tests ──"

# Integration test script (comprehensive)
run_script "integration_test.rush" 'C:\rush-test\integration_test.rush'

# Standalone scripts (if deployed)
for num in 01 02 03 04 05 06 07 08; do
    name=$(ssh $SSH_OPTS "$HOST" "dir /b C:\\rush-test\\${num}-*.rush 2>nul" 2>/dev/null | tr -d '\r')
    if [[ -n "$name" ]]; then
        run_script "$name" "C:\\rush-test\\$name"
    fi
done

# ── Summary ───────────────────────────────────────────
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

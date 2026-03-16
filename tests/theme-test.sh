#!/bin/bash
# ═══════════════════════════════════════════════════════════════════
# Rush Auto-Theme Visual Test Suite
# ═══════════════════════════════════════════════════════════════════
#
# Tests Rush's auto-theming by cycling through background colors,
# running demo commands, and collecting visual feedback.
#
# Usage: bash tests/theme-test.sh
# ═══════════════════════════════════════════════════════════════════

set -euo pipefail

# ── Configuration ─────────────────────────────────────────────────

RUSH="${RUSH:-rush}"
TEST_DIR="/tmp/rush-theme-test"
RESULTS_FILE="/tmp/rush-theme-results.txt"

# Background test matrix: "name|hex"
BACKGROUNDS=(
    "Pure black|#000000"
    "Dark gray|#1a1a1a"
    "Dark blue-gray (default)|#222733"
    "Solarized dark|#002b36"
    "Teal|#008080"
    "Mid gray|#808080"
    "Solarized light|#fdf6e3"
    "Light gray|#e0e0e0"
    "Pure white|#ffffff"
    "Deep purple|#1e0033"
    "Dark green|#003300"
    "Navy blue|#000055"
)

# ── Helpers ───────────────────────────────────────────────────────

# Save current background (if RUSH_BG is set)
ORIG_BG="${RUSH_BG:-}"

restore_bg() {
    if [ -n "$ORIG_BG" ]; then
        set_bg "$ORIG_BG"
    else
        # Reset to terminal default
        printf '\e]111\a'
        unset RUSH_BG 2>/dev/null || true
    fi
}

trap restore_bg EXIT

set_bg() {
    local hex="$1"
    # Strip # prefix for OSC conversion
    local raw="${hex#\#}"

    # Convert 6-digit hex to 16-bit OSC 11 format: rgb:RRRR/GGGG/BBBB
    local r="${raw:0:2}" g="${raw:2:2}" b="${raw:4:2}"
    printf "\e]11;rgb:%s%s/%s%s/%s%s\a" "$r" "$r" "$g" "$g" "$b" "$b"

    export RUSH_BG="$hex"
}

separator() {
    echo ""
    echo "─────────────────────────────────────────────────────────"
    echo ""
}

# ── Create test fixture directory ─────────────────────────────────

setup_test_dir() {
    echo "Setting up test directory: $TEST_DIR"
    mkdir -p "$TEST_DIR/subdir"
    mkdir -p "$TEST_DIR/projects"

    # Regular files
    echo "Hello, world!" > "$TEST_DIR/readme.txt"
    echo "name,age,city" > "$TEST_DIR/data.csv"
    echo "name,age,city" >> "$TEST_DIR/data.csv"
    echo "Alice,30,NYC" >> "$TEST_DIR/data.csv"
    echo '{"theme": "auto", "contrast": "standard"}' > "$TEST_DIR/config.json"
    echo "print('hello')" > "$TEST_DIR/app.py"
    echo "fn main() { println!(\"hello\"); }" > "$TEST_DIR/main.rs"
    echo "console.log('hello');" > "$TEST_DIR/index.js"

    # Executable
    echo '#!/bin/bash' > "$TEST_DIR/run.sh"
    echo 'echo "running"' >> "$TEST_DIR/run.sh"
    chmod +x "$TEST_DIR/run.sh"

    # Archive (touch creates empty file — enough for color testing)
    touch "$TEST_DIR/backup.tar.gz"
    touch "$TEST_DIR/photo.jpg"

    # Symlink
    ln -sf "$TEST_DIR/readme.txt" "$TEST_DIR/link"

    # .rushbg for directory transition test
    echo "#334455" > "$TEST_DIR/.rushbg"

    echo "Done."
    echo ""
}

# ── Run demo commands for a given background ──────────────────────

run_demos() {
    local name="$1"
    local hex="$2"

    echo "═══════════════════════════════════════════════════════════"
    echo "  Background: $name ($hex)"
    echo "═══════════════════════════════════════════════════════════"
    echo ""

    # 1. File listing with colors
    echo "▸ ls (file type colors):"
    RUSH_BG="$hex" "$RUSH" -c "ls $TEST_DIR"
    echo ""

    # 2. Detailed listing
    echo "▸ ls -la (permissions, sizes, metadata):"
    RUSH_BG="$hex" ls -la "$TEST_DIR"
    echo ""

    # 3. Grep highlighting
    echo "▸ grep highlighting:"
    echo "This is a test line with the word theme in it." | RUSH_BG="$hex" grep --color=always "theme" 2>/dev/null || true
    echo "Another line mentioning auto-theme detection." | RUSH_BG="$hex" grep --color=always "auto" 2>/dev/null || true
    echo ""

    # 4. Rush syntax output (colored via transpiler)
    echo "▸ Rush syntax demo:"
    RUSH_BG="$hex" "$RUSH" -c '
name = "Rush"
version = 42
items = [1, 2, 3]
puts "Hello from #{name} v#{version}"
puts "Array: #{items}"
puts "Math: #{2 + 2}"
for i in 1..3
  puts "  item #{i}"
end
'
    echo ""

    # 5. String methods
    echo "▸ String methods:"
    RUSH_BG="$hex" "$RUSH" -c '
puts "hello world".upcase
puts "HELLO WORLD".downcase
puts "  padded  ".strip
puts "hello".include?("ell")
'
    echo ""

    # 6. Error output
    echo "▸ Error handling:"
    RUSH_BG="$hex" "$RUSH" -c '
begin
  x = 1 / 0
rescue e
  puts "Caught: #{e}"
end
' 2>&1
    echo ""
}

# ── Collect feedback ──────────────────────────────────────────────

collect_feedback() {
    local name="$1"
    local hex="$2"

    echo "──── Feedback ────"
    echo -n "  Pass / Fail / Skip? (p/f/s): "
    read -r verdict

    case "$verdict" in
        f|F|fail|FAIL)
            echo -n "  What's wrong? "
            read -r feedback
            echo "FAIL|$name|$hex|$feedback" >> "$RESULTS_FILE"
            ;;
        s|S|skip|SKIP)
            echo "SKIP|$name|$hex|" >> "$RESULTS_FILE"
            ;;
        *)
            echo "PASS|$name|$hex|" >> "$RESULTS_FILE"
            ;;
    esac
    echo ""
}

# ── Print summary ─────────────────────────────────────────────────

print_summary() {
    echo ""
    echo "═══════════════════════════════════════════════════════════"
    echo "  RESULTS SUMMARY"
    echo "═══════════════════════════════════════════════════════════"
    echo ""

    local pass=0 fail=0 skip=0
    while IFS='|' read -r result name hex feedback; do
        case "$result" in
            PASS) ((pass++)); printf "  ✓ %-28s %s\n" "$name" "$hex" ;;
            FAIL) ((fail++)); printf "  ✗ %-28s %s — %s\n" "$name" "$hex" "$feedback" ;;
            SKIP) ((skip++)); printf "  ○ %-28s %s (skipped)\n" "$name" "$hex" ;;
        esac
    done < "$RESULTS_FILE"

    echo ""
    echo "  Total: $pass passed, $fail failed, $skip skipped"
    echo ""

    # Offer to file bugs
    if [ "$fail" -gt 0 ]; then
        echo -n "  File failed items as GitHub issues? (y/n): "
        read -r file_bugs
        if [[ "$file_bugs" == "y" || "$file_bugs" == "Y" ]]; then
            file_github_issues
        fi
    fi
}

# ── File GitHub issues for failures ───────────────────────────────

file_github_issues() {
    if ! command -v gh &>/dev/null; then
        echo "  gh CLI not found — skipping issue creation"
        return
    fi

    while IFS='|' read -r result name hex feedback; do
        if [ "$result" = "FAIL" ]; then
            echo "  Filing issue for: $name ($hex)..."
            gh issue create -R mhasse1/rush \
                --title "Theme bug: $name ($hex) — $feedback" \
                --label "bug" \
                --body "## Auto-theme visual test failure

**Background**: $name (\`$hex\`)
**Feedback**: $feedback

Discovered during interactive theme test suite (\`tests/theme-test.sh\`).

### Reproduction
\`\`\`bash
RUSH_BG=\"$hex\" rush
\`\`\`
" 2>&1 || echo "  Failed to create issue"
        fi
    done < "$RESULTS_FILE"
    echo ""
}

# ── Navigation test ───────────────────────────────────────────────

run_navigation_test() {
    echo "═══════════════════════════════════════════════════════════"
    echo "  NAVIGATION TEST"
    echo "═══════════════════════════════════════════════════════════"
    echo ""
    echo "  A .rushbg file has been placed in $TEST_DIR"
    echo "  with color #334455 (dark blue-gray)."
    echo ""
    echo "  To test directory-specific backgrounds:"
    echo ""
    echo "    1. Open a new Rush REPL:  rush"
    echo "    2. cd $TEST_DIR       → bg should change to #334455"
    echo "    3. ls                       → verify file colors"
    echo "    4. cd subdir                → bg stays #334455"
    echo "    5. cd /tmp                  → bg should restore to original"
    echo "    6. cd $TEST_DIR       → bg changes again"
    echo "    7. cd ~                     → bg restores"
    echo ""
    echo "  Also test:"
    echo "    - Tab completion in $TEST_DIR"
    echo "    - Autosuggestions (type partial commands)"
    echo "    - Prompt colors at each step"
    echo ""
    echo -n "  Press Enter when done with navigation testing... "
    read -r _
    echo ""
    echo -n "  Navigation feedback (or Enter to skip): "
    read -r nav_feedback
    if [ -n "$nav_feedback" ]; then
        echo "NAV|Navigation|various|$nav_feedback" >> "$RESULTS_FILE"
    fi
    echo ""
}

# ── Main ──────────────────────────────────────────────────────────

main() {
    echo ""
    echo "╔═══════════════════════════════════════════════════════════╗"
    echo "║        Rush Auto-Theme Visual Test Suite                 ║"
    echo "║                                                         ║"
    echo "║  This test cycles through ${#BACKGROUNDS[@]} background colors.        ║"
    echo "║  For each, you'll see demo output and rate it.          ║"
    echo "║                                                         ║"
    echo "║  Keys: p=pass  f=fail  s=skip                          ║"
    echo "╚═══════════════════════════════════════════════════════════╝"
    echo ""

    # Initialize
    > "$RESULTS_FILE"
    setup_test_dir

    echo -n "Press Enter to begin... "
    read -r _

    # Run through each background
    for entry in "${BACKGROUNDS[@]}"; do
        IFS='|' read -r name hex <<< "$entry"
        separator

        # Apply background
        set_bg "$hex"
        sleep 0.3  # let terminal render

        run_demos "$name" "$hex"
        collect_feedback "$name" "$hex"
    done

    # Restore original background before summary
    restore_bg

    print_summary
    run_navigation_test

    echo "═══════════════════════════════════════════════════════════"
    echo "  Test complete. Results saved to: $RESULTS_FILE"
    echo "  Test directory preserved at: $TEST_DIR"
    echo "═══════════════════════════════════════════════════════════"
    echo ""
}

main "$@"

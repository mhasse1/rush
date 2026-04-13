#!/usr/bin/env bash
# pty-repro.sh — drive an interactive rush session via a PTY, type a
# command exactly as given, hit enter, watch memory until rush dies or
# a threshold is crossed.
#
# Uses `expect` to attach a PTY. Without a PTY, reedline paths don't
# run and the leak won't trigger.
#
# Usage:
#   pty-repro.sh '<command-to-type>' [timeout_sec] [rss_kb_limit]
#
# Examples:
#   pty-repro.sh 'sudo xremap-linux-aarch64-gnome /usr/local/bin/xremap'
#   pty-repro.sh 'echo xremap-linux-aarch64-gnome /usr/local/bin/xremap' 10
#
# Output: CSV of ts,rss,vsz,pcpu + final verdict.

set -euo pipefail

COMMAND="${1:?command to type required}"
TIMEOUT="${2:-30}"
LIMIT_KB="${3:-1048576}"   # 1 GB default; kill rush if it passes this
RUSH_BIN="${RUSH_BIN:-rush}"

if ! command -v expect >/dev/null 2>&1; then
    echo "ERROR: expect(1) not installed. Install with: sudo apt install expect" >&2
    exit 1
fi

SCRIPT=$(mktemp /tmp/pty-repro.XXXXXX.exp)
CSV=$(mktemp /tmp/pty-repro.XXXXXX.csv)
trap "rm -f '$SCRIPT' '$CSV'" EXIT

cat > "$SCRIPT" <<EXPECT
set timeout $TIMEOUT
spawn $RUSH_BIN
expect -re {\$ |» |> }
send -- {$COMMAND}
send -- "\r"
# Keep the PTY alive for up to timeout so the leak can develop and
# our outer monitor can sample RSS. Do not send further input.
expect eof
EXPECT

echo "command: $COMMAND"
echo "timeout: ${TIMEOUT}s"
echo "kill-at: ${LIMIT_KB} KB RSS"
echo

# Spawn expect; capture its child's pid so we can sample the rush
# that expect spawned (not expect itself).
expect "$SCRIPT" >/dev/null 2>&1 &
EXPECT_PID=$!

# Find the rush child of expect.
for _ in 1 2 3 4 5 6 7 8 9 10; do
    sleep 0.2
    RUSH_PID=$(pgrep -P "$EXPECT_PID" -x rush 2>/dev/null | head -1 || true)
    [[ -n "$RUSH_PID" ]] && break
done
if [[ -z "${RUSH_PID:-}" ]]; then
    echo "ERROR: could not find rush child of expect (pid $EXPECT_PID)" >&2
    kill "$EXPECT_PID" 2>/dev/null || true
    exit 1
fi
echo "rush pid: $RUSH_PID"
echo "ts_unix,rss_kb,vsz_kb,pcpu,note" | tee "$CSV"

START=$(date +%s)
MAX_RSS=0
VERDICT="timeout"
while kill -0 "$RUSH_PID" 2>/dev/null; do
    NOW=$(date +%s)
    ELAPSED=$((NOW - START))
    [[ "$ELAPSED" -gt "$TIMEOUT" ]] && break

    if [[ "$(uname)" == "Linux" ]]; then
        STATS=$(ps -o rss=,vsz=,pcpu= -p "$RUSH_PID" 2>/dev/null || true)
    else
        STATS=$(ps -o rss=,vsz=,%cpu= -p "$RUSH_PID" 2>/dev/null || true)
    fi
    [[ -z "$STATS" ]] && break
    RSS=$(echo "$STATS" | awk '{print $1}')
    [[ "$RSS" -gt "$MAX_RSS" ]] && MAX_RSS=$RSS
    LINE=$(echo "$STATS" | tr -s ' ' ',' | sed 's/^,//;s/,$//')
    echo "$NOW,$LINE," | tee -a "$CSV"

    if [[ "$RSS" -gt "$LIMIT_KB" ]]; then
        echo "# killing rush — RSS $RSS KB > limit $LIMIT_KB KB"
        kill -9 "$RUSH_PID" 2>/dev/null
        VERDICT="leak_killed"
        break
    fi
    sleep 0.5
done

# Final sample if dead already.
if ! kill -0 "$RUSH_PID" 2>/dev/null && [[ "$VERDICT" == "timeout" ]]; then
    VERDICT="exited_cleanly"
fi

# Cleanup.
kill "$EXPECT_PID" 2>/dev/null || true
wait 2>/dev/null || true

echo
echo "=== verdict: $VERDICT ==="
echo "max RSS: $MAX_RSS KB"
cp "$CSV" "./pty-repro-$(hostname -s)-$(date +%s).csv" 2>/dev/null || true

#!/usr/bin/env bash
# run-llm.sh — orchestrate a leak-hunt session against `rush --llm`.
#
# Spawns rush --llm reading from a fifo, kicks off the driver writing
# to that fifo, and starts monitor.sh sampling rush's RSS to a CSV.
# Runs for the given duration (or until interrupted) then cleans up.
#
# Usage:
#   run-llm.sh [duration_sec] [interval_sec] [output_dir]
#
# Defaults: duration=3600 (1h), interval=5, output=./leak-results/<host>-<ts>
#
# CSV is written to $OUT/rss.csv. rush's stdout is /dev/null (we don't
# care about responses for the leak hunt). Driver stdout is /dev/null too
# (it streams into the fifo).

set -euo pipefail

DURATION="${1:-3600}"
INTERVAL="${2:-5}"
HOST=$(hostname -s)
TS=$(date -u +%FT%H%M%SZ)
OUT="${3:-./leak-results/${HOST}-${TS}}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUSH_BIN="${RUSH_BIN:-rush}"

mkdir -p "$OUT"
echo "leak-hunt: host=$HOST duration=${DURATION}s interval=${INTERVAL}s out=$OUT"
echo "rush binary: $(command -v "$RUSH_BIN" || echo 'NOT FOUND')"
echo

# Make a fifo so rush stays alive reading from it indefinitely.
FIFO="$OUT/cmd.fifo"
mkfifo "$FIFO"

# Start rush --llm reading from the fifo. Open write side too so the
# fifo doesn't EOF when the driver pauses or gets restarted.
exec 9>"$FIFO"
"$RUSH_BIN" --llm < "$FIFO" > "$OUT/rush.stdout.log" 2> "$OUT/rush.stderr.log" &
RUSH_PID=$!
echo "rush --llm pid: $RUSH_PID"

# Give rush a beat to start.
sleep 1

# Driver: stream commands forever into the fifo.
"$SCRIPT_DIR/driver.sh" > "$FIFO" &
DRIVER_PID=$!
echo "driver pid: $DRIVER_PID"

# Monitor: sample RSS to CSV.
"$SCRIPT_DIR/monitor.sh" "$RUSH_PID" "$INTERVAL" "$OUT/rss.csv" &
MON_PID=$!
echo "monitor pid: $MON_PID"

cleanup() {
    echo
    echo "stopping..."
    kill "$DRIVER_PID" 2>/dev/null || true
    kill "$MON_PID" 2>/dev/null || true
    # Close our fifo write fd so rush sees EOF and exits cleanly.
    exec 9>&-
    # Give it a moment for clean exit; SIGTERM if still alive.
    sleep 1
    if kill -0 "$RUSH_PID" 2>/dev/null; then
        kill -TERM "$RUSH_PID" 2>/dev/null || true
        sleep 1
        kill -KILL "$RUSH_PID" 2>/dev/null || true
    fi
    rm -f "$FIFO"
    echo "results in: $OUT"
    head -1 "$OUT/rss.csv"
    tail -3 "$OUT/rss.csv"
}
trap cleanup EXIT INT TERM

# Sleep for the duration. Background processes do the work.
echo "running for ${DURATION}s. interrupt to stop early."
sleep "$DURATION"

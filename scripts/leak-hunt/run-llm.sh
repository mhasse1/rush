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

# Pipe the driver directly into rush --llm. The driver is infinite, so
# rush stays alive until we kill the pipeline. $! after a pipeline gives
# the rightmost command's PID, which is rush.
DRIVER_SCRIPT="${DRIVER:-$SCRIPT_DIR/driver.sh}"
"$DRIVER_SCRIPT" | "$RUSH_BIN" --llm > "$OUT/rush.stdout.log" 2> "$OUT/rush.stderr.log" &
RUSH_PID=$!
DRIVER_PID=""  # not directly tracked; pgkill via process group on cleanup
echo "rush --llm pid: $RUSH_PID"

# Give rush a beat to start.
sleep 1

# Monitor: sample RSS to CSV.
"$SCRIPT_DIR/monitor.sh" "$RUSH_PID" "$INTERVAL" "$OUT/rss.csv" &
MON_PID=$!
echo "monitor pid: $MON_PID"

cleanup() {
    echo
    echo "stopping..."
    kill "$MON_PID" 2>/dev/null || true
    # Kill the whole pipeline (driver + rush) by killing rush, which
    # makes driver exit on broken pipe.
    if kill -0 "$RUSH_PID" 2>/dev/null; then
        kill -TERM "$RUSH_PID" 2>/dev/null || true
        sleep 1
        kill -KILL "$RUSH_PID" 2>/dev/null || true
    fi
    pkill -f "$SCRIPT_DIR/driver.sh" 2>/dev/null || true
    echo "results in: $OUT"
    [[ -f "$OUT/rss.csv" ]] && head -1 "$OUT/rss.csv" && tail -3 "$OUT/rss.csv" || echo "no rss.csv produced"
}
trap cleanup EXIT INT TERM

# Sleep for the duration. Background processes do the work.
echo "running for ${DURATION}s. interrupt to stop early."
sleep "$DURATION"

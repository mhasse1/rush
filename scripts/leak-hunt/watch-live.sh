#!/usr/bin/env bash
# watch-live.sh — monitor a live interactive rush session for memory growth.
#
# Use case: your interactive rush has been leaking. Start your normal
# rush session in one terminal, run this in another. CSV updates every
# 10 seconds. When rush dies (OOM kill, manual exit, etc.) you have the
# data leading up to the kill.
#
# Usage:
#   watch-live.sh [interval_sec] [output_csv]
#   watch-live.sh                                  # picks newest rush, 10s interval
#   watch-live.sh 5                                # 5s interval, picks newest rush
#   watch-live.sh 5 ~/rush-leak.csv                # 5s interval, custom path
#
# Picks the newest rush PID by default. If you have multiple, list them
# with `pgrep -af rush` and pass the right one explicitly:
#   watch-live.sh 5 ~/rush-leak.csv 12345
#
# CSV: ts_unix,ts_iso,pid,rss_kb,vsz_kb,pcpu,threads

set -euo pipefail

INTERVAL="${1:-10}"
OUT="${2:-rush-leak-$(hostname -s)-$(date +%Y%m%dT%H%M%S).csv}"
PID="${3:-}"

if [[ -z "$PID" ]]; then
    # Pick the newest rush — assume that's the active one.
    # Filter to interactive (no -c, no --llm, no --mcp args).
    PID=$(pgrep -af 'rush$' | grep -v 'watch-live\|monitor\.sh\|driver\.sh' | tail -1 | awk '{print $1}')
    if [[ -z "$PID" ]]; then
        echo "no interactive rush process found. running rushes:" >&2
        pgrep -af rush >&2
        exit 1
    fi
fi

if ! kill -0 "$PID" 2>/dev/null; then
    echo "PID $PID is not alive" >&2
    exit 1
fi

CMDLINE=$(ps -o cmd= -p "$PID" 2>/dev/null || echo "?")
echo "monitoring rush PID $PID — '$CMDLINE'"
echo "interval: ${INTERVAL}s"
echo "output:   $OUT"
echo "press Ctrl-C to stop (CSV is flushed on every sample)"
echo

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$SCRIPT_DIR/monitor.sh" "$PID" "$INTERVAL" "$OUT"

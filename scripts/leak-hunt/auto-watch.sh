#!/usr/bin/env bash
# auto-watch.sh — keep monitoring the newest interactive rush on this
# host forever. When the current target exits (OOM kill or otherwise)
# wait for a new interactive rush to appear, then monitor that one.
# All samples append to a single rotating CSV per day.
#
# Designed to run unattended in a tmux or nohup wrapper so you can
# collect data across many days of normal interactive work.
#
# Usage:
#   nohup ./auto-watch.sh > /tmp/auto-watch.log 2>&1 &
#   # then just use rush normally; when it dies, the CSV is there.
#
# CSV path: ~/rush-leak/<host>-<YYYY-MM-DD>.csv (one per day).
# Header is written once per target change; each new target gets a
# marker row so you can see where old target died and new one started.

set -euo pipefail

INTERVAL="${1:-10}"
OUT_DIR="${HOME}/rush-leak"
mkdir -p "$OUT_DIR"

# Strict match: interactive rush = `rush` or `/path/to/rush` with no
# additional args (no -c, --llm, --mcp, --version, etc.). pgrep -x
# doesn't accept paths; use pgrep -a and post-filter.
find_interactive_rush() {
    pgrep -af '^[^ ]*rush$|^[^ ]*rush  *$' 2>/dev/null |
        grep -v 'auto-watch\|watch-live\|monitor\.sh\|driver\.sh\|run-llm\|rush-bench' |
        awk 'NF <= 2 {print $1}' |
        sort -n |
        tail -1
}

csv_for_today() {
    printf '%s/%s-%s.csv' "$OUT_DIR" "$(hostname -s)" "$(date +%Y-%m-%d)"
}

log() {
    printf '[%s] %s\n' "$(date -u +%FT%TZ)" "$*"
}

log "auto-watch starting on $(hostname -s), interval=${INTERVAL}s, out=$OUT_DIR"

while true; do
    PID=$(find_interactive_rush || true)
    if [[ -z "$PID" ]]; then
        sleep 5
        continue
    fi

    CSV="$(csv_for_today)"
    # Write header if file is new/empty.
    if [[ ! -s "$CSV" ]]; then
        printf 'ts_unix,ts_iso,pid,rss_kb,vsz_kb,pcpu,threads\n' > "$CSV"
    fi
    # Marker row so you can see where one target ended and another began.
    CMDLINE=$(ps -o cmd= -p "$PID" 2>/dev/null | tr -d ',' || echo '?')
    printf '# --- %s attaching pid=%s cmd=%s\n' "$(date -u +%FT%TZ)" "$PID" "$CMDLINE" >> "$CSV"
    log "attaching to rush PID $PID ($CMDLINE)"

    # Sample until the process dies.
    while kill -0 "$PID" 2>/dev/null; do
        TS=$(date +%s)
        ISO=$(date -u +%FT%TZ)
        if [[ "$(uname)" == "Linux" ]]; then
            STATS=$(ps -o rss=,vsz=,pcpu=,nlwp= -p "$PID" 2>/dev/null || true)
        else
            STATS=$(ps -o rss=,vsz=,%cpu= -p "$PID" 2>/dev/null || true)
            THREADS=$(ps -M -p "$PID" 2>/dev/null | tail -n +2 | wc -l | tr -d ' ' || echo 0)
            STATS="$STATS $THREADS"
        fi
        [[ -z "$STATS" ]] && break
        CSV_ROW=$(echo "$STATS" | tr -s ' ' ',' | sed 's/^,//;s/,$//')
        printf '%s,%s,%s,%s\n' "$TS" "$ISO" "$PID" "$CSV_ROW" >> "$CSV"
        sleep "$INTERVAL"
    done

    printf '# --- %s pid=%s exited\n' "$(date -u +%FT%TZ)" "$PID" >> "$CSV"
    log "PID $PID exited, searching for next interactive rush"
done

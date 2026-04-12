#!/usr/bin/env bash
# monitor.sh — sample a process's RSS / VSZ / CPU at intervals.
#
# Usage:
#   monitor.sh <pid> [interval_sec] [output_csv]
#
# Defaults: interval=5, output=stdout
#
# Output CSV columns: ts_unix, ts_iso, pid, rss_kb, vsz_kb, pcpu, threads
# Exits when the target PID disappears.

set -euo pipefail

PID="${1:?pid required}"
INTERVAL="${2:-5}"
OUT="${3:-/dev/stdout}"

# Header.
printf 'ts_unix,ts_iso,pid,rss_kb,vsz_kb,pcpu,threads\n' > "$OUT"

while kill -0 "$PID" 2>/dev/null; do
    TS=$(date +%s)
    ISO=$(date -u +%FT%TZ)

    # Linux: ps -o rss,vsz,pcpu,nlwp; macOS: ps -o rss,vsz,%cpu (no thread count, use ps -M to get a count)
    if [[ "$(uname)" == "Linux" ]]; then
        STATS=$(ps -o rss=,vsz=,pcpu=,nlwp= -p "$PID" 2>/dev/null || true)
    else
        # macOS: nlwp not available via ps; substitute thread count via ps -M | wc -l (minus header)
        STATS=$(ps -o rss=,vsz=,%cpu= -p "$PID" 2>/dev/null || true)
        THREADS=$(ps -M -p "$PID" 2>/dev/null | tail -n +2 | wc -l | tr -d ' ' || echo 0)
        STATS="$STATS $THREADS"
    fi

    if [[ -z "$STATS" ]]; then
        # Process gone between kill -0 check and ps; exit cleanly.
        break
    fi

    # Normalize whitespace to single commas.
    CSV=$(echo "$STATS" | tr -s ' ' ',' | sed 's/^,//;s/,$//')
    printf '%s,%s,%s,%s\n' "$TS" "$ISO" "$PID" "$CSV" >> "$OUT"

    sleep "$INTERVAL"
done

printf '# target PID %s exited at %s\n' "$PID" "$(date -u +%FT%TZ)" >> "$OUT"

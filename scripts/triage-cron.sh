#!/usr/bin/env bash
# triage-cron.sh — wraps `claude -p` to run the triage-report agent unattended.
# Intended for system cron, e.g.:
#   17 3 * * * /home/mark/src/mcp/rush/scripts/triage-cron.sh
# (3:17 AM local — off-prime to avoid scheduler pile-ups)
#
# The agent itself is read-only and self-cancels if there's no delta.
# Output goes to .claude/cache/triage-cron.log (last run only).
set -euo pipefail

REPO="/home/mark/src/mcp/rush"
LOG="$REPO/.claude/cache/triage-cron.log"
mkdir -p "$(dirname "$LOG")"

cd "$REPO"

# --no-interactive runs Claude in one-shot mode; `-p` passes the prompt.
# We invoke the subagent directly so it doesn't get rerouted.
claude -p "Use the triage-report subagent to run a nightly triage report. \
Compare current state against .claude/cache/triage-state.json, post a delta-only \
comment to issue #291, and update the cache. Stay silent if there is no delta." \
  --no-interactive >"$LOG" 2>&1 || {
    echo "[triage-cron] claude exited non-zero; see $LOG" >&2
    exit 1
  }

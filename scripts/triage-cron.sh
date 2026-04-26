#!/usr/bin/env bash
# triage-cron.sh — wraps `claude -p` to run the triage-report agent unattended.
# Intended for system cron, e.g.:
#   17 3 * * * /home/mark/src/mcp/rush/scripts/triage-cron.sh
# (3:17 AM local — off-prime to avoid scheduler pile-ups)
#
# The agent itself is read-only and self-cancels if there's no delta.
# Output goes to .claude/cache/triage-cron.log (last run only).
set -euo pipefail

# Cron starts with PATH=/usr/bin:/bin only — the claude CLI lives in
# ~/.local/bin and gh may rely on ~/.cargo/bin or /usr/local/bin.
export PATH="$HOME/.local/bin:$HOME/.cargo/bin:/usr/local/bin:$PATH"

REPO="/home/mark/src/mcp/rush"
LOG="$REPO/.claude/cache/triage-cron.log"
mkdir -p "$(dirname "$LOG")"

cd "$REPO"

# `-p`/`--print` is the non-interactive mode. `--agent triage-report` selects
# the project-local subagent (see .claude/agents/triage-report.md).
# `--dangerously-skip-permissions` is required for unattended cron — there's
# no human to approve tool prompts. Blast radius is bounded by the agent's
# `tools: Bash, Read, Write` whitelist and its read-only-on-repo contract.
claude -p \
  --agent triage-report \
  --dangerously-skip-permissions \
  "Run the nightly triage report. Compare current state against \
.claude/cache/triage-state.json, post a delta-only comment to issue #291, \
and update the cache. Stay silent if there is no delta." \
  >"$LOG" 2>&1 || {
    echo "[triage-cron] claude exited non-zero; see $LOG" >&2
    exit 1
  }

#!/usr/bin/env bash
# macos-pty-investigate.sh — repro #295 on macOS hands-on.
#
# Temporarily flips the pty test files from cfg(target_os = "linux") to
# cfg(unix) so the macOS test paths actually run. Builds, runs the smoke
# test with a 60-second wall timeout, and captures the result + any
# diagnostic output to .macos-pty-log.txt at the repo root.
#
# IMPORTANT: this DOES NOT touch /usr/local/bin/rush. Cargo builds and
# runs the test binary at target/debug/rush-cli; that's the only rush
# binary the harness invokes.
#
# Always restores the cfg flip on exit (trap), even if you hit Ctrl-C
# or the test hangs. So the working tree is clean afterward.
#
# Usage:
#   ./scripts/macos-pty-investigate.sh
#
# After it finishes, send the log back so I can analyze:
#   git add .macos-pty-log.txt
#   git commit -m "macos pty investigation log"
#   git push
set -u
# Note: no `set -e` — we want to capture failures, not bail on them.

REPO="$(git -C "$(dirname "$0")/.." rev-parse --show-toplevel)"
cd "$REPO"

LOG="$REPO/.macos-pty-log.txt"
TIMEOUT_SECS=60

FILES=(
  "crates/rush-cli/tests/pty_smoke.rs"
  "crates/rush-cli/tests/pty_paint_no_absolute.rs"
  "crates/rush-cli/tests/pty/mod.rs"
)

cleanup() {
  echo "" | tee -a "$LOG"
  echo "[cleanup] restoring cfg gates" | tee -a "$LOG"
  git checkout -- "${FILES[@]}" 2>/dev/null
  # Reap any lingering rush-cli children we spawned.
  pkill -9 -f 'target/debug/rush-cli' 2>/dev/null
}
trap cleanup EXIT INT TERM

# Header.
{
  echo "===================================================================="
  echo "macos-pty-investigate — $(date -u +%FT%TZ)"
  echo "host:   $(hostname)"
  echo "uname:  $(uname -a)"
  echo "rust:   $(rustc --version 2>&1)"
  echo "git:    $(git rev-parse --short HEAD) ($(git rev-parse --abbrev-ref HEAD))"
  echo "===================================================================="
} | tee "$LOG"

# Flip cfg.
echo "" | tee -a "$LOG"
echo "[step] flipping cfg(target_os = \"linux\") → cfg(unix) in 3 files" | tee -a "$LOG"
sed -i '' 's/cfg(target_os = "linux")/cfg(unix)/' "${FILES[@]}" 2>&1 | tee -a "$LOG"
git diff --stat "${FILES[@]}" | tee -a "$LOG"

# Build first so build noise doesn't muddy the test step's log.
echo "" | tee -a "$LOG"
echo "[step] cargo build -p rush-cli --tests" | tee -a "$LOG"
cargo build -p rush-cli --tests 2>&1 | tee -a "$LOG"
BUILD_RC=${PIPESTATUS[0]}
if [[ $BUILD_RC -ne 0 ]]; then
  echo "" | tee -a "$LOG"
  echo "[ABORT] build failed with rc=$BUILD_RC" | tee -a "$LOG"
  exit $BUILD_RC
fi

# Run the smoke test with a wall timeout.
echo "" | tee -a "$LOG"
echo "[step] cargo test pty_smoke (timeout: ${TIMEOUT_SECS}s)" | tee -a "$LOG"
echo "       NB: smoke test has 5s internal deadlines, so should finish in ~5s." | tee -a "$LOG"
echo "" | tee -a "$LOG"

START=$(date +%s)
cargo test -p rush-cli --test pty_smoke -- --nocapture --test-threads=1 \
  >>"$LOG" 2>&1 &
TEST_PID=$!

# Watcher: hard-kill the test after TIMEOUT_SECS.
HUNG=0
(
  sleep "$TIMEOUT_SECS"
  if kill -0 "$TEST_PID" 2>/dev/null; then
    echo "" >>"$LOG"
    echo "[!!! HUNG !!!] cargo test still running after ${TIMEOUT_SECS}s — killing." >>"$LOG"
    # Try to grab a sample of what the rush-cli child is doing before we kill it.
    PIDS_RUSH=$(pgrep -f 'target/debug/rush-cli' || true)
    if [[ -n "$PIDS_RUSH" ]]; then
      echo "" >>"$LOG"
      echo "[diagnostic] rush-cli children: $PIDS_RUSH" >>"$LOG"
      for p in $PIDS_RUSH; do
        echo "" >>"$LOG"
        echo "--- ps $p ---" >>"$LOG"
        ps -p "$p" -o pid,ppid,stat,wchan,command >>"$LOG" 2>&1
        echo "" >>"$LOG"
        echo "--- lsof $p (head 30) ---" >>"$LOG"
        lsof -p "$p" 2>/dev/null | head -30 >>"$LOG"
        echo "" >>"$LOG"
        echo "--- sample $p (1s) ---" >>"$LOG"
        # `sample` ships with macOS, no sudo needed for own processes.
        sample "$p" 1 -mayDie >>"$LOG" 2>&1 || echo "(sample failed)" >>"$LOG"
      done
    fi
    pkill -P "$TEST_PID" 2>/dev/null
    kill -9 "$TEST_PID" 2>/dev/null
    pkill -9 -f 'target/debug/rush-cli' 2>/dev/null
    echo "HUNG" >/tmp/rush-investigate-hung
  fi
) &
WATCH_PID=$!

wait "$TEST_PID" 2>/dev/null
TEST_RC=$?
kill "$WATCH_PID" 2>/dev/null
wait "$WATCH_PID" 2>/dev/null

END=$(date +%s)
ELAPSED=$((END - START))

if [[ -f /tmp/rush-investigate-hung ]]; then
  HUNG=1
  rm -f /tmp/rush-investigate-hung
fi

# Summary.
echo "" | tee -a "$LOG"
echo "===================================================================="  | tee -a "$LOG"
if [[ $HUNG -eq 1 ]]; then
  echo "RESULT: HUNG after ${ELAPSED}s. See diagnostic block above for ps/lsof/sample." | tee -a "$LOG"
elif [[ $TEST_RC -eq 0 ]]; then
  echo "RESULT: PASSED in ${ELAPSED}s — the macOS hang was GitHub-runner-specific!"  | tee -a "$LOG"
  echo "        We can flip the cfg back to cfg(unix) permanently."  | tee -a "$LOG"
else
  echo "RESULT: FAILED (rc=$TEST_RC) in ${ELAPSED}s — fast failure is the good case."  | tee -a "$LOG"
  echo "        Look at the panic message above to identify which step blocked." | tee -a "$LOG"
fi
echo "===================================================================="  | tee -a "$LOG"
echo "" | tee -a "$LOG"
echo "Log written to: $LOG" | tee -a "$LOG"
echo "" | tee -a "$LOG"
echo "Next: send the log back so I can analyze it." | tee -a "$LOG"
echo "  git add .macos-pty-log.txt" | tee -a "$LOG"
echo "  git commit -m 'macos pty investigation log'" | tee -a "$LOG"
echo "  git push" | tee -a "$LOG"

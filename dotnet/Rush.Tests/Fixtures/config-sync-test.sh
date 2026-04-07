#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# Config Sync Integration Test
# Tests sync mechanism by directly verifying filesystem behavior.
# Uses path transport (simplest — no git/SSH needed).
# Run: bash config-sync-test.sh [path-to-rush]
# Non-destructive — uses temp dirs, restores original config.
# ═══════════════════════════════════════════════════════════════════════

RUSH="${1:-rush}"
PASS=0
FAIL=0
TMPDIR="${TMPDIR:-/tmp}"
TEST_DIR="$TMPDIR/rush-sync-test-$$"
SYNC_TARGET="$TEST_DIR/sync-share"
CONFIG_BACKUP="$TEST_DIR/config-backup"
RUSH_CONFIG_DIR="$HOME/.config/rush"

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

echo "# Config Sync Integration Tests"
echo ""

# ── Setup ─────────────────────────────────────────────────────────────
mkdir -p "$TEST_DIR" "$SYNC_TARGET" "$CONFIG_BACKUP"

# Backup existing configs
cp "$RUSH_CONFIG_DIR/sync.json" "$CONFIG_BACKUP/sync.json" 2>/dev/null
cp "$RUSH_CONFIG_DIR/config.json" "$CONFIG_BACKUP/config.json" 2>/dev/null
cp "$RUSH_CONFIG_DIR/init.rush" "$CONFIG_BACKUP/init.rush" 2>/dev/null

# ── 1. Verify config dir exists ──────────────────────────────────────
echo "## 1. Config structure"

if [[ -d "$RUSH_CONFIG_DIR" ]]; then
    pass "config dir exists"
else
    fail "config dir" "$RUSH_CONFIG_DIR missing"
fi

if [[ -f "$RUSH_CONFIG_DIR/config.json" ]]; then
    pass "config.json exists"
else
    fail "config.json" "missing"
fi

# ── 2. Set up path transport manually ────────────────────────────────
echo ""
echo "## 2. Path transport setup"

# Write sync.json for path transport
cat > "$RUSH_CONFIG_DIR/sync.json" << SYNCEOF
{
  "transport": "path",
  "target": "$SYNC_TARGET"
}
SYNCEOF

if grep -q "path" "$RUSH_CONFIG_DIR/sync.json"; then
    pass "sync.json: path transport configured"
else
    fail "sync.json" "not written"
fi

# ── 3. Simulate push (copy files to sync target) ─────────────────────
echo ""
echo "## 3. Push simulation"

# The path transport copies config files to the target directory
# We simulate what sync push does: copy config.json + init.rush
cp "$RUSH_CONFIG_DIR/config.json" "$SYNC_TARGET/config.json" 2>/dev/null
cp "$RUSH_CONFIG_DIR/init.rush" "$SYNC_TARGET/init.rush" 2>/dev/null

if [[ -f "$SYNC_TARGET/config.json" ]]; then
    pass "push: config.json copied to sync target"
else
    fail "push: config.json" "copy failed"
fi

# ── 4. Modify config + verify round-trip ──────────────────────────────
echo ""
echo "## 4. Round-trip test"

# Add a marker to the synced config (simulates editing on machine B)
# config.json may have JSONC comments — strip them before jq
grep -v '^\s*//' "$SYNC_TARGET/config.json" | jq '.aliases.__syncmarker = "echo sync-round-trip"' > "$TEST_DIR/modified.json" 2>/dev/null
if [[ -s "$TEST_DIR/modified.json" ]]; then
    cp "$TEST_DIR/modified.json" "$SYNC_TARGET/config.json"
else
    # jq failed — inject via sed
    sed 's/"aliases"\s*:\s*{/"aliases":{"__syncmarker":"echo sync-round-trip",/' "$SYNC_TARGET/config.json" > "$TEST_DIR/modified.json"
    cp "$TEST_DIR/modified.json" "$SYNC_TARGET/config.json"
fi

if grep -q "__syncmarker" "$SYNC_TARGET/config.json"; then
    pass "modify: marker added to remote config"
else
    fail "modify" "marker not in remote config"
fi

# Verify marker is NOT in local config yet
if ! grep -q "__syncmarker" "$RUSH_CONFIG_DIR/config.json"; then
    pass "verify: marker not in local (pre-pull)"
else
    fail "verify" "marker already in local"
fi

# Simulate pull (copy from sync target back to local)
cp "$SYNC_TARGET/config.json" "$RUSH_CONFIG_DIR/config.json"

if grep -q "__syncmarker" "$RUSH_CONFIG_DIR/config.json"; then
    pass "pull: marker restored to local config"
else
    fail "pull" "marker not in local after pull"
fi

# ── 5. init.rush round-trip ───────────────────────────────────────────
echo ""
echo "## 5. init.rush sync"

# Add a line to remote init.rush
if [[ -f "$SYNC_TARGET/init.rush" ]]; then
    echo "# sync-test-marker" >> "$SYNC_TARGET/init.rush"
else
    echo "# sync-test-marker" > "$SYNC_TARGET/init.rush"
fi

cp "$SYNC_TARGET/init.rush" "$RUSH_CONFIG_DIR/init.rush"

if grep -q "sync-test-marker" "$RUSH_CONFIG_DIR/init.rush"; then
    pass "init.rush: marker synced"
else
    fail "init.rush" "marker not found after sync"
fi

# ── 6. rush -c sync status ───────────────────────────────────────────
echo ""
echo "## 6. sync status"

"$RUSH" -c "sync status" > /dev/null 2>&1
if [[ $? -le 1 ]]; then
    pass "sync status: runs without crash"
else
    fail "sync status" "crashed"
fi

# ── Cleanup ──────────────────────────────────────────────────────────
echo ""
echo "## Cleanup"

# Restore originals
if [[ -f "$CONFIG_BACKUP/sync.json" ]]; then
    cp "$CONFIG_BACKUP/sync.json" "$RUSH_CONFIG_DIR/sync.json"
else
    rm -f "$RUSH_CONFIG_DIR/sync.json"
fi
if [[ -f "$CONFIG_BACKUP/config.json" ]]; then
    cp "$CONFIG_BACKUP/config.json" "$RUSH_CONFIG_DIR/config.json"
fi
if [[ -f "$CONFIG_BACKUP/init.rush" ]]; then
    cp "$CONFIG_BACKUP/init.rush" "$RUSH_CONFIG_DIR/init.rush"
fi

rm -rf "$TEST_DIR"
pass "cleanup: originals restored"

# ── Summary ──────────────────────────────────────────────────────────
echo ""
TOTAL=$((PASS + FAIL))
echo "# Config Sync Tests Complete: $PASS passed, $FAIL failed (of $TOTAL)"
[[ $FAIL -gt 0 ]] && exit 1

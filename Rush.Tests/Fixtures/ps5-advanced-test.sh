#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# ps5 Advanced Test Suite (#141)
# Tests complex ps5 block scenarios based on real-world COI experience.
# Run: bash ps5-advanced-test.sh [windows-host]
# Default: buster (must have Rush + PS 5.1)
# ═══════════════════════════════════════════════════════════════════════

RUSH="${RUSH:-rush}"
HOST="${1:-buster}"
PASS=0
FAIL=0

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1 — $2"; FAIL=$((FAIL + 1)); }

mcp_ssh() {
    local input=""
    for req in "$@"; do input+="$req"$'\n'; done
    echo "$input" | "$RUSH" --mcp-ssh 2>/dev/null
}

jf() { echo "$1" | jq -r "$2" 2>/dev/null; }

find_resp() {
    local lines="$1" id="$2"
    echo "$lines" | while IFS= read -r line; do
        local rid=$(echo "$line" | jq -r '.id // empty' 2>/dev/null)
        [[ "$rid" == "$id" ]] && echo "$line" && return
    done
}

tool_field() {
    local text=$(jf "$1" '.result.content[0].text')
    echo "$text" | jq -r "$2" 2>/dev/null
}

# Helper: run a Rush script on the remote host via exec_script
run_rush_script() {
    local script="$1"
    local b64=$(echo -n "$script" | base64)
    local output=$(mcp_ssh "$INIT" \
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_exec_script\",\"arguments\":{\"host\":\"$HOST\",\"filename\":\"test.rush\",\"content\":\"$b64\"}}}")
    find_resp "$output" 2
}

# Helper: run a ps5 script on the remote host via exec_script
run_ps5_script() {
    local script="$1"
    local rush_wrapper="ps5
$script
end"
    run_rush_script "$rush_wrapper"
}

INIT='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'

echo "# ps5 Advanced Tests → $HOST"
echo ""

# Verify Windows + Rush
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts os\"}}}")
resp=$(find_resp "$output" 2)
remote_os=$(tool_field "$resp" '.stdout' | tr -d '\r\n')

if [[ "$remote_os" != "windows" ]]; then
    echo "SKIP: $HOST is $remote_os (need Windows)"
    exit 0
fi

# ═══════════════════════════════════════════════════════════════════════
echo "## 1. Multi-line ps5 with loops"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $result = @()
  foreach ($i in 1..5) {
    $result += "item-$i"
  }
  $env:PS5_LOOP = $result -join ","
end
puts env.PS5_LOOP')
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "item-1,item-2,item-3,item-4,item-5" ]]; then
    pass "ps5 loop: foreach 1..5"
else
    fail "ps5 loop" "got $stdout — $(tool_field "$resp" '.stderr')"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 2. ps5 try/catch error handling"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  try {
    $null = 1 / 0
    $env:PS5_TRY = "no-error"
  } catch {
    $env:PS5_TRY = "caught: $($_.Exception.Message)"
  }
end
puts env.PS5_TRY')
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -qi "caught"; then
    pass "ps5 try/catch: caught division error"
else
    fail "ps5 try/catch" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 3. ps5 ForEach-Object pipeline"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $env:PS5_PIPE = (1..10 | Where-Object { $_ % 2 -eq 0 } | ForEach-Object { $_ * 10 }) -join ","
end
puts env.PS5_PIPE')
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "20,40,60,80,100" ]]; then
    pass "ps5 pipeline: Where + ForEach"
else
    fail "ps5 pipeline" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 4. ps5 structured output (JSON)"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $obj = @{
    name = "rush-test"
    version = 1
    features = @("ssh", "mcp", "ps5")
  }
  $env:PS5_JSON = ($obj | ConvertTo-Json -Compress)
end
puts env.PS5_JSON')
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | jq -e '.name' >/dev/null 2>&1; then
    name=$(echo "$stdout" | jq -r '.name')
    if [[ "$name" == "rush-test" ]]; then
        pass "ps5 JSON: structured output survives"
    else
        fail "ps5 JSON: name" "got $name"
    fi
else
    fail "ps5 JSON" "not valid JSON: $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 5. ps5 nested if blocks"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $x = 15
  if ($x -gt 20) {
    $env:PS5_NESTED = "high"
  } elseif ($x -gt 10) {
    if ($x -gt 14) {
      $env:PS5_NESTED = "mid-high"
    } else {
      $env:PS5_NESTED = "mid-low"
    }
  } else {
    $env:PS5_NESTED = "low"
  }
end
puts env.PS5_NESTED')
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "mid-high" ]]; then
    pass "ps5 nested if: correct branch"
else
    fail "ps5 nested if" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 6. ps5 error propagation (Write-Error)"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  Write-Error "test-error-message" -ErrorAction SilentlyContinue
  $env:PS5_ERR = "completed"
end
puts env.PS5_ERR')
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "completed" ]]; then
    pass "ps5 error: Write-Error doesn't crash block"
else
    fail "ps5 error" "got $stdout — $(tool_field "$resp" '.stderr')"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 7. ps5 special characters in output (AD-style DNs)"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $dn = "CN=John Doe,OU=SBSUsers,OU=Users,OU=MyBusiness,DC=ContinentalOptical,DC=local"
  $env:PS5_DN = $dn
end
puts env.PS5_DN')
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "CN=John Doe" && echo "$stdout" | grep -q "DC=local"; then
    pass "ps5 special chars: AD DN survives round-trip"
else
    fail "ps5 special chars" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 8. ps5 CSV output"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $data = @(
    [PSCustomObject]@{ Name = "Alice"; Role = "Admin" }
    [PSCustomObject]@{ Name = "Bob"; Role = "User" }
  )
  $env:PS5_CSV = ($data | ConvertTo-Csv -NoTypeInformation) -join "|"
end
puts env.PS5_CSV')
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "Alice" && echo "$stdout" | grep -q "Bob"; then
    pass "ps5 CSV: structured table output"
else
    fail "ps5 CSV" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 9. ps5 string interpolation"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $host_name = $env:COMPUTERNAME
  $proc_count = (Get-Process).Count
  $env:PS5_INTERP = "Host: $host_name, Processes: $proc_count"
end
puts env.PS5_INTERP')
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "Host:" && echo "$stdout" | grep -q "Processes:"; then
    pass "ps5 interpolation: host + process count"
else
    fail "ps5 interpolation" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 10. Rush variable bridging into ps5"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'server = "prod01"
port = 8080
ps5
  $env:PS5_BRIDGE = "$server running on port $port"
end
puts env.PS5_BRIDGE')
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "prod01" && echo "$stdout" | grep -q "8080"; then
    pass "ps5 bridge: Rush vars in ps5 interpolation"
else
    fail "ps5 bridge" "got $stdout — $(tool_field "$resp" '.stderr')"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 11. ps5 after Rush code in same script"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'x = 42
puts "before: #{x}"
ps5
  $env:PS5_AFTER = "ps5 ran"
end
puts env.PS5_AFTER
puts "after: #{x}"')
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "before: 42" && echo "$stdout" | grep -q "ps5 ran" && echo "$stdout" | grep -q "after: 42"; then
    pass "ps5 interleaved: Rush → ps5 → Rush"
else
    fail "ps5 interleaved" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 12. Session persistence: repeated ps5 blocks (#144)"
# ═══════════════════════════════════════════════════════════════════════

# Run 5 sequential ps5 blocks in the same session — verify state doesn't degrade
output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:PS5_SEQ1 = 'seq1'\\nend\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:PS5_SEQ2 = 'seq2'\\nend\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:PS5_SEQ3 = 'seq3'\\nend\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:PS5_SEQ4 = 'seq4'\\nend\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts \\\"#{env.PS5_SEQ1},#{env.PS5_SEQ2},#{env.PS5_SEQ3},#{env.PS5_SEQ4}\\\"\"}}}")
resp=$(find_resp "$output" 6)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "seq1,seq2,seq3,seq4" ]]; then
    pass "session persistence: 4 sequential ps5 blocks"
else
    fail "session persistence" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 13. Rush vars survive ps5 block (#144)"
# ═══════════════════════════════════════════════════════════════════════

output=$(mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"before_ps5 = \\\"still here\\\"\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps5\\n  \$env:PS5_MIDDLE = 'middle'\\nend\"}}}" \
    "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"puts before_ps5\"}}}")
resp=$(find_resp "$output" 4)
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "still here" ]]; then
    pass "Rush var survives ps5 block"
else
    fail "Rush var after ps5" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 14. Large ps5 output (#145)"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $lines = @()
  for ($i = 1; $i -le 100; $i++) {
    $lines += "line-$i: some data here for testing large output"
  }
  $env:PS5_LARGE = $lines.Count.ToString()
end
puts env.PS5_LARGE')
stdout=$(tool_field "$resp" '.stdout')

if [[ "$stdout" == "100" ]]; then
    pass "ps5 large output: 100 lines generated"
else
    fail "ps5 large output" "got $stdout"
fi

# ═══════════════════════════════════════════════════════════════════════
echo ""
echo "## 15. ps5 output with equals/commas/backslashes (#145)"
# ═══════════════════════════════════════════════════════════════════════

resp=$(run_rush_script 'ps5
  $special = "key=value, path=C:\Users\test, OU=Users,DC=local"
  $env:PS5_SPECIAL = $special
end
puts env.PS5_SPECIAL')
stdout=$(tool_field "$resp" '.stdout')

if echo "$stdout" | grep -q "key=value" && echo "$stdout" | grep -q "DC=local"; then
    pass "ps5 special output: equals, commas, backslashes"
else
    fail "ps5 special output" "got $stdout"
fi

# ── Cleanup ──────────────────────────────────────────────────────────
mcp_ssh "$INIT" \
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"rush_execute\",\"arguments\":{\"host\":\"$HOST\",\"command\":\"ps\\n  @('PS5_LOOP','PS5_TRY','PS5_PIPE','PS5_JSON','PS5_NESTED','PS5_ERR','PS5_DN','PS5_CSV','PS5_INTERP','PS5_BRIDGE','PS5_AFTER','PS5_SEQ1','PS5_SEQ2','PS5_SEQ3','PS5_SEQ4','PS5_MIDDLE','PS5_LARGE','PS5_SPECIAL') | ForEach-Object { Remove-Item \\\"Env:\\\\\$_\\\" -ErrorAction SilentlyContinue }\\nend\"}}}" >/dev/null 2>&1

echo ""
TOTAL=$((PASS + FAIL))
echo "# ps5 Advanced Tests Complete ($HOST): $PASS passed, $FAIL failed (of $TOTAL)"
[[ $FAIL -gt 0 ]] && exit 1

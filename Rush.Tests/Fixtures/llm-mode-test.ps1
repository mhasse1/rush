# ═══════════════════════════════════════════════════════════════════════
# Rush --llm Mode Test Suite (Windows)
# Tests the JSON wire protocol used by LLM agents and MCP servers.
# Run: powershell -ExecutionPolicy Bypass -File llm-mode-test.ps1 [path-to-rush]
# Non-destructive — safe for production servers.
# ═══════════════════════════════════════════════════════════════════════

param([string]$Rush = "rush")

$Pass = 0
$Fail = 0
$TestDir = Join-Path $env:TEMP "rush-llm-test-$PID"
New-Item -ItemType Directory -Path $TestDir -Force | Out-Null

# ── Helpers ───────────────────────────────────────────────────────────

function Pass($msg) { Write-Host "PASS: $msg"; $script:Pass++ }
function Fail($msg, $detail) { Write-Host "FAIL: $msg — $detail"; $script:Fail++ }

# Send commands to rush --llm, return array of JSON lines
function Invoke-LlmSession {
    param([string[]]$Commands)
    $cmdText = ($Commands -join "`n") + "`n"

    # Write commands to a temp file and pipe it in — more reliable than stdin redirection
    $tmpIn = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($tmpIn, $cmdText, [System.Text.Encoding]::UTF8)

    try {
        $stdout = Get-Content $tmpIn -Raw | & $Rush --llm 2>$null
        if ($null -eq $stdout) { return @() }
        if ($stdout -is [string]) {
            return $stdout -split "`n" | Where-Object { $_.Trim() -ne "" }
        } else {
            return $stdout | Where-Object { $_.Trim() -ne "" }
        }
    } finally {
        Remove-Item $tmpIn -Force -ErrorAction SilentlyContinue
    }
}

# Parse a JSON line
function Parse-Json($line) {
    try { return $line | ConvertFrom-Json } catch { return $null }
}

# ── Tests ─────────────────────────────────────────────────────────────

Write-Host "# Rush --llm Mode Tests (Windows)"
Write-Host ""

# ── 1. Startup Context ───────────────────────────────────────────────
Write-Host "## 1. Startup Context"

$lines = Invoke-LlmSession @("echo done")
$ctx = Parse-Json $lines[0]

if ($ctx.ready -eq $true) { Pass "context: ready=true" } else { Fail "context: ready" "got $($ctx.ready)" }
if ($ctx.shell -eq "rush") { Pass "context: shell=rush" } else { Fail "context: shell" "got $($ctx.shell)" }
if ($ctx.host) { Pass "context: host is set ($($ctx.host))" } else { Fail "context: host" "empty" }
if ($ctx.cwd) { Pass "context: cwd is set" } else { Fail "context: cwd" "empty" }
if ($ctx.version) { Pass "context: version is set ($($ctx.version))" } else { Fail "context: version" "empty" }

# ── 2. Simple Command Execution ──────────────────────────────────────
Write-Host ""
Write-Host "## 2. Command Execution"

$lines = Invoke-LlmSession @("echo hello world")
$result = Parse-Json $lines[1]

if ($result.status -eq "success") { Pass "echo: status=success" } else { Fail "echo: status" "got $($result.status)" }
if ($result.exit_code -eq 0) { Pass "echo: exit_code=0" } else { Fail "echo: exit_code" "got $($result.exit_code)" }
if ("$($result.stdout)" -match "hello world") { Pass "echo: stdout correct" } else { Fail "echo: stdout" "got $($result.stdout)" }
if ($null -ne $result.duration_ms) { Pass "echo: duration_ms present" } else { Fail "echo: duration_ms" "missing" }

# ── 3. Error Handling ────────────────────────────────────────────────
Write-Host ""
Write-Host "## 3. Error Handling"

$lines = Invoke-LlmSession @("command_that_does_not_exist_xyz")
$result = Parse-Json $lines[1]

if ($result.status -eq "error") { Pass "bad command: status=error" } else { Fail "bad command: status" "got $($result.status)" }
if ($result.exit_code -ne 0) { Pass "bad command: exit_code != 0" } else { Fail "bad command: exit_code" "got 0" }

# ── 4. Rush Syntax ───────────────────────────────────────────────────
Write-Host ""
Write-Host "## 4. Rush Syntax"

$lines = Invoke-LlmSession @('puts "hello from rush"')
$result = Parse-Json $lines[1]

if ("$($result.stdout)" -match "hello from rush") { Pass "rush puts: output correct" } else { Fail "rush puts" "got $($result.stdout)" }

# Variable assignment + interpolation (two commands = 4 lines: ctx, result, ctx, result)
$lines = Invoke-LlmSession @('x = 42', 'puts "x is #{x}"')
$result = Parse-Json $lines[3]

if ("$($result.stdout)" -match "x is 42") { Pass "rush interpolation: x is 42" } else { Fail "rush interpolation" "got $($result.stdout)" }

# ── 5. Multi-line Command (JSON-quoted) ──────────────────────────────
Write-Host ""
Write-Host "## 5. Multi-line (JSON-quoted)"

$lines = Invoke-LlmSession @('"sum = 0\nfor i in [1,2,3]\n  sum = sum + i\nend\nputs sum"')
$result = Parse-Json $lines[1]

if ("$($result.stdout)" -match "6") { Pass "multi-line: for loop sum = 6" } else { Fail "multi-line" "got $($result.stdout)" }

# ── 6. JSON Envelope ─────────────────────────────────────────────────
Write-Host ""
Write-Host "## 6. JSON Envelope"

$lines = Invoke-LlmSession @('{"cmd":"echo envelope works"}')
$result = Parse-Json $lines[1]

if ("$($result.stdout)" -match "envelope works") { Pass "envelope: cmd executes" } else { Fail "envelope: cmd" "got $($result.stdout)" }

# Envelope with cwd
$lines = Invoke-LlmSession @('{"cmd":"pwd","cwd":"C:/Windows/Temp"}')
$result = Parse-Json $lines[1]

if ("$($result.stdout)" -match "Temp") { Pass "envelope: cwd=C:/Windows/Temp" } else { Fail "envelope: cwd" "got $($result.stdout)" }

# Envelope with env
$lines = Invoke-LlmSession @('{"cmd":"echo $env:RUSH_TEST_ENV_WIN","env":{"RUSH_TEST_ENV_WIN":"envelope_env"}}')
$result = Parse-Json $lines[1]

if ("$($result.stdout)" -match "envelope_env") { Pass "envelope: env var injection" } else { Fail "envelope: env" "got $($result.stdout)" }

# Bad envelope
$lines = Invoke-LlmSession @('{"not_cmd":"missing"}')
$result = Parse-Json $lines[1]

if ($result.status -eq "error") { Pass "envelope: missing cmd returns error" } else { Fail "envelope: missing cmd" "got $($result.status)" }

# ── 7. File Transfer: Put/Get ────────────────────────────────────────
Write-Host ""
Write-Host "## 7. File Transfer"

$testFile = "$TestDir/transfer-test.txt".Replace('\', '/')
$b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes("hello from transfer"))

$lines = Invoke-LlmSession @("{`"transfer`":`"put`",`"path`":`"$testFile`",`"content`":`"$b64`"}")
$result = Parse-Json $lines[1]

if ($result.status -eq "success") { Pass "transfer put: status=success" } else { Fail "transfer put" "got $($result.status) — $($result.stderr)" }

# Get the file back
$lines = Invoke-LlmSession @("{`"transfer`":`"get`",`"path`":`"$testFile`"}")
$result = Parse-Json $lines[1]

if ($result.status -eq "success") { Pass "transfer get: status=success" } else { Fail "transfer get" "got $($result.status)" }
if ("$($result.content)" -match "hello from transfer") { Pass "transfer get: content matches" } else { Fail "transfer get: content" "got $($result.content)" }
if ($result.encoding -eq "utf8") { Pass "transfer get: encoding=utf8" } else { Fail "transfer get: encoding" "got $($result.encoding)" }

# Get missing file
$lines = Invoke-LlmSession @("{`"transfer`":`"get`",`"path`":`"$TestDir/nonexistent.txt`"}")
$result = Parse-Json $lines[1]

if ($result.status -eq "error") { Pass "transfer get missing: status=error" } else { Fail "transfer get missing" "got $($result.status)" }

# ── 8. Transfer: Exec Script ────────────────────────────────────────
Write-Host ""
Write-Host "## 8. Transfer Exec"

$script = 'Write-Host "exec test output"
Write-Host "args: $($args -join '' '')"'
$scriptB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($script))

$lines = Invoke-LlmSession @("{`"transfer`":`"exec`",`"filename`":`"test.ps1`",`"content`":`"$scriptB64`",`"args`":[`"--flag`",`"value`"],`"cleanup`":true}")
$result = Parse-Json $lines[1]

if ($result.status -eq "success") { Pass "transfer exec: status=success" } else { Fail "transfer exec" "got $($result.status) — $($result.stderr)" }
if ("$($result.stdout)" -match "exec test output") { Pass "transfer exec: stdout correct" } else { Fail "transfer exec: stdout" "got $($result.stdout)" }

# ── 9. Builtins ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "## 9. Builtins"

# lcat
Set-Content -Path "$TestDir/lcat-test.txt" -Value "lcat test content" -NoNewline
$lcatPath = "$TestDir/lcat-test.txt".Replace('\', '/')

$lines = Invoke-LlmSession @("lcat $lcatPath")
$result = Parse-Json $lines[1]

if ($result.status -eq "success") { Pass "lcat: status=success" } else { Fail "lcat" "got $($result.status)" }
if ($result.mime -eq "text/plain") { Pass "lcat: mime=text/plain" } else { Fail "lcat: mime" "got $($result.mime)" }
if ("$($result.content)" -match "lcat test content") { Pass "lcat: content correct" } else { Fail "lcat: content" "got $($result.content)" }

# help
$lines = Invoke-LlmSession @("help file")
$result = Parse-Json $lines[1]

if ($result.status -eq "success") { Pass "help: status=success" } else { Fail "help" "got $($result.status)" }
if ($result.stdout) { Pass "help: has output" } else { Fail "help: output" "empty" }

# ── 10. TTY Blocklist ────────────────────────────────────────────────
Write-Host ""
Write-Host "## 10. TTY Blocklist"

$lines = Invoke-LlmSession @("vim")
$result = Parse-Json $lines[1]

if ($result.error_type -eq "tty_required") { Pass "tty blocklist: vim blocked" } else { Fail "tty blocklist: vim" "got error_type=$($result.error_type)" }

# ── 11. Exit Code Tracking ──────────────────────────────────────────
Write-Host ""
Write-Host "## 11. Exit Code Tracking"

$lines = Invoke-LlmSession @("exit 1")
$result = Parse-Json $lines[1]
$ctx2 = Parse-Json $lines[2]

if ($result.exit_code -ne 0) { Pass "exit code: non-zero after exit 1" } else { Fail "exit code" "got 0" }
if ($ctx2.last_exit_code -ne 0) { Pass "context: last_exit_code tracks failure" } else { Fail "context: last_exit_code" "got $($ctx2.last_exit_code)" }

# ── 12. Backward Compatibility ──────────────────────────────────────
Write-Host ""
Write-Host "## 12. Backward Compatibility"

$lines = Invoke-LlmSession @("echo plain_text")
$result = Parse-Json $lines[1]
if ("$($result.stdout)" -match "plain_text") { Pass "compat: plain text" } else { Fail "compat: plain text" "got $($result.stdout)" }

$lines = Invoke-LlmSession @('"echo json_quoted"')
$result = Parse-Json $lines[1]
if ("$($result.stdout)" -match "json_quoted") { Pass "compat: JSON-quoted string" } else { Fail "compat: JSON-quoted" "got $($result.stdout)" }

$lines = Invoke-LlmSession @('{"cmd":"echo json_envelope"}')
$result = Parse-Json $lines[1]
if ("$($result.stdout)" -match "json_envelope") { Pass "compat: JSON envelope" } else { Fail "compat: JSON envelope" "got $($result.stdout)" }

# ── 13. Windows-Specific ─────────────────────────────────────────────
Write-Host ""
Write-Host "## 13. Windows-Specific"

# PowerShell cmdlet via ps block
$lines = Invoke-LlmSession @('ps
  $env:RUSH_TEST_WIN13 = (Get-Process | Measure-Object).Count.ToString()
end', 'puts env.RUSH_TEST_WIN13')
$result = Parse-Json $lines[3]

if ("$($result.stdout)" -match "^\d+$") { Pass "ps block: Get-Process count ($($result.stdout))" } else { Fail "ps block: Get-Process" "got $($result.stdout)" }

# Platform detection
$lines = Invoke-LlmSession @('puts os')
$result = Parse-Json $lines[1]

if ("$($result.stdout)" -match "windows") { Pass "platform: os=windows" } else { Fail "platform: os" "got $($result.stdout)" }

# ── Cleanup ──────────────────────────────────────────────────────────
Remove-Item -Recurse -Force $TestDir -ErrorAction SilentlyContinue

# ── Summary ──────────────────────────────────────────────────────────
Write-Host ""
$Total = $Pass + $Fail
Write-Host "# LLM Mode Tests Complete: $Pass passed, $Fail failed (of $Total)"
if ($Fail -gt 0) { exit 1 }

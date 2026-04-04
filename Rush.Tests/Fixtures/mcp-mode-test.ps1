# ═══════════════════════════════════════════════════════════════════════
# Rush --mcp Mode Test Suite (Windows)
# Tests the MCP (Model Context Protocol) JSON-RPC 2.0 server.
# Run: powershell -ExecutionPolicy Bypass -File mcp-mode-test.ps1 [path-to-rush]
# Non-destructive — safe for production servers.
# ═══════════════════════════════════════════════════════════════════════

param([string]$Rush = "rush")

$Pass = 0
$Fail = 0
$TestDir = Join-Path $env:TEMP "rush-mcp-test-$PID"
New-Item -ItemType Directory -Path $TestDir -Force | Out-Null

# ── Helpers ───────────────────────────────────────────────────────────

function Pass($msg) { Write-Host "PASS: $msg"; $script:Pass++ }
function Fail($msg, $detail) { Write-Host "FAIL: $msg — $detail"; $script:Fail++ }

# Send JSON-RPC requests to rush --mcp, return array of JSON lines
function Invoke-McpSession {
    param([string[]]$Requests)
    $reqText = ($Requests -join "`n") + "`n"

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Rush
    $psi.Arguments = "--mcp"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    # Write each request as a separate line (JSON-RPC requires line-delimited input)
    foreach ($req in $Requests) {
        $proc.StandardInput.WriteLine($req)
    }
    $proc.StandardInput.Close()

    $stdout = $proc.StandardOutput.ReadToEnd()
    if (-not $proc.WaitForExit(30000)) {
        try { $proc.Kill() } catch {}
    }

    if ([string]::IsNullOrEmpty($stdout)) { return @() }
    return $stdout -split "`n" | Where-Object { $_.Trim() -ne "" }
}

function Parse-Json($line) {
    try { return $line | ConvertFrom-Json } catch { return $null }
}

# Find a JSON-RPC response by id from an array of output lines
function Find-Response($lines, [int]$id) {
    foreach ($l in $lines) {
        $parsed = Parse-Json $l
        if ($parsed -and $parsed.id -eq $id) { return $parsed }
    }
    return $null
}

# Extract the text content from an MCP tool result (nested JSON in content[0].text)
function Get-ToolResult($response) {
    $text = $response.result.content[0].text
    if ($text) { return $text | ConvertFrom-Json } else { return $null }
}

$InitReq = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'

# ── Tests ─────────────────────────────────────────────────────────────

Write-Host "# Rush --mcp Mode Tests (Windows)"
Write-Host ""

# ── 1. Initialize ────────────────────────────────────────────────────
Write-Host "## 1. Initialize"

$lines = Invoke-McpSession @($InitReq)
$init = Find-Response $lines 1

if ($init.result.protocolVersion -eq "2024-11-05") { Pass "init: protocolVersion" } else { Fail "init: protocolVersion" "got $($init.result.protocolVersion)" }
if ($init.result.serverInfo.name -eq "rush-local") { Pass "init: server name=rush-local" } else { Fail "init: server name" "got $($init.result.serverInfo.name)" }
if ($init.result.serverInfo.version) { Pass "init: version ($($init.result.serverInfo.version))" } else { Fail "init: version" "missing" }
if ($null -ne $init.result.capabilities.tools) { Pass "init: tools capability" } else { Fail "init: tools" "missing" }
if ($null -ne $init.result.capabilities.resources) { Pass "init: resources capability" } else { Fail "init: resources" "missing" }
if ($init.result.instructions) { Pass "init: instructions present" } else { Fail "init: instructions" "missing" }

# ── 2. Tools List ────────────────────────────────────────────────────
Write-Host ""
Write-Host "## 2. Tools List"

$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
)
$tools = Find-Response $lines 2

$toolCount = $tools.result.tools.Count
if ($toolCount -ge 3) { Pass "tools/list: $toolCount tools" } else { Fail "tools/list" "got $toolCount, expected >= 3" }

foreach ($name in @("rush_execute", "rush_read_file", "rush_context")) {
    $found = $tools.result.tools | Where-Object { $_.name -eq $name }
    if ($found) { Pass "tools/list: $name present" } else { Fail "tools/list" "$name missing" }
}

# Check schema
$execTool = $tools.result.tools | Where-Object { $_.name -eq "rush_execute" }
if ($execTool.inputSchema.properties.command) { Pass "tools/list: rush_execute has command param" } else { Fail "tools/list: schema" "missing command" }

# ── 3. rush_execute ──────────────────────────────────────────────────
Write-Host ""
Write-Host "## 3. rush_execute"

$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"echo mcp hello"}}}'
)
$resp = Find-Response $lines 2
$tr = Get-ToolResult $resp

if ($tr.status -eq "success") { Pass "rush_execute: status=success" } else { Fail "rush_execute: status" "got $($tr.status) — $($tr.stderr)" }
if ("$($tr.stdout)" -match "mcp hello") { Pass "rush_execute: stdout correct" } else { Fail "rush_execute: stdout" "got $($tr.stdout)" }
if ($resp.result.isError -eq $false) { Pass "rush_execute: isError=false" } else { Fail "rush_execute: isError" "got $($resp.result.isError)" }

# Rush syntax
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"puts \"rush syntax via mcp\""}}}'
)
$resp = Find-Response $lines 2
$tr = Get-ToolResult $resp

if ("$($tr.stdout)" -match "rush syntax via mcp") { Pass "rush_execute: Rush syntax works" } else { Fail "rush_execute: Rush syntax" "got $($tr.stdout)" }

# Error command
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"nonexistent_cmd_xyz_123"}}}'
)
$resp = Find-Response $lines 2
$tr = Get-ToolResult $resp

if ($tr.status -eq "error") { Pass "rush_execute: error status" } else { Fail "rush_execute: error" "got $($tr.status)" }
if ($resp.result.isError -eq $true) { Pass "rush_execute: isError=true" } else { Fail "rush_execute: isError" "got $($resp.result.isError)" }

# ── 4. rush_read_file ────────────────────────────────────────────────
Write-Host ""
Write-Host "## 4. rush_read_file"

$testFile = "$TestDir/mcp-read-test.txt".Replace('\', '/')
Set-Content -Path "$TestDir/mcp-read-test.txt" -Value "mcp file content" -NoNewline

$lines = Invoke-McpSession @(
    $InitReq,
    "{`"jsonrpc`":`"2.0`",`"id`":2,`"method`":`"tools/call`",`"params`":{`"name`":`"rush_read_file`",`"arguments`":{`"path`":`"$testFile`"}}}"
)
$resp = Find-Response $lines 2
$tr = Get-ToolResult $resp

if ($tr.status -eq "success") { Pass "rush_read_file: status=success" } else { Fail "rush_read_file: status" "got $($tr.status)" }
if ("$($tr.content)" -match "mcp file content") { Pass "rush_read_file: content correct" } else { Fail "rush_read_file: content" "got $($tr.content)" }
if ($tr.mime -eq "text/plain") { Pass "rush_read_file: mime=text/plain" } else { Fail "rush_read_file: mime" "got $($tr.mime)" }
if ($tr.encoding -eq "utf8") { Pass "rush_read_file: encoding=utf8" } else { Fail "rush_read_file: encoding" "got $($tr.encoding)" }

# Missing file
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_read_file","arguments":{"path":"C:/nonexistent/file.txt"}}}'
)
$resp = Find-Response $lines 2
$tr = Get-ToolResult $resp

if ($tr.status -eq "error") { Pass "rush_read_file: missing file returns error" } else { Fail "rush_read_file: missing" "got $($tr.status)" }

# ── 5. rush_context ──────────────────────────────────────────────────
Write-Host ""
Write-Host "## 5. rush_context"

$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_context","arguments":{}}}'
)
$resp = Find-Response $lines 2
$tr = Get-ToolResult $resp

if ($tr.ready -eq $true) { Pass "rush_context: ready=true" } else { Fail "rush_context: ready" "got $($tr.ready)" }
if ($tr.host) { Pass "rush_context: host ($($tr.host))" } else { Fail "rush_context: host" "missing" }
if ($tr.cwd) { Pass "rush_context: cwd present" } else { Fail "rush_context: cwd" "missing" }
if ($tr.shell -eq "rush") { Pass "rush_context: shell=rush" } else { Fail "rush_context: shell" "got $($tr.shell)" }

# ── 6. Resources ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "## 6. Resources"

$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"resources/list"}'
)
$resp = Find-Response $lines 2

$resCount = $resp.result.resources.Count
if ($resCount -ge 1) { Pass "resources/list: $resCount resources" } else { Fail "resources/list" "got $resCount" }

$langSpec = $resp.result.resources | Where-Object { $_.uri -eq "rush://lang-spec" }
if ($langSpec) { Pass "resources/list: rush://lang-spec present" } else { Fail "resources/list" "rush://lang-spec missing" }

# Read the lang spec
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"rush://lang-spec"}}'
)
$resp = Find-Response $lines 2

if ("$($resp.result.contents[0].text)" -match "Rush Language") { Pass "resources/read: lang-spec has content" } else { Fail "resources/read" "no content" }
if ($resp.result.contents[0].mimeType -eq "text/yaml") { Pass "resources/read: mimeType=text/yaml" } else { Fail "resources/read: mime" "got $($resp.result.contents[0].mimeType)" }

# ── 7. Error Handling ────────────────────────────────────────────────
Write-Host ""
Write-Host "## 7. JSON-RPC Error Handling"

# Unknown method
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"nonexistent/method"}'
)
$resp = Find-Response $lines 2

if ($resp.error.code -eq -32601) { Pass "error: unknown method returns -32601" } else { Fail "error: unknown method" "got code $($resp.error.code)" }

# Unknown tool
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"nonexistent_tool","arguments":{}}}'
)
$resp = Find-Response $lines 2

if ($resp.error.code -eq -32602) { Pass "error: unknown tool returns -32602" } else { Fail "error: unknown tool" "got code $($resp.error.code)" }

# Missing required parameter
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{}}}'
)
$resp = Find-Response $lines 2

if ($resp.error.code -eq -32602) { Pass "error: missing param returns -32602" } else { Fail "error: missing param" "got code $($resp.error.code)" }

# ── 8. State Persistence ─────────────────────────────────────────────
Write-Host ""
Write-Host "## 8. State Persistence"

$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"x = 99"}}}',
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"puts x"}}}'
)
$resp = Find-Response $lines 3
$tr = Get-ToolResult $resp

if ("$($tr.stdout)" -match "99") { Pass "state: variables persist across calls" } else { Fail "state: persistence" "got $($tr.stdout)" }

# ── 9. Windows-Specific ──────────────────────────────────────────────
Write-Host ""
Write-Host "## 9. Windows-Specific"

# Execute a Windows-native command
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"hostname"}}}'
)
$resp = Find-Response $lines 2
$tr = Get-ToolResult $resp

if ($tr.status -eq "success" -and $tr.stdout) { Pass "windows: hostname returns $($tr.stdout.Trim())" } else { Fail "windows: hostname" "got $($tr.status)" }

# PowerShell via rush
$lines = Invoke-McpSession @(
    $InitReq,
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rush_execute","arguments":{"command":"ps\n  $env:RUSH_MCP_WIN = $PSVersionTable.PSVersion.ToString()\nend\nputs env.RUSH_MCP_WIN"}}}'
)
$resp = Find-Response $lines 2
$tr = Get-ToolResult $resp

if ("$($tr.stdout)" -match "^\d+\.") { Pass "windows: PSVersion via ps block ($($tr.stdout))" } else { Fail "windows: PSVersion" "got $($tr.stdout)" }

# ── Cleanup ──────────────────────────────────────────────────────────
Remove-Item -Recurse -Force $TestDir -ErrorAction SilentlyContinue

# ── Summary ──────────────────────────────────────────────────────────
Write-Host ""
$Total = $Pass + $Fail
Write-Host "# MCP Mode Tests Complete: $Pass passed, $Fail failed (of $Total)"
if ($Fail -gt 0) { exit 1 }

# Build + publish rush-ps-bridge as a single-file self-contained binary.
#
# Usage:
#   pwsh ./build.ps1                 # detect RID automatically
#   pwsh ./build.ps1 -Rid win-x64    # explicit RID
#   pwsh ./build.ps1 -Install        # also copy to $env:USERPROFILE/bin
#
# Tested on Windows 11 + .NET 10.0.202.

[CmdletBinding()]
param(
    [string]$Rid = "",
    [string]$Configuration = "Release",
    [switch]$Install
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

if ([string]::IsNullOrEmpty($Rid)) {
    $Rid = switch ($true) {
        $IsWindows { "win-x64" }
        $IsMacOS   { if ([System.Environment]::ProcessorArchitecture -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' } }
        default    { "linux-x64" }
    }
}

Write-Host "Publishing rush-ps-bridge for $Rid..."
dotnet publish `
    -c $Configuration `
    -r $Rid `
    -p:PublishSingleFile=true `
    --self-contained `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$exeName = if ($IsWindows -or $Rid -like "win-*") { "rush-ps.exe" } else { "rush-ps" }
$publishDir = Join-Path -Path "bin" -ChildPath "$Configuration/net10.0/$Rid/publish"
$binary = Join-Path -Path $publishDir -ChildPath $exeName

if (-not (Test-Path $binary)) {
    Write-Error "Published binary not found at $binary"
    exit 1
}

$size = [math]::Round((Get-Item $binary).Length / 1MB, 1)
Write-Host "Built: $binary (${size} MB)"

if ($Install) {
    $installDir = if ($IsWindows) {
        Join-Path -Path $env:USERPROFILE -ChildPath "bin"
    } else {
        "/usr/local/bin"
    }
    if (-not (Test-Path $installDir)) {
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    }
    $target = Join-Path -Path $installDir -ChildPath $exeName
    Copy-Item -Path $binary -Destination $target -Force
    Write-Host "Installed: $target"

    if ($IsWindows) {
        $currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
        if ($currentPath -notlike "*$installDir*") {
            Write-Host "Note: $installDir is not on your user PATH."
            Write-Host "      Add via: setx PATH `"$currentPath;$installDir`""
        }
    }
}

#!/usr/bin/env bash
# Quick Windows build on buster — both x64 + arm64
# Copies arm64 to Resilio for faust deployment
set -euo pipefail

echo "Building Windows binaries on buster..."
ssh buster "\$env:PATH = \"\$env:LOCALAPPDATA\\Microsoft\\dotnet;C:\\Program Files\\Git\\cmd;\$env:PATH\"
cd C:\\src\\rush
& 'C:\\Program Files\\Git\\cmd\\git.exe' pull --quiet 2>\$null
Write-Host 'Building win-x64...'
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o C:\\temp\\rush-build-x64 2>&1 | Select-Object -Last 1
Write-Host 'Building win-arm64...'
dotnet publish -c Release -r win-arm64 -p:PublishSingleFile=true -p:SkipCleanCheck=true -o C:\\temp\\rush-build-arm64 2>&1 | Select-Object -Last 1
Write-Host 'Done'
"

echo "Downloading..."
mkdir -p dist/native
scp -q buster:C:/temp/rush-build-x64/rush.exe dist/native/rush-win-x64.exe
scp -q buster:C:/temp/rush-build-arm64/rush.exe dist/native/rush-win-arm64.exe

# Deploy to buster
echo "Deploying to buster..."
ssh buster 'Copy-Item C:\temp\rush-build-x64\rush.exe C:\bin\rush.exe -Force' 2>/dev/null
echo "  buster: $(ssh buster 'C:\bin\rush.exe --version' 2>/dev/null | tr -d '\r')"

# Copy arm64 to Resilio for faust
if [[ -d "$HOME/Resilio/coi/_rush" ]]; then
    cp dist/native/rush-win-arm64.exe "$HOME/Resilio/coi/_rush/rush_arm64.exe"
    cp dist/native/rush-win-x64.exe "$HOME/Resilio/coi/_rush/rush_x64.exe"
    echo "  faust: copied to Resilio (deploy via Datto or manual copy)"
fi

echo "Done."

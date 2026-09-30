#!/usr/bin/env bash
# Baut das Pilotpaket für Windows (x64) auf dem Mac: Tests, dann Dienst und App als eigenständige .exe.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
name="SIEntryDesk-$version-win-x64"
out="artifacts/$name"
rm -rf "$out" "artifacts/$name.zip"

dotnet test tests/SIEntryDesk.Core.Tests --nologo -v quiet

common=(-c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none --nologo -v quiet)
dotnet publish src/SIEntryDesk.Service "${common[@]}" -o "$out/Service"
dotnet publish src/SIEntryDesk.App "${common[@]}" -o "$out/App"
cp deploy/install.ps1 deploy/uninstall.ps1 deploy/set-tokens.ps1 deploy/sientrydesk.example.json deploy/PILOT.md "$out/"
mkdir -p "$out/intune-zugaenge"
cp deploy/intune-zugaenge/set-tokens-intune.ps1 deploy/intune-zugaenge/tokens.example.txt "$out/intune-zugaenge/"

(cd artifacts && zip -qr "$name.zip" "$name")
echo "Paket: artifacts/$name.zip ($(du -h "artifacts/$name.zip" | cut -f1))"

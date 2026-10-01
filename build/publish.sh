#!/usr/bin/env bash
# Baut die Pakete für Windows (x64) auf dem Mac: Tests, dann Dienst und App als eigenständige .exe.
# Zwei getrennte Pakete, damit Zugänge nie ins Programmpaket geraten:
#   SIEntryDesk-<Version>-win-x64.zip   Programm und Installationsskripte (Intune-App „SI EntryDesk“)
#   SIEntryDesk-Zugaenge-<Version>.zip  nur das Skript und die Vorlage für die Zugänge (Intune-App „SI EntryDesk Zugänge“)
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
name="SIEntryDesk-$version-win-x64"
out="artifacts/$name"
tokens="SIEntryDesk-Zugaenge-$version"
tokensOut="artifacts/$tokens"
rm -rf "$out" "artifacts/$name.zip" "$tokensOut" "artifacts/$tokens.zip"

dotnet test tests/SIEntryDesk.Core.Tests --nologo -v quiet

common=(-c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none --nologo -v quiet)
dotnet publish src/SIEntryDesk.Service "${common[@]}" -o "$out/Service"
dotnet publish src/SIEntryDesk.App "${common[@]}" -o "$out/App"
cp deploy/install.ps1 deploy/uninstall.ps1 deploy/set-tokens.ps1 deploy/sientrydesk.example.json deploy/PILOT.md "$out/"
mkdir -p "$tokensOut"
cp deploy/intune-zugaenge/set-tokens-intune.ps1 deploy/intune-zugaenge/tokens.example.txt "$tokensOut/"

# Sicherung: kein tokens.txt und keine Anlagen-Konfiguration in einem Paket.
if find "$out" "$tokensOut" \( -name tokens.txt -o -name sientrydesk.json \) | grep -q .; then
  echo "ABBRUCH: tokens.txt oder sientrydesk.json im Paket" >&2
  exit 1
fi

(cd artifacts && zip -qr "$name.zip" "$name" && zip -qr "$tokens.zip" "$tokens")
echo "Programm: artifacts/$name.zip ($(du -h "artifacts/$name.zip" | cut -f1))"
echo "Zugänge:  artifacts/$tokens.zip ($(du -h "artifacts/$tokens.zip" | cut -f1))"

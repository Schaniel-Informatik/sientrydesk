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
# deploy/ enthält nur ausgelieferte Dateien, die Anleitungen für Admins kommen aus docs/admin nach Doku/.
cp deploy/programm/* "$out/"
mkdir -p "$out/Doku" "$tokensOut"
cp docs/admin/*.md THIRD-PARTY-NOTICES.md "$out/Doku/"
cp deploy/zugaenge/* "$tokensOut/"

# Sicherung: Die ZIP-Dateien sind für alle Anlagen gleich und können später öffentlich werden. Die sientrydesk.json
# einer Anlage und die tokens.txt legt der Admin erst beim Packen für Intune in den entpackten Ordner.
if find "$out" "$tokensOut" \( -name tokens.txt -o -name sientrydesk.json \) | grep -q .; then
  echo "ABBRUCH: tokens.txt oder sientrydesk.json im Paket" >&2
  exit 1
fi

(cd artifacts && zip -qr "$name.zip" "$name" && zip -qr "$tokens.zip" "$tokens")
# Nur die ZIP-Dateien behalten, die entpackten Ordner braucht es nach dem Packen nicht mehr.
rm -rf "$out" "$tokensOut"

# Ältere Pakete als die zuletzt erfolgreich getestete Version löschen. Diese steht in artifacts/GETESTET (lokal,
# wird nach einem bestandenen Test gesetzt). Ohne die Datei bleibt alles. Alte Versionen lassen sich aus Git neu bauen.
if [[ -f artifacts/GETESTET ]]; then
  tested=$(tr -d '[:space:]' < artifacts/GETESTET)
  for zip in artifacts/SIEntryDesk-*.zip; do
    v=$(basename "$zip" .zip | sed -e 's/^SIEntryDesk-Zugaenge-//' -e 's/^SIEntryDesk-//' -e 's/-win-x64$//')
    if [[ "$v" != "$tested" && "$(printf '%s\n%s\n' "$v" "$tested" | sort -V | head -1)" == "$v" ]]; then
      rm -f "$zip"
    fi
  done
fi
echo "Programm: artifacts/$name.zip ($(du -h "artifacts/$name.zip" | cut -f1))"
echo "Zugänge:  artifacts/$tokens.zip ($(du -h "artifacts/$tokens.zip" | cut -f1))"

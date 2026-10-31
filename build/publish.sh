#!/usr/bin/env bash
# Builds the packages for Windows (x64) on the Mac: tests, then service and app as self-contained .exe.
# Two separate packages, so that credentials never end up in the program package:
#   SIEntryDesk-<Version>-win-x64.zip   program and install scripts (Intune app "SI EntryDesk")
#   SIEntryDesk-Tokens-<Version>.zip    script and template for the credentials (Intune app "SI EntryDesk Zugänge"),
#                                       own version from build/tokens-version.txt
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
name="SIEntryDesk-$version-win-x64"
out="artifacts/$name"
# Credentials package with its own version (build/tokens-version.txt), only new if deploy/tokens really changed.
read -r tokensVersion tokensHash < <(grep -v '^#' build/tokens-version.txt | grep -v '^$' | tail -1)
currentHash=$(cd deploy/tokens && find . -type f ! -name '.*' -print0 | sort -z | xargs -0 shasum -a 256 | shasum -a 256 | cut -c1-64)
if [[ "$currentHash" != "$tokensHash" ]]; then
  echo "ABBRUCH: deploy/tokens hat sich geändert, Zugänge-Version $tokensVersion aber nicht." >&2
  echo "Version erhöhen und in build/tokens-version.txt anfügen: <neue Version> $currentHash" >&2
  exit 1
fi
tokens="SIEntryDesk-Tokens-$tokensVersion"
tokensOut="artifacts/$tokens"
rm -rf "$out" "artifacts/$name.zip" "$tokensOut" "artifacts/$tokens.zip"

dotnet test tests/SIEntryDesk.Core.Tests --nologo -v quiet

common=(-c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none --nologo -v quiet)
dotnet publish src/SIEntryDesk.Service "${common[@]}" -o "$out/Service"
dotnet publish src/SIEntryDesk.App "${common[@]}" -o "$out/App"
# deploy/ contains only shipped files, the admin guides come from docs/admin into Doku/.
cp deploy/programm/* "$out/"
mkdir -p "$out/Doku" "$tokensOut"
cp docs/admin/*.md THIRD-PARTY-NOTICES.md "$out/Doku/"
cp deploy/tokens/* "$tokensOut/"

# Safeguard: the ZIP files are the same for all installations and are public. The sientrydesk.json of an
# installation and the tokens.txt are put into the unpacked folder by the admin only when packing for Intune.
if find "$out" "$tokensOut" \( -name tokens.txt -o -name sientrydesk.json \) | grep -q .; then
  echo "ABBRUCH: tokens.txt oder sientrydesk.json im Paket" >&2
  exit 1
fi

(cd artifacts && zip -qr "$name.zip" "$name" && zip -qr "$tokens.zip" "$tokens")
# Keep only the current version of the credentials package.
for zip in artifacts/SIEntryDesk-Tokens-*.zip; do
  [[ "$zip" == "artifacts/$tokens.zip" ]] || rm -f "$zip"
done

# Keep only the ZIP files, the unpacked folders are not needed after packing.
rm -rf "$out" "$tokensOut"

# Delete packages older than the last successfully tested version. It is in artifacts/GETESTET (local, set after a
# passed test). Without the file everything stays. Old versions can be rebuilt from Git.
if [[ -f artifacts/GETESTET ]]; then
  tested=$(tr -d '[:space:]' < artifacts/GETESTET)
  for zip in artifacts/SIEntryDesk-[0-9]*.zip; do
    v=$(basename "$zip" .zip | sed -e 's/^SIEntryDesk-Tokens-//' -e 's/^SIEntryDesk-//' -e 's/-win-x64$//')
    if [[ "$v" != "$tested" && "$(printf '%s\n%s\n' "$v" "$tested" | sort -V | head -1)" == "$v" ]]; then
      rm -f "$zip"
    fi
  done
fi
echo "Programm: artifacts/$name.zip ($(du -h "artifacts/$name.zip" | cut -f1))"
echo "Zugänge:  artifacts/$tokens.zip ($(du -h "artifacts/$tokens.zip" | cut -f1))"

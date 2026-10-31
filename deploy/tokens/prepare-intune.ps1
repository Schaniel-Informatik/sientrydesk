<#
.SYNOPSIS
    Bereitet das Intune-Paket „SI EntryDesk Zugänge“ vor: prüft tokens.txt, erstellt die .intunewin-Datei und löscht
    tokens.txt danach.

.DESCRIPTION
    Im entpackten Paket der Zugänge ausführen, nachdem tokens.txt nach der Vorlage tokens.example.txt angelegt ist.
      1. Prüft tokens.txt (wie bei der Installation) und dass im Ordner nur die nötigen Dateien liegen.
      2. Erstellt mit IntuneWinAppUtil.exe den Ordner Intune-SIEntryDesk-Zugaenge-<Kennung> neben diesem Ordner,
         darin SIEntryDesk-Zugaenge-<Kennung>.intunewin.
      3. Löscht tokens.txt. Ab dann stehen die Zugänge nur noch verschlüsselt im Intune-Paket.
      4. Zeigt die Einstellungen für Intune.

    IntuneWinAppUtil.exe: mit -IntuneWinAppUtil angeben, sonst sucht das Skript im Suchpfad, neben diesem Ordner und
    unter Downloads.

.EXAMPLE
    Doppelklick auf prepare-intune.cmd (fragt nach der Kennung), oder:
    powershell -ExecutionPolicy Bypass -File .\prepare-intune.ps1 -Label 2026-09
#>
param(
    [string] $Label,
    [string] $IntuneWinAppUtil
)

$ErrorActionPreference = 'Stop'
$folder = $PSScriptRoot
$parent = Split-Path $folder -Parent
$tokensFile = Join-Path $folder 'tokens.txt'

function Find-IntuneWinAppUtil([string] $Given) {
    if ($Given) {
        if (Test-Path $Given) { return (Resolve-Path $Given).Path }
        throw "IntuneWinAppUtil nicht gefunden: $Given"
    }
    $command = Get-Command 'IntuneWinAppUtil.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($dir in $parent, $folder, (Join-Path $env:USERPROFILE 'Downloads')) {
        $candidate = Get-ChildItem -Path $dir -Filter 'IntuneWinAppUtil.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    $picked = Select-File 'IntuneWinAppUtil.exe auswählen' 'IntuneWinAppUtil.exe|IntuneWinAppUtil.exe'
    if ($picked) { return $picked }
    throw ('IntuneWinAppUtil.exe nicht gefunden. Von https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool ' +
           'herunterladen und neben diesen Ordner legen, oder mit -IntuneWinAppUtil <Pfad> angeben.')
}

# Dateiauswahl, wenn das Skript per Doppelklick (prepare-intune.cmd) läuft. Ohne Fenster (z. B. über SSH) null.
function Select-File([string] $Title, [string] $Filter) {
    if (-not [Environment]::UserInteractive) { return $null }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = $Title
        $dialog.Filter = $Filter
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.FileName }
    } catch {
    }
    return $null
}


if (-not $Label) {
    $suggested = Get-Date -Format 'yyyy-MM'
    $answer = Read-Host "Kennung der Zugänge (Jahr-Monat, Enter = $suggested)"
    $Label = if ($answer) { $answer.Trim() } else { $suggested }
}
if ($Label -notmatch '^[A-Za-z0-9._-]{1,32}$') { throw 'Kennung ungültig, z. B. 2026-09' }

Write-Host '1/4 tokens.txt prüfen'
$tool = Find-IntuneWinAppUtil $IntuneWinAppUtil
if (-not (Test-Path $tokensFile)) { throw 'tokens.txt fehlt. tokens.example.txt als tokens.txt kopieren und ausfüllen.' }
$expected = 'set-tokens-intune.ps1', 'tokens.txt', 'tokens.example.txt', 'prepare-intune.ps1', 'prepare-intune.cmd'
$extra = Get-ChildItem $folder -Force | Where-Object { $expected -notcontains $_.Name }
if ($extra) { throw "Im Ordner liegt mehr als nötig: $($extra.Name -join ', '). Nur das entpackte Paket der Zugänge und tokens.txt." }
& (Join-Path $folder 'set-tokens-intune.ps1') -Check

$out = Join-Path $parent "Intune-SIEntryDesk-Zugaenge-$Label"
$packageFile = "SIEntryDesk-Zugaenge-$Label.intunewin"
try {
    Write-Host '2/4 Intune-Paket erstellen'
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Path $out | Out-Null
    & $tool -c $folder -s 'set-tokens-intune.ps1' -o $out -q | Out-Null
    $intunewin = Join-Path $out 'set-tokens-intune.intunewin'
    if (-not (Test-Path $intunewin)) { throw "IntuneWinAppUtil hat keine .intunewin erzeugt (Code $LASTEXITCODE)." }
    Rename-Item $intunewin $packageFile
} catch {
    Write-Warning "tokens.txt liegt noch in $folder. Löschen, falls du hier abbrichst."
    throw
}

Write-Host '3/4 tokens.txt löschen'
Remove-Item -Force $tokensFile

Write-Host '4/4 Einstellungen für Intune (Apps > Windows > Erstellen > Windows-App (Win32))'
@(
    "    Paketdatei             $(Join-Path $out $packageFile)"
    "    Name                   SI EntryDesk Zugänge $Label"
    "    Installationsbefehl    powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Label $Label"
    "    Deinstallationsbefehl  powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Remove"
    "    Installationsverhalten System"
    "    Anforderungen          64 Bit, Windows 11"
    "    Erkennungsregel        Manuell, Registrierung: HKEY_LOCAL_MACHINE\SOFTWARE\SIEntryDesk, Wertname TokenLabel,"
    "                           Zeichenfolgenvergleich, Ist gleich $Label, 32-Bit-App auf 64-Bit-Clients: Nein"
    "    Abhängigkeiten         SI EntryDesk, automatisch installieren (in dieser App, nicht in der Programm-App)"
    "    Ablösung               keine. Bei der jährlichen Erneuerung löst die neue App die alte ab, ohne Deinstallation"
    "    Zuweisung              Erforderlich, dieselbe Gerätegruppe"
) | Write-Host

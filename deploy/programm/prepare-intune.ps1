<#
.SYNOPSIS
    Bereitet das Intune-Paket „SI EntryDesk“ vor: prüft den Paketordner, erzeugt detect.ps1 und die .intunewin-Datei.

.DESCRIPTION
    Im entpackten Programmpaket ausführen, nachdem die sientrydesk.json der Anlage neben install.ps1 liegt.
      1. Prüft: Programmdateien vollständig, sientrydesk.json lesbar mit Host und AccessPin, keine tokens.txt.
      2. Erzeugt detect.ps1 im Paketordner, das Erkennungsskript für Intune. install.ps1 legt es auch nach
         C:\Program Files\SIEntryDesk, dort zeigt es am Gerät dasselbe wie Intune.
      3. Erstellt mit IntuneWinAppUtil.exe den Ordner Intune-SIEntryDesk-<Version> neben dem Paketordner, darin
         SIEntryDesk-<Version>.intunewin und detect.ps1 zum Hochladen.
      4. Zeigt die Einstellungen für Intune.

    IntuneWinAppUtil.exe (Microsoft Win32 Content Prep Tool): mit -IntuneWinAppUtil angeben, sonst sucht das Skript
    im Suchpfad, neben dem Paketordner und unter Downloads.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\prepare-intune.ps1
#>
param(
    [string] $IntuneWinAppUtil
)

$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
$parent = Split-Path $package -Parent

function Find-IntuneWinAppUtil([string] $Given) {
    if ($Given) {
        if (Test-Path $Given) { return (Resolve-Path $Given).Path }
        throw "IntuneWinAppUtil nicht gefunden: $Given"
    }
    $command = Get-Command 'IntuneWinAppUtil.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($dir in $parent, $package, (Join-Path $env:USERPROFILE 'Downloads')) {
        $candidate = Get-ChildItem -Path $dir -Filter 'IntuneWinAppUtil.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    throw ('IntuneWinAppUtil.exe nicht gefunden. Von https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool ' +
           'herunterladen und neben den Paketordner legen, oder mit -IntuneWinAppUtil <Pfad> angeben.')
}

Write-Host '1/4 Paket prüfen'
$tool = Find-IntuneWinAppUtil $IntuneWinAppUtil
$missing = 'install.ps1', 'uninstall.ps1', 'set-tokens.ps1', 'App\SIEntryDesk.exe', 'Service\SIEntryDesk.Service.exe' |
    Where-Object { -not (Test-Path (Join-Path $package $_)) }
if ($missing) { throw "Im Paketordner fehlt: $($missing -join ', ')" }
if (Get-ChildItem $package -Recurse -Filter 'tokens.txt') {
    throw 'tokens.txt gehört nicht ins Programmpaket. Hier löschen, sie gehört nur ins Paket der Zugänge.'
}
$expected = 'App', 'Service', 'install.ps1', 'uninstall.ps1', 'set-tokens.ps1', 'prepare-intune.ps1', 'detect.ps1',
            'sientrydesk.json', 'sientrydesk.example.json', 'Doku'
$extra = Get-ChildItem $package -Force | Where-Object { $expected -notcontains $_.Name }
if ($extra) { throw "Im Paketordner liegt mehr als nötig: $($extra.Name -join ', '). Nur das entpackte Paket und sientrydesk.json." }

$configFile = Join-Path $package 'sientrydesk.json'
if (-not (Test-Path $configFile)) { throw 'sientrydesk.json fehlt neben install.ps1.' }
# Windows PowerShell 5.1 liest kein JSON mit Kommentaren, ganze Kommentarzeilen werden für die Prüfung entfernt.
$configText = (Get-Content -Raw -Encoding UTF8 $configFile) -replace '(?m)^\s*//.*$', ''
try { $config = $configText | ConvertFrom-Json }
catch { throw "sientrydesk.json ist kein gültiges JSON (Kommentare nur auf eigenen Zeilen): $($_.Exception.Message)" }
foreach ($required in 'Host', 'AccessPin') {
    if (-not $config.$required) { throw "sientrydesk.json: '$required' fehlt." }
}
$fileVersion = (Get-Item (Join-Path $package 'App\SIEntryDesk.exe')).VersionInfo.FileVersion
if (-not $fileVersion) { throw 'Version von SIEntryDesk.exe nicht lesbar.' }
$version = [version] $fileVersion
$shortVersion = $version.ToString(3)
$hash = (Get-FileHash -Algorithm SHA256 $configFile).Hash
Write-Host "    Version $shortVersion, Konsole $($config.Host)"

Write-Host '2/4 Erkennungsskript detect.ps1'
$created = Get-Date -Format 'yyyy-MM-dd HH:mm'
# Nur ASCII, damit Intune und Windows PowerShell 5.1 das Skript ohne Kodierungsfragen ausführen.
$detection = @"
# SI EntryDesk: Erkennungsskript fuer die Intune-App "SI EntryDesk".
# Erzeugt am $created mit prepare-intune.ps1 fuer Version $version und die sientrydesk.json dieses Pakets.
# Installiert = Dienst vorhanden, App mindestens Version $version, sientrydesk.json unveraendert aus dem Paket.
# Intune: Ausgabe und Exit-Code 0 = installiert. Ohne Ausgabe = nicht installiert, Intune installiert das Paket.
# Am Geraet als Administrator ausfuehren: zeigt dasselbe wie Intune, ohne Ausgabe fehlt etwas.

`$version    = [version] '$version'
`$configHash = '$hash'
`$exe        = Join-Path `$env:ProgramW6432 'SIEntryDesk\App\SIEntryDesk.exe'
`$config     = Join-Path `$env:ProgramData 'SIEntryDesk\sientrydesk.json'

try {
    if (-not (Get-Service -Name 'SIEntryDesk' -ErrorAction SilentlyContinue)) { exit 0 }
    if (-not (Test-Path -LiteralPath `$exe) -or -not (Test-Path -LiteralPath `$config)) { exit 0 }
    if ([version] (Get-Item -LiteralPath `$exe).VersionInfo.FileVersion -lt `$version) { exit 0 }
    if ((Get-FileHash -LiteralPath `$config -Algorithm SHA256).Hash -ne `$configHash) { exit 0 }
} catch {
    exit 0
}
Write-Output "SI EntryDesk `$version mit dieser Konfiguration installiert"
exit 0
"@
$detectFile = Join-Path $package 'detect.ps1'
[System.IO.File]::WriteAllText($detectFile, $detection.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.ASCIIEncoding))

Write-Host '3/4 Intune-Paket erstellen'
$out = Join-Path $parent "Intune-SIEntryDesk-$shortVersion"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out | Out-Null
& $tool -c $package -s 'install.ps1' -o $out -q | Out-Null
$intunewin = Join-Path $out 'install.intunewin'
if (-not (Test-Path $intunewin)) { throw "IntuneWinAppUtil hat keine .intunewin erzeugt (Code $LASTEXITCODE)." }
$packageFile = "SIEntryDesk-$shortVersion.intunewin"
Rename-Item $intunewin $packageFile
Copy-Item $detectFile $out

Write-Host '4/4 Einstellungen für Intune (Apps > Windows > Erstellen > Windows-App (Win32))'
@(
    "    Paketdatei             $(Join-Path $out $packageFile)"
    "    Name                   SI EntryDesk"
    "    App-Version            $shortVersion"
    "    Installationsbefehl    powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\install.ps1"
    "    Deinstallationsbefehl  powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\uninstall.ps1"
    "    Installationsverhalten System"
    "    Anforderungen          64 Bit, Windows 11"
    "    Erkennungsregel        Benutzerdefiniertes Skript: $(Join-Path $out 'detect.ps1')"
    "                           als 32-Bit-Prozess: Nein, Signaturprüfung erzwingen: Nein"
    "    Zuweisung              Erforderlich, Gerätegruppe"
) | Write-Host

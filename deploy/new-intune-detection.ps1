<#
.SYNOPSIS
    Erzeugt das Erkennungsskript für die Intune-App „SI EntryDesk“, passend zu diesem Paket und seiner sientrydesk.json.

.DESCRIPTION
    Im entpackten Programmpaket ausführen, nachdem die sientrydesk.json neben install.ps1 liegt. Schreibt
    SIEntryDesk-<Version>-Erkennung.ps1 neben den Paketordner, nicht hinein. Diese Datei in Intune als
    benutzerdefiniertes Erkennungsskript hochladen.

    Das Erkennungsskript meldet „installiert“, wenn
      - der Dienst SIEntryDesk vorhanden ist,
      - SIEntryDesk.exe mindestens die Version dieses Pakets hat,
      - die installierte sientrydesk.json genau der aus diesem Paket entspricht (SHA-256).
    So verteilt Intune auch eine geänderte sientrydesk.json bei gleicher Programmversion neu, z. B. neue Ablaufdaten.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\new-intune-detection.ps1
#>
param(
    [string] $ConfigFile = (Join-Path $PSScriptRoot 'sientrydesk.json')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'App\SIEntryDesk.exe'
if (-not (Test-Path $exe)) { throw "App\SIEntryDesk.exe fehlt. Das Skript im entpackten Programmpaket ausführen." }
if (-not (Test-Path $ConfigFile)) { throw "sientrydesk.json fehlt: $ConfigFile. Zuerst neben install.ps1 legen." }

$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
if (-not $fileVersion) { throw 'Version von SIEntryDesk.exe nicht lesbar.' }
$version = [version] $fileVersion
$hash = (Get-FileHash -Algorithm SHA256 $ConfigFile).Hash
$created = Get-Date -Format 'yyyy-MM-dd HH:mm'
$out = Join-Path (Split-Path $PSScriptRoot -Parent) "SIEntryDesk-$version-Erkennung.ps1"

# Nur ASCII, damit Intune und Windows PowerShell 5.1 das Skript ohne Kodierungsfragen ausführen.
$detection = @"
# SI EntryDesk: Erkennungsskript fuer die Intune-App "SI EntryDesk".
# Erzeugt am $created mit new-intune-detection.ps1 fuer Version $version und die sientrydesk.json dieses Pakets.
# Installiert = Dienst vorhanden, App mindestens Version $version, sientrydesk.json unveraendert aus dem Paket.
# Intune: Ausgabe und Exit-Code 0 = installiert. Ohne Ausgabe = nicht installiert, Intune installiert das Paket.

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

[System.IO.File]::WriteAllText($out, $detection.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.ASCIIEncoding))
Write-Host "Erkennungsskript für Intune: $out"
Write-Host "Version $version, sientrydesk.json SHA-256 $hash"

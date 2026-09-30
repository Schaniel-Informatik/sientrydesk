<#
.SYNOPSIS
    Intune: setzt die Zugänge von SI EntryDesk. Eigene Win32-App „SI EntryDesk Zugänge“, abhängig von „SI EntryDesk“.

.DESCRIPTION
    Liest tokens.txt aus demselben Ordner (UTF-8):
      Zeile 1: Access-Token (Gerät = Anzeigen, Standorte = Bearbeiten)
      Zeile 2: leer (optional eigener Token zum Öffnen)
      Zeile 3: Protect-API-Schlüssel (leer = kein Livebild)
    Speichert sie mit DPAPI über den Dienst, startet ihn neu und hinterlegt die Kennung unter
    HKLM\SOFTWARE\SIEntryDesk\TokenLabel für die Erkennungsregel in Intune.

    tokens.txt nur für das Erstellen des Pakets anlegen und danach löschen. Intune speichert den Inhalt des Pakets
    verschlüsselt, auf dem Gerät liegt er nur während der Installation im Zwischenspeicher der Intune-Erweiterung.

.EXAMPLE
    Installationsbefehl:   powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Label 2026-09
    Deinstallationsbefehl: powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Remove
#>
param(
    [string] $Label,
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'
$serviceExe = Join-Path $env:ProgramFiles 'SIEntryDesk\Service\SIEntryDesk.Service.exe'
$secrets    = Join-Path $env:ProgramData 'SIEntryDesk\secrets.dat'
$regPath    = 'HKLM:\SOFTWARE\SIEntryDesk'

if ($Remove) {
    Remove-Item -Force $secrets -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $regPath -Name 'TokenLabel' -ErrorAction SilentlyContinue
    if (Get-Service SIEntryDesk -ErrorAction SilentlyContinue) { Restart-Service SIEntryDesk }
    exit 0
}

if (-not $Label -or $Label -notmatch '^[A-Za-z0-9._-]{1,32}$') { throw 'Kennung fehlt oder ist ungültig, z. B. -Label 2026-09' }
if (-not (Test-Path $serviceExe)) { throw 'SI EntryDesk ist nicht installiert (Abhängigkeit in Intune prüfen).' }

$lines = @(Get-Content -Encoding UTF8 (Join-Path $PSScriptRoot 'tokens.txt'))
$access = if ($lines.Count -gt 0) { $lines[0].Trim() } else { '' }
if (-not $access) { throw 'tokens.txt: Zeile 1 (Access-Token) fehlt.' }
$unlock = if ($lines.Count -gt 1) { $lines[1].Trim() } else { '' }
$protect = if ($lines.Count -gt 2) { $lines[2].Trim() } else { '' }

"$access`n$unlock`n$protect" | & $serviceExe set-secrets | Out-Null
$code = $LASTEXITCODE
Remove-Variable access, unlock, protect, lines
if ($code -ne 0) { throw "Zugänge konnten nicht gespeichert werden (Code $code)" }

New-Item -Force -Path $regPath | Out-Null
Set-ItemProperty -Path $regPath -Name 'TokenLabel' -Value $Label
if (Get-Service SIEntryDesk -ErrorAction SilentlyContinue) { Restart-Service SIEntryDesk }
Write-Host "Zugänge gesetzt, Kennung $Label"

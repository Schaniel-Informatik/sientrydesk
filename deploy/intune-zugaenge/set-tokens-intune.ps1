<#
.SYNOPSIS
    Intune: setzt die Zugänge von SI EntryDesk. Eigene Win32-App „SI EntryDesk Zugänge“, abhängig von „SI EntryDesk“.

.DESCRIPTION
    Liest tokens.txt aus demselben Ordner (UTF-8, Vorlage tokens.example.txt). Leere Zeilen und Zeilen mit # zählen
    nicht, sonst gilt pro Zeile name=wert:
      access=   Access-Token (Gerät = Anzeigen, Standorte = Bearbeiten), Pflicht
      protect=  Protect-API-Schlüssel, leer oder weggelassen = kein Livebild
      unlock=   optional eigener Access-Token nur zum Öffnen, normalerweise weglassen
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
    [switch] $Remove,
    # Nur tokens.txt prüfen, nichts speichern (für prepare-intune.ps1).
    [switch] $Check
)

# Intune startet Installationsbefehle als 32-Bit-Prozess. Die 32-Bit-PowerShell sieht C:\Program Files (x86) und
# HKLM\SOFTWARE\WOW6432Node statt der richtigen Orte, deshalb hier in die 64-Bit-PowerShell wechseln.
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    $arguments = @('-ExecutionPolicy', 'Bypass', '-NoProfile', '-File', $PSCommandPath)
    foreach ($bound in $PSBoundParameters.GetEnumerator()) {
        if ($bound.Value -is [switch]) { if ($bound.Value) { $arguments += "-$($bound.Key)" } }
        else { $arguments += @("-$($bound.Key)", [string] $bound.Value) }
    }
    & (Join-Path $env:WINDIR 'Sysnative\WindowsPowerShell\v1.0\powershell.exe') @arguments
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'

# Fehlermeldungen nennen nur Zeilennummer und Namen, nie den Inhalt, damit kein Token im Intune-Protokoll landet.
function Read-TokenFile([string] $Path) {
    if (-not (Test-Path $Path)) { throw 'tokens.txt fehlt im Paket.' }
    $values = @{}
    $n = 0
    foreach ($raw in @(Get-Content -Encoding UTF8 $Path)) {
        $n++
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -lt 1) { throw "tokens.txt, Zeile ${n}: erwartet name=wert, z. B. access=... (Vorlage tokens.example.txt)." }
        $key = $line.Substring(0, $eq).Trim().ToLowerInvariant()
        $value = $line.Substring($eq + 1).Trim()
        if (@('access', 'unlock', 'protect') -notcontains $key) { throw "tokens.txt, Zeile ${n}: unbekannter Name, erlaubt sind access, protect und unlock." }
        if ($values.ContainsKey($key)) { throw "tokens.txt, Zeile ${n}: $key kommt doppelt vor." }
        if ($value -match '[\s<>"'']') { throw "tokens.txt, Zeile ${n}: Wert von $key enthält Leerzeichen, Anführungszeichen oder <>. Nur den Wert selbst einsetzen." }
        $values[$key] = $value
    }
    if (-not $values['access']) { throw 'tokens.txt: access= fehlt oder ist leer.' }
    return $values
}

if ($Check) {
    $checked = Read-TokenFile (Join-Path $PSScriptRoot 'tokens.txt')
    Write-Host "    tokens.txt in Ordnung: $(@($checked.Keys | Where-Object { $checked[$_] } | Sort-Object) -join ', ')"
    $checked.Clear()
    exit 0
}

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

$tokens = Read-TokenFile (Join-Path $PSScriptRoot 'tokens.txt')
"$($tokens['access'])`n$($tokens['unlock'])`n$($tokens['protect'])" | & $serviceExe set-secrets | Out-Null
$code = $LASTEXITCODE
$tokens.Clear()
Remove-Variable tokens
if ($code -ne 0) { throw "Zugänge konnten nicht gespeichert werden (Code $code)" }

New-Item -Force -Path $regPath | Out-Null
Set-ItemProperty -Path $regPath -Name 'TokenLabel' -Value $Label
if (Get-Service SIEntryDesk -ErrorAction SilentlyContinue) { Restart-Service SIEntryDesk }
Write-Host "Zugänge gesetzt, Kennung $Label"

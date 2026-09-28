#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installiert SI EntryDesk (Pilot): Dienst, Tray-App, Konfiguration und Tokens.

.DESCRIPTION
    - Dienst "SIEntryDesk" unter dem virtuellen Konto "NT SERVICE\SIEntryDesk" (nicht SYSTEM)
    - Daten unter %ProgramData%\SIEntryDesk, lesbar nur für SYSTEM, Administratoren und den Dienst
    - Tokens werden verdeckt abgefragt und mit DPAPI (Maschinenschlüssel) verschlüsselt abgelegt
    - Tray-App startet bei jeder Anmeldung
    - Livebild nur mit -ProtectPin und Protect-API-Schlüssel. Ohne Klingeln über das Tray-Menü, mit -NoLiveView
      auf diesem PC abgeschaltet. Jeder Abruf steht mit Benutzer im Protokoll des Dienstes.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -ConsoleHost 192.0.2.10 -AccessPin 'AA:BB:…' -ProtectPin 'CC:DD:…' -Doors 'Eingang'
#>
param(
    [Parameter(Mandatory)] [string] $ConsoleHost,
    [Parameter(Mandatory)] [string] $AccessPin,
    [string[]] $Doors = @(),
    [string] $ProtectPin = '',
    [string] $StreamPin = '',
    [switch] $NoLiveView,
    [ValidateRange(15, 600)] [int] $LiveViewSeconds = 90,
    [switch] $KeepTokens
)

$ErrorActionPreference = 'Stop'
$serviceName    = 'SIEntryDesk'
$serviceAccount = "NT SERVICE\$serviceName"
$installDir     = Join-Path $env:ProgramFiles 'SIEntryDesk'
$dataDir        = Join-Path $env:ProgramData 'SIEntryDesk'
$logDir         = Join-Path $dataDir 'logs'
$stateDir       = Join-Path $dataDir 'state'
$serviceExe     = Join-Path $installDir 'Service\SIEntryDesk.Service.exe'
$appExe         = Join-Path $installDir 'App\SIEntryDesk.exe'

function Invoke-Native([string] $Exe, [string[]] $Arguments) {
    & $Exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') ist fehlgeschlagen (Code $LASTEXITCODE)" }
}

Write-Host '1/7 Laufende Instanzen beenden'
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { Stop-Service $serviceName -Force }
Get-Process -Name 'SIEntryDesk' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host '2/7 Dateien kopieren'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
foreach ($part in 'Service', 'App') {
    $target = Join-Path $installDir $part
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item -Recurse -Force (Join-Path $PSScriptRoot $part) $target
}
Get-ChildItem $installDir -Recurse -File | Unblock-File

Write-Host '3/7 Dienst einrichten'
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $serviceName -BinaryPathName "`"$serviceExe`"" -DisplayName 'SI EntryDesk' `
        -Description 'Klingel-Ereignisse von UniFi Access, Öffnen nur während eines Rufs' -StartupType Automatic | Out-Null
}
Invoke-Native 'sc.exe' @('config', $serviceName, 'obj=', $serviceAccount, 'start=', 'auto')
Invoke-Native 'sc.exe' @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/30000')

Write-Host '4/7 Datenordner und Rechte'
New-Item -ItemType Directory -Force -Path $dataDir, $logDir, $stateDir | Out-Null
# Nur SYSTEM (S-1-5-18), Administratoren (S-1-5-32-544) und das Dienstkonto, keine Vererbung von ProgramData.
Invoke-Native 'icacls.exe' @($dataDir, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "${serviceAccount}:(OI)(CI)RX")
Invoke-Native 'icacls.exe' @($logDir, '/grant:r', "${serviceAccount}:(OI)(CI)M")
Invoke-Native 'icacls.exe' @($stateDir, '/grant:r', "${serviceAccount}:(OI)(CI)M")

Write-Host '5/7 Konfiguration schreiben'
[ordered]@{ Host = $ConsoleHost; AccessPin = $AccessPin; Doors = @($Doors); ProtectPin = $ProtectPin; StreamPin = $StreamPin
    LiveView = -not $NoLiveView; LiveViewSeconds = $LiveViewSeconds } |
    ConvertTo-Json | Set-Content -Encoding UTF8 -Path (Join-Path $dataDir 'config.json')

Write-Host '6/7 Tokens'
if ($KeepTokens -and (Test-Path (Join-Path $dataDir 'secrets.dat'))) {
    Write-Host '    Vorhandene Tokens bleiben.'
} else {
    function Read-Secret([string] $Prompt) {
        $secure = Read-Host -AsSecureString $Prompt
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    $access = Read-Secret 'Access-Token (Gerät: Anzeigen)'
    if (-not $access) { throw 'Ohne Access-Token geht es nicht.' }
    $unlock = Read-Secret 'Token zum Öffnen (Standorte: Bearbeiten), leer lassen = derselbe Token'
    $protect = if ($ProtectPin) { Read-Secret 'Protect-API-Schlüssel (UniFi OS → Integrations) für das Livebild' } else { '' }
    "$access`n$unlock`n$protect" | & $serviceExe set-secrets | Out-Null
    $code = $LASTEXITCODE
    Remove-Variable access, unlock, protect
    if ($code -ne 0) { throw "Tokens konnten nicht gespeichert werden (Code $code)" }
}

Write-Host '7/7 Autostart und Start'
New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' `
    -Value "`"$appExe`"" -PropertyType String -Force | Out-Null
# Die App braucht keinen eingehenden Zugriff, das Bild kommt über 127.0.0.1. Die Sperrregel verhindert die
# Firewall-Abfrage, die LibVLC sonst auslöst, und hat Vorrang vor einer versehentlich erteilten Freigabe.
$firewallRule = 'SI EntryDesk: kein eingehender Zugriff'
Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Program $appExe -Action Block -Profile Any | Out-Null
# Eintrag im Startmenü für alle Benutzer, falls die App beendet wurde.
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SI EntryDesk.lnk'))
$shortcut.TargetPath = $appExe
$shortcut.WorkingDirectory = Split-Path $appExe
$shortcut.Description = 'SI EntryDesk: Türsprechstelle'
$shortcut.Save()
Start-Service $serviceName
Start-Sleep -Seconds 3
Write-Host "    Dienst: $((Get-Service $serviceName).Status)"
# Die App im normalen Benutzerkontext starten, nicht in dieser erhöhten Sitzung.
Start-Process explorer.exe -ArgumentList "`"$appExe`""

Write-Host "    Version: $((Get-Item $appExe).VersionInfo.ProductVersion.Split('+')[0])"
Write-Host ''
Write-Host 'Fertig. Protokoll des Dienstes:' $logDir

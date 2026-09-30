#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installiert oder aktualisiert SI EntryDesk: Dienst, Tray-App und Anlagen-Konfiguration.

.DESCRIPTION
    - Dienst "SIEntryDesk" unter dem virtuellen Konto "NT SERVICE\SIEntryDesk" (nicht SYSTEM)
    - Daten unter %ProgramData%\SIEntryDesk, lesbar nur für SYSTEM, Administratoren und den Dienst
    - Anlagen-Konfiguration aus sientrydesk.json (ohne Geheimnisse), siehe sientrydesk.example.json
    - Tokens nur mit -SetTokens (fragt verdeckt) oder separat mit set-tokens.ps1. Ohne -SetTokens fragt das Skript
      nichts und eignet sich für Intune. Vorhandene Tokens bleiben bei einem Update erhalten.
    - Tray-App startet bei jeder Anmeldung, Eintrag im Startmenü, Firewall-Sperre für eingehende Verbindungen

.EXAMPLE
    Erstinstallation von Hand:
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens

.EXAMPLE
    Update oder Intune (System-Kontext), Konfiguration liegt neben dem Skript:
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
    [string] $ConfigFile = (Join-Path $PSScriptRoot 'sientrydesk.json'),
    [switch] $SetTokens,
    [switch] $NoLiveView
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

Write-Host '1/8 Konfiguration prüfen'
if (-not (Test-Path $ConfigFile)) {
    throw "Konfiguration nicht gefunden: $ConfigFile. Vorlage: sientrydesk.example.json"
}
# Windows PowerShell 5.1 liest kein JSON mit Kommentaren. Ganze Kommentarzeilen werden für die Prüfung entfernt,
# der Dienst selbst versteht sie.
$configText = (Get-Content -Raw -Encoding UTF8 $ConfigFile) -replace '(?m)^\s*//.*$', ''
try { $config = $configText | ConvertFrom-Json }
catch { throw "sientrydesk.json ist kein gültiges JSON (Kommentare nur auf eigenen Zeilen): $($_.Exception.Message)" }
foreach ($required in 'Host', 'AccessPin') {
    if (-not $config.$required) { throw "sientrydesk.json: '$required' fehlt." }
}

Write-Host '2/8 Laufende Instanzen beenden'
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { Stop-Service $serviceName -Force }
Get-Process -Name 'SIEntryDesk' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host '3/8 Dateien kopieren'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
foreach ($part in 'Service', 'App') {
    $target = Join-Path $installDir $part
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item -Recurse -Force (Join-Path $PSScriptRoot $part) $target
}
Copy-Item -Force (Join-Path $PSScriptRoot 'set-tokens.ps1') $installDir
Get-ChildItem $installDir -Recurse -File | Unblock-File

Write-Host '4/8 Dienst einrichten'
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $serviceName -BinaryPathName "`"$serviceExe`"" -DisplayName 'SI EntryDesk' `
        -Description 'Klingel-Ereignisse von UniFi Access, Öffnen nur während eines Rufs' -StartupType Automatic | Out-Null
}
Invoke-Native 'sc.exe' @('config', $serviceName, 'obj=', $serviceAccount, 'start=', 'auto')
Invoke-Native 'sc.exe' @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/30000')

Write-Host '5/8 Datenordner und Rechte'
New-Item -ItemType Directory -Force -Path $dataDir, $logDir, $stateDir | Out-Null
# Nur SYSTEM (S-1-5-18), Administratoren (S-1-5-32-544) und das Dienstkonto, keine Vererbung von ProgramData.
Invoke-Native 'icacls.exe' @($dataDir, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "${serviceAccount}:(OI)(CI)RX")
Invoke-Native 'icacls.exe' @($logDir, '/grant:r', "${serviceAccount}:(OI)(CI)M")
Invoke-Native 'icacls.exe' @($stateDir, '/grant:r', "${serviceAccount}:(OI)(CI)M")

Write-Host '6/8 Konfiguration übernehmen'
Copy-Item -Force $ConfigFile (Join-Path $dataDir 'sientrydesk.json')
# Konfiguration bis 0.2.x, sonst würden alte Werte mitgelesen.
Remove-Item -Force (Join-Path $dataDir 'config.json') -ErrorAction SilentlyContinue
if ($NoLiveView) {
    '{ "LiveView": false }' | Set-Content -Encoding UTF8 (Join-Path $dataDir 'sientrydesk.local.json')
    Write-Host '    Livebild ohne Klingeln auf diesem PC abgeschaltet (sientrydesk.local.json).'
}

Write-Host '7/8 Tokens'
if ($SetTokens) {
    & (Join-Path $PSScriptRoot 'set-tokens.ps1') -ServiceExe $serviceExe -NoRestart
} elseif (Test-Path (Join-Path $dataDir 'secrets.dat')) {
    Write-Host '    Vorhandene Tokens bleiben.'
} else {
    Write-Warning 'Noch keine Tokens gespeichert. Mit set-tokens.ps1 oder install.ps1 -SetTokens setzen, sonst klingelt nichts.'
}

Write-Host '8/8 Autostart und Start'
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
# Im System-Kontext (Intune) gibt es keine Anzeige, die App startet dann bei der nächsten Anmeldung.
if (-not ([Security.Principal.WindowsIdentity]::GetCurrent()).IsSystem) {
    # Im normalen Benutzerkontext starten, nicht in dieser erhöhten Sitzung.
    Start-Process explorer.exe -ArgumentList "`"$appExe`""
}

Write-Host "    Version: $((Get-Item $appExe).VersionInfo.ProductVersion.Split('+')[0])"
Write-Host ''
Write-Host 'Fertig. Protokoll des Dienstes:' $logDir

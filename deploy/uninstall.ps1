#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Entfernt SI EntryDesk. Mit -KeepData bleiben Konfiguration, Tokens und Protokolle erhalten.
#>
param([switch] $KeepData)

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
$serviceName = 'SIEntryDesk'

Get-Process -Name 'SIEntryDesk' -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
}
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SI EntryDesk.lnk') -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName 'SI EntryDesk: kein eingehender Zugriff' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-Item -Path 'HKLM:\SOFTWARE\SIEntryDesk' -Recurse -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $env:ProgramFiles 'SIEntryDesk') -ErrorAction SilentlyContinue
# Reste einer Installation durch die 32-Bit-PowerShell (Intune mit 0.5.0) entfernen.
Remove-Item -Recurse -Force (Join-Path ${env:ProgramFiles(x86)} 'SIEntryDesk') -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\WOW6432Node\SIEntryDesk' -Recurse -ErrorAction SilentlyContinue
if (-not $KeepData) {
    Remove-Item -Recurse -Force (Join-Path $env:ProgramData 'SIEntryDesk') -ErrorAction SilentlyContinue
}
Write-Host 'SI EntryDesk entfernt.'

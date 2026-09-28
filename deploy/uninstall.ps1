#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Entfernt SI EntryDesk. Mit -KeepData bleiben Konfiguration, Tokens und Protokolle erhalten.
#>
param([switch] $KeepData)

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
Remove-Item -Recurse -Force (Join-Path $env:ProgramFiles 'SIEntryDesk') -ErrorAction SilentlyContinue
if (-not $KeepData) {
    Remove-Item -Recurse -Force (Join-Path $env:ProgramData 'SIEntryDesk') -ErrorAction SilentlyContinue
}
Write-Host 'SI EntryDesk entfernt.'

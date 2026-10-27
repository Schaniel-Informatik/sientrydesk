#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Entfernt SI EntryDesk. Mit -KeepData bleiben Konfiguration, Tokens und Protokolle erhalten.
#>
param([switch] $KeepData)

# Intune starts install commands as a 32-bit process. The 32-bit PowerShell sees C:\Program Files (x86) and
# HKLM\SOFTWARE\WOW6432Node instead of the right locations, so switch to the 64-bit PowerShell here.
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
# Remove leftovers of an installation by the 32-bit PowerShell (Intune with 0.5.0).
Remove-Item -Recurse -Force (Join-Path ${env:ProgramFiles(x86)} 'SIEntryDesk') -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\WOW6432Node\SIEntryDesk' -Recurse -ErrorAction SilentlyContinue
if (-not $KeepData) {
    Remove-Item -Recurse -Force (Join-Path $env:ProgramData 'SIEntryDesk') -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $env:WINDIR 'Temp\SIEntryDesk-install.log') -ErrorAction SilentlyContinue
    # The users' settings (door sound automatically, reminder, ring per door, talk with), as far as their profiles
    # are loaded.
    Get-ChildItem Registry::HKEY_USERS | ForEach-Object {
        Remove-Item "Registry::$($_.Name)\Software\SIEntryDesk" -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host 'SI EntryDesk entfernt.'

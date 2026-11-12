#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes SI EntryDesk. With -KeepData configuration, tokens and logs are kept.
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

if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
}
# Stop-Process does not wait, files stay locked until the processes have exited.
$running = @(Get-Process -Name 'SIEntryDesk', 'SIEntryDesk.Service' -ErrorAction SilentlyContinue)
$running | Stop-Process -Force -ErrorAction SilentlyContinue
$running | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SI EntryDesk.lnk') -ErrorAction SilentlyContinue
# English names from 0.10.0, German ones before.
Get-NetFirewallRule -DisplayName 'SI EntryDesk: no inbound access', 'SI EntryDesk: coordination between PCs',
    'SI EntryDesk: kein eingehender Zugriff', 'SI EntryDesk: Abstimmung zwischen PCs' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
# Only the program's own value. TokenLabel belongs to the credentials and goes with them below (not with -KeepData).
$regPath = 'HKLM:\SOFTWARE\SIEntryDesk'
Remove-ItemProperty -Path $regPath -Name 'Version' -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force (Join-Path $env:ProgramFiles 'SIEntryDesk') -ErrorAction SilentlyContinue
# Remove leftovers of an installation by the 32-bit PowerShell (Intune with 0.5.0).
Remove-Item -Recurse -Force (Join-Path ${env:ProgramFiles(x86)} 'SIEntryDesk') -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\WOW6432Node\SIEntryDesk' -Recurse -ErrorAction SilentlyContinue
if (-not $KeepData) {
    Remove-Item -Recurse -Force (Join-Path $env:ProgramData 'SIEntryDesk') -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $env:WINDIR 'Temp\SIEntryDesk-install.log') -ErrorAction SilentlyContinue
    # The credentials (secrets.dat) are gone, so their label goes too.
    Remove-ItemProperty -Path $regPath -Name 'TokenLabel' -ErrorAction SilentlyContinue
    # The users' settings (door sound automatically, reminder, ring per door, talk with), as far as their profiles
    # are loaded.
    Get-ChildItem Registry::HKEY_USERS | ForEach-Object {
        Remove-Item "Registry::$($_.Name)\Software\SIEntryDesk" -Recurse -Force -ErrorAction SilentlyContinue
    }
}
# Remove the key only when nothing is left in it.
$key = Get-Item -Path $regPath -ErrorAction SilentlyContinue
if ($key -and $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0) { Remove-Item -Path $regPath -ErrorAction SilentlyContinue }
Write-Host 'SI EntryDesk removed.'

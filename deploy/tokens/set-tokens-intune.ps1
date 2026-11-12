<#
.SYNOPSIS
    Intune: sets the credentials of SI EntryDesk. Separate Win32 app "SI EntryDesk Tokens", depending on "SI EntryDesk".

.DESCRIPTION
    Reads tokens.txt from the same folder (UTF-8, template tokens.example.txt). Empty lines and lines with # do not
    count, otherwise each line is name=value:
      access=   Access token (Device = View, Locations = Edit), required
      protect=  Protect API key, empty or left out = no live view
    Stores them with DPAPI via the service, restarts it and records the label under
    HKLM\SOFTWARE\SIEntryDesk\TokenLabel for the detection rule in Intune.

    Create tokens.txt only for building the package and delete it afterwards. Intune stores the content of the package
    encrypted, on the device it exists only during the installation in the cache of the Intune extension.

.EXAMPLE
    Install command:   powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Label 2026-09
    Uninstall command: powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Remove
#>
param(
    [string] $Label,
    [switch] $Remove,
    # Only check tokens.txt, store nothing (for prepare-intune.ps1).
    [switch] $Check
)

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

# Error messages name only line number and name, never the content, so that no token ends up in the Intune log.
function Read-TokenFile([string] $Path) {
    if (-not (Test-Path $Path)) { throw 'tokens.txt is missing in the package.' }
    $values = @{}
    $n = 0
    foreach ($raw in @(Get-Content -Encoding UTF8 $Path)) {
        $n++
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $eq = $line.IndexOf('=')
        if ($eq -lt 1) { throw "tokens.txt, line ${n}: expected name=value, e.g. access=... (template tokens.example.txt)." }
        $key = $line.Substring(0, $eq).Trim().ToLowerInvariant()
        $value = $line.Substring($eq + 1).Trim()
        if ($key -eq 'unlock') { throw "tokens.txt, line ${n}: unlock= is no longer supported, one Access token with Device = View and Locations = Edit is enough." }
        if (@('access', 'protect') -notcontains $key) { throw "tokens.txt, line ${n}: unknown name, allowed are access and protect." }
        if ($values.ContainsKey($key)) { throw "tokens.txt, line ${n}: $key appears twice." }
        if ($value -match '[\s<>"'']') { throw "tokens.txt, line ${n}: value of $key contains spaces, quotes or <>. Insert only the value itself." }
        $values[$key] = $value
    }
    if (-not $values['access']) { throw 'tokens.txt: access= is missing or empty.' }
    return $values
}

if ($Check) {
    $checked = Read-TokenFile (Join-Path $PSScriptRoot 'tokens.txt')
    Write-Host "    tokens.txt is fine: $(@($checked.Keys | Where-Object { $checked[$_] } | Sort-Object) -join ', ')"
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

if (-not $Label -or $Label -notmatch '^[A-Za-z0-9._-]{1,32}$') { throw 'Label missing or invalid, e.g. -Label 2026-09' }
if (-not (Test-Path $serviceExe)) { throw 'SI EntryDesk is not installed (check the dependency in Intune).' }

$tokens = Read-TokenFile (Join-Path $PSScriptRoot 'tokens.txt')
# Line 2 stays empty (formerly a separate token for unlocking).
"$($tokens['access'])`n`n$($tokens['protect'])" | & $serviceExe set-secrets | Out-Null
$code = $LASTEXITCODE
$tokens.Clear()
Remove-Variable tokens
if ($code -ne 0) { throw "Credentials could not be stored (code $code)" }

# Create the key only if it is missing: New-Item -Force would recreate it empty and delete the Version of the program.
if (-not (Test-Path $regPath)) { New-Item -Path $regPath | Out-Null }
Set-ItemProperty -Path $regPath -Name 'TokenLabel' -Value $Label
if (Get-Service SIEntryDesk -ErrorAction SilentlyContinue) { Restart-Service SIEntryDesk }
Write-Host "Credentials set, label $Label"

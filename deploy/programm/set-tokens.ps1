#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Sets or renews the tokens of SI EntryDesk without reinstalling the software.

.DESCRIPTION
    Asks for the two values without echo and stores them with DPAPI (machine key) in
    %ProgramData%\SIEntryDesk\secrets.dat. Then the service restarts.
    - Access token: Device = View, Locations = Edit, everything else None
    - Protect API key for the live view (empty = no live view)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\set-tokens.ps1"
#>
param(
    [string] $ServiceExe = (Join-Path $env:ProgramFiles 'SIEntryDesk\Service\SIEntryDesk.Service.exe'),
    [switch] $NoRestart
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ServiceExe)) { throw "SI EntryDesk is not installed ($ServiceExe is missing)." }

function Read-Secret([string] $Prompt) {
    $secure = Read-Host -AsSecureString $Prompt
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$access = Read-Secret 'Access token (Device = View, Locations = Edit)'
if (-not $access) { throw 'Nothing works without an Access token.' }
$protect = Read-Secret 'Protect API key for the live view, empty = no live view'
# Line 2 stays empty (formerly a separate token for unlocking).
"$access`n`n$protect" | & $ServiceExe set-secrets | Out-Null
$code = $LASTEXITCODE
Remove-Variable access, protect
if ($code -ne 0) { throw "Tokens could not be stored (code $code)" }
Write-Host '    Tokens stored.'

if (-not $NoRestart -and (Get-Service SIEntryDesk -ErrorAction SilentlyContinue)) {
    Restart-Service SIEntryDesk
    Write-Host "    Service restarted: $((Get-Service SIEntryDesk).Status)"
}

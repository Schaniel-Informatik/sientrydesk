#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Setzt oder erneuert die Tokens von SI EntryDesk, ohne die Software neu zu installieren.

.DESCRIPTION
    Fragt die drei Werte verdeckt ab und speichert sie mit DPAPI (Maschinenschlüssel) in
    %ProgramData%\SIEntryDesk\secrets.dat. Danach startet der Dienst neu.
    - Access-Token: Gerät = Anzeigen, Standorte = Bearbeiten, alles andere Keinen
    - optional ein eigener Access-Token nur zum Öffnen, normalerweise leer lassen
    - Protect-API-Schlüssel für das Livebild (leer = kein Livebild)

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File "C:\Program Files\SIEntryDesk\set-tokens.ps1"
#>
param(
    [string] $ServiceExe = (Join-Path $env:ProgramFiles 'SIEntryDesk\Service\SIEntryDesk.Service.exe'),
    [switch] $NoRestart
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ServiceExe)) { throw "SI EntryDesk ist nicht installiert ($ServiceExe fehlt)." }

function Read-Secret([string] $Prompt) {
    $secure = Read-Host -AsSecureString $Prompt
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$access = Read-Secret 'Access-Token (Gerät = Anzeigen, Standorte = Bearbeiten)'
if (-not $access) { throw 'Ohne Access-Token geht es nicht.' }
$unlock = Read-Secret 'Eigener Token nur zum Öffnen, normalerweise leer lassen (Enter)'
$protect = Read-Secret 'Protect-API-Schlüssel für das Livebild, leer = kein Livebild'
"$access`n$unlock`n$protect" | & $ServiceExe set-secrets | Out-Null
$code = $LASTEXITCODE
Remove-Variable access, unlock, protect
if ($code -ne 0) { throw "Tokens konnten nicht gespeichert werden (Code $code)" }
Write-Host '    Tokens gespeichert.'

if (-not $NoRestart -and (Get-Service SIEntryDesk -ErrorAction SilentlyContinue)) {
    Restart-Service SIEntryDesk
    Write-Host "    Dienst neu gestartet: $((Get-Service SIEntryDesk).Status)"
}

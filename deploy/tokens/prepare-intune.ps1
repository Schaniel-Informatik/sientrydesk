<#
.SYNOPSIS
    Prepares the Intune package with the credentials ("SI EntryDesk Tokens"): checks tokens.txt, creates the .intunewin
    file and deletes tokens.txt afterwards.

.DESCRIPTION
    Run in the unpacked credentials package, after tokens.txt has been created from the template tokens.example.txt.
      1. Checks tokens.txt (as during the installation) and that the folder contains only the needed files.
      2. Creates with IntuneWinAppUtil.exe the folder Intune-SIEntryDesk-Tokens-<label> next to this folder, with
         SIEntryDesk-Tokens-<label>.intunewin.
      3. Deletes tokens.txt. From then on the credentials exist only encrypted in the Intune package.
      4. Shows the settings for Intune.

    IntuneWinAppUtil.exe: give it with -IntuneWinAppUtil, otherwise the script searches the path, next to this folder
    and under Downloads.

.EXAMPLE
    Double-click prepare-intune.cmd (asks for the label), or:
    powershell -ExecutionPolicy Bypass -File .\prepare-intune.ps1 -Label 2026-09
#>
param(
    [string] $Label,
    [string] $IntuneWinAppUtil
)

$ErrorActionPreference = 'Stop'
$folder = $PSScriptRoot
$parent = Split-Path $folder -Parent
$tokensFile = Join-Path $folder 'tokens.txt'

function Find-IntuneWinAppUtil([string] $Given) {
    if ($Given) {
        if (Test-Path $Given) { return (Resolve-Path $Given).Path }
        throw "IntuneWinAppUtil not found: $Given"
    }
    $command = Get-Command 'IntuneWinAppUtil.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($dir in $parent, $folder, (Join-Path $env:USERPROFILE 'Downloads')) {
        $candidate = Get-ChildItem -Path $dir -Filter 'IntuneWinAppUtil.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    $picked = Select-File 'Select IntuneWinAppUtil.exe' 'IntuneWinAppUtil.exe|IntuneWinAppUtil.exe'
    if ($picked) { return $picked }
    throw ('IntuneWinAppUtil.exe not found. Download it from https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool ' +
           'and put it next to this folder, or give it with -IntuneWinAppUtil <path>.')
}

# File dialog when the script runs by double-click (prepare-intune.cmd). Without a window (e.g. via SSH) null.
function Select-File([string] $Title, [string] $Filter) {
    if (-not [Environment]::UserInteractive) { return $null }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = $Title
        $dialog.Filter = $Filter
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.FileName }
    } catch {
    }
    return $null
}


if (-not $Label) {
    $suggested = Get-Date -Format 'yyyy-MM'
    $answer = Read-Host "Label of the credentials (year-month, Enter = $suggested)"
    $Label = if ($answer) { $answer.Trim() } else { $suggested }
}
if ($Label -notmatch '^[A-Za-z0-9._-]{1,32}$') { throw 'Invalid label, e.g. 2026-09' }

Write-Host '1/4 Checking tokens.txt'
$tool = Find-IntuneWinAppUtil $IntuneWinAppUtil
if (-not (Test-Path $tokensFile)) { throw 'tokens.txt is missing. Copy tokens.example.txt to tokens.txt and fill it in.' }
$expected = 'set-tokens-intune.ps1', 'tokens.txt', 'tokens.example.txt', 'prepare-intune.ps1', 'prepare-intune.cmd'
$extra = Get-ChildItem $folder -Force | Where-Object { $expected -notcontains $_.Name }
if ($extra) { throw "The folder contains more than needed: $($extra.Name -join ', '). Only the unpacked credentials package and tokens.txt." }
& (Join-Path $folder 'set-tokens-intune.ps1') -Check

$out = Join-Path $parent "Intune-SIEntryDesk-Tokens-$Label"
$packageFile = "SIEntryDesk-Tokens-$Label.intunewin"
try {
    Write-Host '2/4 Creating the Intune package'
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Path $out | Out-Null
    & $tool -c $folder -s 'set-tokens-intune.ps1' -o $out -q | Out-Null
    $intunewin = Join-Path $out 'set-tokens-intune.intunewin'
    if (-not (Test-Path $intunewin)) { throw "IntuneWinAppUtil did not create an .intunewin (code $LASTEXITCODE)." }
    Rename-Item $intunewin $packageFile
} catch {
    Write-Warning "tokens.txt is still in $folder. Delete it if you stop here."
    throw
}

Write-Host '3/4 Deleting tokens.txt'
Remove-Item -Force $tokensFile

Write-Host '4/4 Settings for Intune (Apps > Windows > Create > Windows app (Win32))'
@(
    "    Package file           $(Join-Path $out $packageFile)"
    "    Name                   SI EntryDesk Tokens $Label"
    "    Install command        powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Label $Label"
    "    Uninstall command      powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\set-tokens-intune.ps1 -Remove"
    "    Install behavior       System"
    "    Requirements           64-bit, Windows 11"
    "    Detection rule         Manual, registry: HKEY_LOCAL_MACHINE\SOFTWARE\SIEntryDesk, value name TokenLabel,"
    "                           string comparison, equals $Label, 32-bit app on 64-bit clients: No"
    "    Dependencies           SI EntryDesk, install automatically (in this app, not in the program app)"
    "    Supersedence           none. At the yearly renewal the new app supersedes the old one, without uninstalling"
    "    Assignment             Required, the same device group"
) | Write-Host

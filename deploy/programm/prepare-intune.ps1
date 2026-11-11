<#
.SYNOPSIS
    Prepares the Intune package "SI EntryDesk": checks the package folder, creates detect.ps1 and the .intunewin file.

.DESCRIPTION
    Run in the unpacked program package, after the sientrydesk.json of the installation is next to install.ps1.
      1. Checks: program files complete, sientrydesk.json readable with Host and AccessPin, no tokens.txt.
      2. Creates detect.ps1 in the package folder, the detection script for Intune. install.ps1 also puts it into
         C:\Program Files\SIEntryDesk, where it shows on the device the same as Intune.
      3. Creates with IntuneWinAppUtil.exe the folder Intune-SIEntryDesk-<version> next to the package folder, with
         SIEntryDesk-<version>.intunewin and detect.ps1 for uploading.
      4. Shows the settings for Intune.

    IntuneWinAppUtil.exe (Microsoft Win32 Content Prep Tool): give it with -IntuneWinAppUtil, otherwise the script
    searches the path, next to the package folder and under Downloads.

.EXAMPLE
    Double-click prepare-intune.cmd, or:
    powershell -ExecutionPolicy Bypass -File .\prepare-intune.ps1
#>
param(
    [string] $IntuneWinAppUtil
)

$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
$parent = Split-Path $package -Parent

function Find-IntuneWinAppUtil([string] $Given) {
    if ($Given) {
        if (Test-Path $Given) { return (Resolve-Path $Given).Path }
        throw "IntuneWinAppUtil not found: $Given"
    }
    $command = Get-Command 'IntuneWinAppUtil.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($dir in $parent, $package, (Join-Path $env:USERPROFILE 'Downloads')) {
        $candidate = Get-ChildItem -Path $dir -Filter 'IntuneWinAppUtil.exe' -Recurse -Depth 2 -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    $picked = Select-File 'Select IntuneWinAppUtil.exe' 'IntuneWinAppUtil.exe|IntuneWinAppUtil.exe'
    if ($picked) { return $picked }
    throw ('IntuneWinAppUtil.exe not found. Download it from https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool ' +
           'and put it next to the package folder, or give it with -IntuneWinAppUtil <path>.')
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


Write-Host '1/4 Checking the package'
$tool = Find-IntuneWinAppUtil $IntuneWinAppUtil
$missing = 'install.ps1', 'uninstall.ps1', 'set-tokens.ps1', 'App\SIEntryDesk.exe', 'Service\SIEntryDesk.Service.exe' |
    Where-Object { -not (Test-Path (Join-Path $package $_)) }
if ($missing) { throw "Missing in the package folder: $($missing -join ', ')" }
if (Get-ChildItem $package -Recurse -Filter 'tokens.txt') {
    throw 'tokens.txt does not belong into the program package. Delete it here, it belongs only into the credentials package.'
}
$expected = 'App', 'Service', 'install.ps1', 'uninstall.ps1', 'set-tokens.ps1', 'prepare-intune.ps1', 'prepare-intune.cmd', 'detect.ps1',
            'sientrydesk.json', 'sientrydesk.example.json', 'Doku'
$extra = Get-ChildItem $package -Force | Where-Object { $expected -notcontains $_.Name }
if ($extra) { throw "The package folder contains more than needed: $($extra.Name -join ', '). Only the unpacked package and sientrydesk.json." }

$configFile = Join-Path $package 'sientrydesk.json'
if (-not (Test-Path $configFile)) {
    $picked = Select-File 'Select the sientrydesk.json of the installation' 'sientrydesk.json|sientrydesk.json|JSON|*.json'
    if (-not $picked) { throw 'sientrydesk.json is missing next to install.ps1.' }
    Copy-Item $picked $configFile
    Write-Host "    sientrydesk.json taken over from $picked"
}
# Windows PowerShell 5.1 does not read JSON with comments, whole comment lines are removed for the check.
$configText = (Get-Content -Raw -Encoding UTF8 $configFile) -replace '(?m)^\s*//.*$', ''
try { $config = $configText | ConvertFrom-Json }
catch { throw "sientrydesk.json is not valid JSON (comments only on lines of their own): $($_.Exception.Message)" }
foreach ($required in 'Host', 'AccessPin') {
    if (-not $config.$required) { throw "sientrydesk.json: '$required' is missing." }
}
$language = if ($config.Language) { "$($config.Language)".Trim().ToLowerInvariant() } else { 'de' }
if ($language -notin 'de', 'en', 'pl') { throw "sientrydesk.json: Language '$($config.Language)' is unknown, possible are de, en and pl." }
$fileVersion = (Get-Item (Join-Path $package 'App\SIEntryDesk.exe')).VersionInfo.FileVersion
if (-not $fileVersion) { throw 'Version of SIEntryDesk.exe not readable.' }
$version = [version] $fileVersion
$shortVersion = $version.ToString(3)
# With .NET instead of Get-FileHash: the cmdlet is missing in Windows PowerShell when started from PowerShell 7.
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $hash = ($sha.ComputeHash([System.IO.File]::ReadAllBytes($configFile)) | ForEach-Object { $_.ToString('X2') }) -join '' }
finally { $sha.Dispose() }
Write-Host "    Version $shortVersion, console $($config.Host), language $language"

Write-Host '2/4 Detection script detect.ps1'
$created = Get-Date -Format 'yyyy-MM-dd HH:mm'
# ASCII only, so that Intune and Windows PowerShell 5.1 run the script without encoding issues.
$detection = @"
# SI EntryDesk: detection script for the Intune app "SI EntryDesk".
# Created on $created with prepare-intune.ps1 for version $version and the sientrydesk.json of this package.
# Installed = service present, app at least version $version, sientrydesk.json unchanged from the package.
# Intune: output and exit code 0 = installed. Without output = not installed, Intune installs the package.
# Run on the device as administrator: shows the same as Intune, without output something is missing.

`$version    = [version] '$version'
`$configHash = '$hash'
`$exe        = Join-Path `$env:ProgramW6432 'SIEntryDesk\App\SIEntryDesk.exe'
`$config     = Join-Path `$env:ProgramData 'SIEntryDesk\sientrydesk.json'

try {
    if (-not (Get-Service -Name 'SIEntryDesk' -ErrorAction SilentlyContinue)) { exit 0 }
    if (-not (Test-Path -LiteralPath `$exe) -or -not (Test-Path -LiteralPath `$config)) { exit 0 }
    if ([version] (Get-Item -LiteralPath `$exe).VersionInfo.FileVersion -lt `$version) { exit 0 }
    `$sha = [System.Security.Cryptography.SHA256]::Create()
    `$actual = (`$sha.ComputeHash([System.IO.File]::ReadAllBytes(`$config)) | ForEach-Object { `$_.ToString('X2') }) -join ''
    if (`$actual -ne `$configHash) { exit 0 }
} catch {
    exit 0
}
Write-Output "SI EntryDesk `$version installed with this configuration"
exit 0
"@
$detectFile = Join-Path $package 'detect.ps1'
[System.IO.File]::WriteAllText($detectFile, $detection.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.ASCIIEncoding))

Write-Host '3/4 Creating the Intune package'
$out = Join-Path $parent "Intune-SIEntryDesk-$shortVersion"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out | Out-Null
& $tool -c $package -s 'install.ps1' -o $out -q | Out-Null
$intunewin = Join-Path $out 'install.intunewin'
if (-not (Test-Path $intunewin)) { throw "IntuneWinAppUtil did not create an .intunewin (code $LASTEXITCODE)." }
$packageFile = "SIEntryDesk-$shortVersion.intunewin"
Rename-Item $intunewin $packageFile
Copy-Item $detectFile $out

Write-Host '4/4 Settings for Intune (Apps > Windows > Create > Windows app (Win32))'
@(
    "    Package file           $(Join-Path $out $packageFile)"
    "    Name                   SI EntryDesk"
    "    App version            $shortVersion"
    "    Install command        powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\install.ps1"
    "    Uninstall command      powershell.exe -ExecutionPolicy Bypass -NoProfile -File .\uninstall.ps1"
    "    Install behavior       System"
    "    Requirements           64-bit, Windows 11"
    "    Detection rule         Custom script: $(Join-Path $out 'detect.ps1')"
    "                           run as 32-bit process: No, enforce signature check: No"
    "    Dependencies           none"
    "    Supersedence           none. Update: replace package file and detection script in the existing app"
    "    Assignment             Required, device group"
) | Write-Host

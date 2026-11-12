#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or updates SI EntryDesk: service, tray app and installation configuration.

.DESCRIPTION
    - Service "SIEntryDesk" under the virtual account "NT SERVICE\SIEntryDesk" (not SYSTEM)
    - Data under %ProgramData%\SIEntryDesk, readable only by SYSTEM, administrators and the service
    - Installation configuration from sientrydesk.json (without secrets), see sientrydesk.example.json
    - Tokens only with -SetTokens (asks without echo) or separately with set-tokens.ps1. Without -SetTokens the script
      asks nothing and suits Intune. Existing tokens are kept on an update.
    - Tray app starts at every sign-in, Start menu entry, firewall block for inbound connections of the app
    - Firewall exception UDP 47801 only from the local subnet, only for the service: coordination between the PCs,
      who answered a call
    - -NoLiveView or -NoTalk switch off the live view without ringing or talkback on this PC only

.EXAMPLE
    First installation by hand:
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens

.EXAMPLE
    Update or Intune (system context), configuration next to the script:
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
    [string] $ConfigFile = (Join-Path $PSScriptRoot 'sientrydesk.json'),
    [switch] $SetTokens,
    [switch] $NoLiveView,
    [switch] $NoTalk
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
# Intune does not show the output. As SYSTEM therefore a log under C:\Windows\Temp (contains no credentials).
if (([Security.Principal.WindowsIdentity]::GetCurrent()).IsSystem) {
    Start-Transcript -Path (Join-Path $env:WINDIR 'Temp\SIEntryDesk-install.log') -Force | Out-Null
}
$serviceName    = 'SIEntryDesk'
$serviceAccount = "NT SERVICE\$serviceName"
$installDir     = Join-Path $env:ProgramFiles 'SIEntryDesk'
$dataDir        = Join-Path $env:ProgramData 'SIEntryDesk'
$logDir         = Join-Path $dataDir 'logs'
$stateDir       = Join-Path $dataDir 'state'
$serviceExe     = Join-Path $installDir 'Service\SIEntryDesk.Service.exe'
$appExe         = Join-Path $installDir 'App\SIEntryDesk.exe'

# If the script fails after stopping the service, start it again: the PC keeps ringing with what is installed. With
# break the error still ends the script with exit code 1, so Intune reports the failure.
trap {
    if ($serviceName -and (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
        Start-Service $serviceName -ErrorAction SilentlyContinue
    }
    break
}

# Stop-Process does not wait. Until a process has really exited, Windows keeps its files locked (0.9.0 on some PCs:
# "Zugriff verweigert" on a LibVLC plugin of the app). Therefore stop everything that runs from the install folder
# and wait until it has exited: usually well under a second, at most 60 s.
function Stop-Running {
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -in 'SIEntryDesk', 'SIEntryDesk.Service' -or
        ($_.Path -and $_.Path.StartsWith("$installDir\", [StringComparison]::OrdinalIgnoreCase))
    })
    if ($running.Count -eq 0) { return }
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    $running | Wait-Process -Timeout 60 -ErrorAction SilentlyContinue
}

# Deletes a folder, retrying for up to 30 s if a file is still locked (process hanging, virus scanner). Raises the
# chance that the update still goes through.
function Remove-Folder([string] $Path) {
    for ($attempt = 1; Test-Path $Path; $attempt++) {
        try { Remove-Item -Recurse -Force $Path; return }
        catch {
            if ($attempt -ge 15) { throw "$Path cannot be deleted, a file is still in use: $($_.Exception.Message)" }
            # Once more stop what runs from the folder, but only once: a hanging process would cost 60 s each time.
            if ($attempt -eq 1) { Write-Host "    $Path still locked, waiting …"; Stop-Running }
            Start-Sleep -Seconds 2
        }
    }
}

function Invoke-Native([string] $Exe, [string[]] $Arguments) {
    & $Exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed (code $LASTEXITCODE)" }
}

Write-Host '1/8 Checking configuration'
if (-not (Test-Path $ConfigFile)) {
    throw "Configuration not found: $ConfigFile. Template: sientrydesk.example.json"
}
# Windows PowerShell 5.1 does not read JSON with comments. Whole comment lines are removed for the check,
# the service itself understands them.
$configText = (Get-Content -Raw -Encoding UTF8 $ConfigFile) -replace '(?m)^\s*//.*$', ''
try { $config = $configText | ConvertFrom-Json }
catch { throw "sientrydesk.json is not valid JSON (comments only on lines of their own): $($_.Exception.Message)" }
foreach ($required in 'Host', 'AccessPin') {
    if (-not $config.$required) { throw "sientrydesk.json: '$required' is missing." }
}
# Language for the users (from 0.10.0). A typo would only show as German, better to stop here.
$language = if ($config.Language) { "$($config.Language)".Trim().ToLowerInvariant() } else { 'de' }
if ($language -notin 'de', 'en', 'pl') { throw "sientrydesk.json: Language '$($config.Language)' is unknown, possible are de, en and pl." }

Write-Host '2/8 Stopping running instances'
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { Stop-Service $serviceName -Force }
Stop-Running

Write-Host '3/8 Copying files'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
foreach ($part in 'Service', 'App') {
    $target = Join-Path $installDir $part
    Remove-Folder $target
    Copy-Item -Recurse -Force (Join-Path $PSScriptRoot $part) $target
}
# For admins at the device: renew and remove credentials, check like Intune (detect.ps1 from prepare-intune.ps1).
foreach ($script in 'set-tokens.ps1', 'uninstall.ps1', 'detect.ps1') {
    $source = Join-Path $PSScriptRoot $script
    if (Test-Path $source) { Copy-Item -Force $source $installDir }
}
Get-ChildItem $installDir -Recurse -File | Unblock-File
# Remove leftovers of an installation by the 32-bit PowerShell (Intune with 0.5.0).
$x86Dir = Join-Path ${env:ProgramFiles(x86)} 'SIEntryDesk'
Remove-Item -Recurse -Force $x86Dir -ErrorAction SilentlyContinue
if (Test-Path $x86Dir) { Write-Warning "$x86Dir could not be removed completely, please delete the rest by hand." }
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\WOW6432Node\SIEntryDesk' -Recurse -ErrorAction SilentlyContinue

Write-Host '4/8 Setting up the service'
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $serviceName -BinaryPathName "`"$serviceExe`"" -DisplayName 'SI EntryDesk' `
        -Description 'Doorbell events from UniFi Access, opening only during a call' -StartupType Automatic | Out-Null
}
# Always set the path: an existing service may still point elsewhere (e.g. Program Files (x86)).
$change = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" |
    Invoke-CimMethod -MethodName Change -Arguments @{ PathName = "`"$serviceExe`"" }
if ($change.ReturnValue -ne 0) { throw "The program path of the service could not be set (code $($change.ReturnValue))" }
Invoke-Native 'sc.exe' @('config', $serviceName, 'obj=', $serviceAccount, 'start=', 'auto')
Invoke-Native 'sc.exe' @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/30000')

Write-Host '5/8 Data folder and permissions'
New-Item -ItemType Directory -Force -Path $dataDir, $logDir, $stateDir | Out-Null
# Only SYSTEM (S-1-5-18), administrators (S-1-5-32-544) and the service account, no inheritance from ProgramData.
Invoke-Native 'icacls.exe' @($dataDir, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "${serviceAccount}:(OI)(CI)RX")
Invoke-Native 'icacls.exe' @($logDir, '/grant:r', "${serviceAccount}:(OI)(CI)M")
Invoke-Native 'icacls.exe' @($stateDir, '/grant:r', "${serviceAccount}:(OI)(CI)M")

Write-Host '6/8 Taking over the configuration'
Copy-Item -Force $ConfigFile (Join-Path $dataDir 'sientrydesk.json')
# Configuration up to 0.2.x, otherwise old values would be read as well.
Remove-Item -Force (Join-Path $dataDir 'config.json') -ErrorAction SilentlyContinue
# Deviations for this PC only. Without switches an existing sientrydesk.local.json stays unchanged.
$local = [ordered]@{}
if ($NoLiveView) { $local['LiveView'] = $false }
if ($NoTalk) { $local['Talkback'] = $false }
if ($local.Count -gt 0) {
    $local | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $dataDir 'sientrydesk.local.json')
    Write-Host "    Switched off on this PC: $(@($local.Keys) -join ', ') (sientrydesk.local.json)."
}

Write-Host '7/8 Tokens'
if ($SetTokens) {
    & (Join-Path $PSScriptRoot 'set-tokens.ps1') -ServiceExe $serviceExe -NoRestart
} elseif (Test-Path (Join-Path $dataDir 'secrets.dat')) {
    Write-Host '    Existing tokens are kept.'
} else {
    Write-Warning 'No tokens stored yet. Set them with set-tokens.ps1 or install.ps1 -SetTokens, otherwise nothing rings.'
}

Write-Host '8/8 Autostart and start'
New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' `
    -Value "`"$appExe`"" -PropertyType String -Force | Out-Null
# The app needs no inbound access, the video comes via 127.0.0.1. The block rule prevents the firewall prompt
# LibVLC would otherwise trigger, and takes precedence over an accidentally granted exception.
# Rule names in English from 0.10.0, the German ones of earlier versions are removed.
$firewallRule = 'SI EntryDesk: no inbound access'
Get-NetFirewallRule -DisplayName $firewallRule, 'SI EntryDesk: kein eingehender Zugriff' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Program $appExe -Action Block -Profile Any | Out-Null
# Coordination between PCs (0.9.0): the services tell each other by signed UDP broadcast who answered a call. Only from
# the local subnet and only for the service. Any profile, because Entra-joined PCs often see the office as public.
$peerRule = 'SI EntryDesk: coordination between PCs'
Get-NetFirewallRule -DisplayName $peerRule, 'SI EntryDesk: Abstimmung zwischen PCs' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $peerRule -Direction Inbound -Program $serviceExe -Protocol UDP -LocalPort 47801 `
    -RemoteAddress LocalSubnet -Action Allow -Profile Any | Out-Null
# Start menu entry for all users, in case the app was closed.
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SI EntryDesk.lnk'))
$shortcut.TargetPath = $appExe
$shortcut.WorkingDirectory = Split-Path $appExe
$shortcut.Description = @{ de = 'SI EntryDesk: Türsprechstelle'; en = 'SI EntryDesk: door intercom'; pl = 'SI EntryDesk: domofon' }[$language]
$shortcut.Save()
Start-Service $serviceName
Start-Sleep -Seconds 3
Write-Host "    Service: $((Get-Service $serviceName).Status)"
$version = (Get-Item $appExe).VersionInfo.ProductVersion.Split('+')[0]
# Create the key only if it is missing: New-Item -Force would recreate it empty and delete TokenLabel, the detection
# value of the Intune app with the credentials (until 0.10.0).
$regPath = 'HKLM:\SOFTWARE\SIEntryDesk'
if (-not (Test-Path $regPath)) { New-Item -Path $regPath | Out-Null }
Set-ItemProperty -Path $regPath -Name 'Version' -Value $version

if (([Security.Principal.WindowsIdentity]::GetCurrent()).IsSystem) {
    # Intune installs as SYSTEM. The tray app was stopped above and must run again in the logged-on sessions,
    # otherwise nothing rings until the next logon. As SYSTEM this works via the session management, started with
    # the normal (non-elevated) token of each user.
    try {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class SIEntryDeskSessionLauncher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string application, string commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const int WTSActive = 0;
    private const int WTSDisconnected = 4;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    // Starts a program in every logged-on user session, with the user's normal token.
    // Works only as SYSTEM (Intune), returns the number of started processes.
    public static int StartInUserSessions(string exe, string directory)
    {
        IntPtr sessions;
        int count;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out sessions, out count))
            return 0;
        int started = 0;
        try
        {
            int size = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
            for (int i = 0; i < count; i++)
            {
                WTS_SESSION_INFO info = (WTS_SESSION_INFO)Marshal.PtrToStructure(
                    new IntPtr(sessions.ToInt64() + i * size), typeof(WTS_SESSION_INFO));
                if (info.State != WTSActive && info.State != WTSDisconnected)
                    continue;
                IntPtr userToken;
                if (!WTSQueryUserToken(info.SessionId, out userToken))
                    continue;
                IntPtr primary = IntPtr.Zero;
                IntPtr environment = IntPtr.Zero;
                try
                {
                    if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                        continue;
                    CreateEnvironmentBlock(out environment, primary, false);
                    STARTUPINFO startup = new STARTUPINFO();
                    startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                    startup.lpDesktop = "winsta0\\default";
                    PROCESS_INFORMATION process;
                    if (CreateProcessAsUser(primary, exe, "\"" + exe + "\"", IntPtr.Zero, IntPtr.Zero, false,
                            CreateUnicodeEnvironment, environment, directory, ref startup, out process))
                    {
                        CloseHandle(process.hThread);
                        CloseHandle(process.hProcess);
                        started++;
                    }
                }
                finally
                {
                    if (environment != IntPtr.Zero)
                        DestroyEnvironmentBlock(environment);
                    if (primary != IntPtr.Zero)
                        CloseHandle(primary);
                    CloseHandle(userToken);
                }
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }
        return started;
    }
}
'@
        $started = [SIEntryDeskSessionLauncher]::StartInUserSessions($appExe, (Split-Path $appExe))
        Write-Host "    App started in $started signed-in session(s)."
    } catch {
        Write-Warning "The app could not be started in the sessions, it starts at the next sign-in: $($_.Exception.Message)"
    }
} else {
    # Installed by hand: start in the normal user context, not in this elevated session.
    Start-Process explorer.exe -ArgumentList "`"$appExe`""
}

Write-Host "    Version: $version"
Write-Host ''
Write-Host 'Done. Log of the service:' $logDir

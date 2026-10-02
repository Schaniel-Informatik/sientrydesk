#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installiert oder aktualisiert SI EntryDesk: Dienst, Tray-App und Anlagen-Konfiguration.

.DESCRIPTION
    - Dienst "SIEntryDesk" unter dem virtuellen Konto "NT SERVICE\SIEntryDesk" (nicht SYSTEM)
    - Daten unter %ProgramData%\SIEntryDesk, lesbar nur für SYSTEM, Administratoren und den Dienst
    - Anlagen-Konfiguration aus sientrydesk.json (ohne Geheimnisse), siehe sientrydesk.example.json
    - Tokens nur mit -SetTokens (fragt verdeckt) oder separat mit set-tokens.ps1. Ohne -SetTokens fragt das Skript
      nichts und eignet sich für Intune. Vorhandene Tokens bleiben bei einem Update erhalten.
    - Tray-App startet bei jeder Anmeldung, Eintrag im Startmenü, Firewall-Sperre für eingehende Verbindungen
    - -NoLiveView bzw. -NoTalk schalten Livebild ohne Klingeln bzw. Gegensprechen nur auf diesem PC ab

.EXAMPLE
    Erstinstallation von Hand:
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -SetTokens

.EXAMPLE
    Update oder Intune (System-Kontext), Konfiguration liegt neben dem Skript:
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
    [string] $ConfigFile = (Join-Path $PSScriptRoot 'sientrydesk.json'),
    [switch] $SetTokens,
    [switch] $NoLiveView,
    [switch] $NoTalk
)

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
# Intune zeigt die Ausgabe nicht. Als SYSTEM deshalb ein Protokoll unter C:\Windows\Temp (enthält keine Zugänge).
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

function Invoke-Native([string] $Exe, [string[]] $Arguments) {
    & $Exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') ist fehlgeschlagen (Code $LASTEXITCODE)" }
}

Write-Host '1/8 Konfiguration prüfen'
if (-not (Test-Path $ConfigFile)) {
    throw "Konfiguration nicht gefunden: $ConfigFile. Vorlage: sientrydesk.example.json"
}
# Windows PowerShell 5.1 liest kein JSON mit Kommentaren. Ganze Kommentarzeilen werden für die Prüfung entfernt,
# der Dienst selbst versteht sie.
$configText = (Get-Content -Raw -Encoding UTF8 $ConfigFile) -replace '(?m)^\s*//.*$', ''
try { $config = $configText | ConvertFrom-Json }
catch { throw "sientrydesk.json ist kein gültiges JSON (Kommentare nur auf eigenen Zeilen): $($_.Exception.Message)" }
foreach ($required in 'Host', 'AccessPin') {
    if (-not $config.$required) { throw "sientrydesk.json: '$required' fehlt." }
}

Write-Host '2/8 Laufende Instanzen beenden'
if (Get-Service $serviceName -ErrorAction SilentlyContinue) { Stop-Service $serviceName -Force }
Get-Process -Name 'SIEntryDesk' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host '3/8 Dateien kopieren'
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
foreach ($part in 'Service', 'App') {
    $target = Join-Path $installDir $part
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Copy-Item -Recurse -Force (Join-Path $PSScriptRoot $part) $target
}
# Für Admins am Gerät: Zugänge erneuern, entfernen und prüfen wie Intune (detect.ps1 aus prepare-intune.ps1).
foreach ($script in 'set-tokens.ps1', 'uninstall.ps1', 'detect.ps1') {
    $source = Join-Path $PSScriptRoot $script
    if (Test-Path $source) { Copy-Item -Force $source $installDir }
}
Get-ChildItem $installDir -Recurse -File | Unblock-File
# Reste einer Installation durch die 32-Bit-PowerShell (Intune mit 0.5.0) entfernen.
$x86Dir = Join-Path ${env:ProgramFiles(x86)} 'SIEntryDesk'
Remove-Item -Recurse -Force $x86Dir -ErrorAction SilentlyContinue
if (Test-Path $x86Dir) { Write-Warning "$x86Dir liess sich nicht ganz entfernen, Rest bitte von Hand löschen." }
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\WOW6432Node\SIEntryDesk' -Recurse -ErrorAction SilentlyContinue

Write-Host '4/8 Dienst einrichten'
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $serviceName -BinaryPathName "`"$serviceExe`"" -DisplayName 'SI EntryDesk' `
        -Description 'Klingel-Ereignisse von UniFi Access, Öffnen nur während eines Rufs' -StartupType Automatic | Out-Null
}
# Pfad immer setzen: Ein bestehender Dienst kann noch auf einen anderen Ort zeigen (z. B. Program Files (x86)).
$change = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" |
    Invoke-CimMethod -MethodName Change -Arguments @{ PathName = "`"$serviceExe`"" }
if ($change.ReturnValue -ne 0) { throw "Programmpfad des Dienstes konnte nicht gesetzt werden (Code $($change.ReturnValue))" }
Invoke-Native 'sc.exe' @('config', $serviceName, 'obj=', $serviceAccount, 'start=', 'auto')
Invoke-Native 'sc.exe' @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/30000')

Write-Host '5/8 Datenordner und Rechte'
New-Item -ItemType Directory -Force -Path $dataDir, $logDir, $stateDir | Out-Null
# Nur SYSTEM (S-1-5-18), Administratoren (S-1-5-32-544) und das Dienstkonto, keine Vererbung von ProgramData.
Invoke-Native 'icacls.exe' @($dataDir, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "${serviceAccount}:(OI)(CI)RX")
Invoke-Native 'icacls.exe' @($logDir, '/grant:r', "${serviceAccount}:(OI)(CI)M")
Invoke-Native 'icacls.exe' @($stateDir, '/grant:r', "${serviceAccount}:(OI)(CI)M")

Write-Host '6/8 Konfiguration übernehmen'
Copy-Item -Force $ConfigFile (Join-Path $dataDir 'sientrydesk.json')
# Konfiguration bis 0.2.x, sonst würden alte Werte mitgelesen.
Remove-Item -Force (Join-Path $dataDir 'config.json') -ErrorAction SilentlyContinue
# Abweichungen nur für diesen PC. Ohne Schalter bleibt eine vorhandene sientrydesk.local.json unverändert.
$local = [ordered]@{}
if ($NoLiveView) { $local['LiveView'] = $false }
if ($NoTalk) { $local['Talkback'] = $false }
if ($local.Count -gt 0) {
    $local | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $dataDir 'sientrydesk.local.json')
    Write-Host "    Auf diesem PC abgeschaltet: $(@($local.Keys) -join ', ') (sientrydesk.local.json)."
}

Write-Host '7/8 Tokens'
if ($SetTokens) {
    & (Join-Path $PSScriptRoot 'set-tokens.ps1') -ServiceExe $serviceExe -NoRestart
} elseif (Test-Path (Join-Path $dataDir 'secrets.dat')) {
    Write-Host '    Vorhandene Tokens bleiben.'
} else {
    Write-Warning 'Noch keine Tokens gespeichert. Mit set-tokens.ps1 oder install.ps1 -SetTokens setzen, sonst klingelt nichts.'
}

Write-Host '8/8 Autostart und Start'
New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'SIEntryDesk' `
    -Value "`"$appExe`"" -PropertyType String -Force | Out-Null
# Die App braucht keinen eingehenden Zugriff, das Bild kommt über 127.0.0.1. Die Sperrregel verhindert die
# Firewall-Abfrage, die LibVLC sonst auslöst, und hat Vorrang vor einer versehentlich erteilten Freigabe.
$firewallRule = 'SI EntryDesk: kein eingehender Zugriff'
Get-NetFirewallRule -DisplayName $firewallRule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $firewallRule -Direction Inbound -Program $appExe -Action Block -Profile Any | Out-Null
# Eintrag im Startmenü für alle Benutzer, falls die App beendet wurde.
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\SI EntryDesk.lnk'))
$shortcut.TargetPath = $appExe
$shortcut.WorkingDirectory = Split-Path $appExe
$shortcut.Description = 'SI EntryDesk: Türsprechstelle'
$shortcut.Save()
Start-Service $serviceName
Start-Sleep -Seconds 3
Write-Host "    Dienst: $((Get-Service $serviceName).Status)"
$version = (Get-Item $appExe).VersionInfo.ProductVersion.Split('+')[0]
New-Item -Force -Path 'HKLM:\SOFTWARE\SIEntryDesk' | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\SIEntryDesk' -Name 'Version' -Value $version

if (([Security.Principal.WindowsIdentity]::GetCurrent()).IsSystem) {
    # Intune installiert als SYSTEM. Die Tray-App wurde oben beendet und muss in den angemeldeten Sitzungen wieder
    # laufen, sonst klingelt bis zur nächsten Anmeldung nichts. Als SYSTEM geht das über die Sitzungsverwaltung,
    # gestartet wird mit dem normalen (nicht erhöhten) Token des jeweiligen Benutzers.
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

    // Startet ein Programm in jeder angemeldeten Benutzersitzung, mit dem normalen Token des Benutzers.
    // Geht nur als SYSTEM (Intune), gibt die Anzahl gestarteter Prozesse zurück.
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
        Write-Host "    App in $started angemeldeten Sitzung(en) gestartet."
    } catch {
        Write-Warning "App konnte nicht in den Sitzungen gestartet werden, sie startet bei der nächsten Anmeldung: $($_.Exception.Message)"
    }
} else {
    # Von Hand installiert: im normalen Benutzerkontext starten, nicht in dieser erhöhten Sitzung.
    Start-Process explorer.exe -ArgumentList "`"$appExe`""
}

Write-Host "    Version: $version"
Write-Host ''
Write-Host 'Fertig. Protokoll des Dienstes:' $logDir

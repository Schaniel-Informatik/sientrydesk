using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using SIEntryDesk.Core.Diagnostics;
using SIEntryDesk.Core.Peers;

namespace SIEntryDesk.App;

/// <summary>Checks on this PC for --check: everything the user can see without admin rights.</summary>
internal static class LocalChecks
{
    /// <summary>One by one, so that the window shows each item as soon as it is done. The microphone last, it takes
    /// longest.</summary>
    public static IReadOnlyList<Func<CheckItem>> All { get; } = [ServiceState, Autostart, TrayApp, PeerFirewall, SoundOutput, Microphone, TalkSound];

    /// <summary>Name of the rule from install.ps1 that lets the other PCs' messages in.</summary>
    private const string PeerFirewallRule = "SI EntryDesk: Abstimmung zwischen PCs";

    public static CheckItem Item(string name, CheckLevel level, string detail) => new(CheckReport.ThisPc, name, level, detail);

    private static CheckItem ServiceState()
    {
        try
        {
            // Directly by name instead of listing all services (review 2026-10-23).
            using var service = new ServiceController("SIEntryDesk");
            return service.Status == ServiceControllerStatus.Running
                ? Item("Dienst", CheckLevel.Ok, "läuft")
                : Item("Dienst", CheckLevel.Fail, $"{German(service.Status)}. Als Administrator: Start-Service SIEntryDesk, Protokoll unter C:\\ProgramData\\SIEntryDesk\\logs");
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 })
        {
            // ERROR_SERVICE_DOES_NOT_EXIST
            return Item("Dienst", CheckLevel.Fail, "nicht installiert. install.ps1 ausführen bzw. Intune-App zuweisen");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Item("Dienst", CheckLevel.Warn, "Zustand nicht lesbar");
        }
    }

    private static string German(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => "gestoppt",
        ServiceControllerStatus.StartPending => "startet gerade",
        ServiceControllerStatus.StopPending => "wird gerade gestoppt",
        ServiceControllerStatus.Paused => "angehalten",
        _ => status.ToString(),
    };

    private static CheckItem Autostart()
    {
        using var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        return run?.GetValue("SIEntryDesk") is string
            ? Item("Autostart", CheckLevel.Ok, "App startet bei jeder Anmeldung")
            : Item("Autostart", CheckLevel.Warn, "nicht eingetragen, die App startet nicht von selbst. install.ps1 erneut ausführen");
    }

    /// <summary>True if the check comes from the menu of the running tray app (not SIEntryDesk.exe --check).</summary>
    public static bool InTrayApp { get; set; }

    private static CheckItem TrayApp()
    {
        if (InTrayApp)
            return Item("App", CheckLevel.Ok, "läuft in dieser Sitzung");
        var session = Process.GetCurrentProcess().SessionId;
        var running = Process.GetProcessesByName("SIEntryDesk")
            .Any(p => p.Id != Environment.ProcessId && p.SessionId == session);
        return running
            ? Item("App", CheckLevel.Ok, "läuft in dieser Sitzung")
            : Item("App", CheckLevel.Fail, "läuft nicht, dieser Benutzer bekommt kein Klingeln. Über das Startmenü starten");
    }

    /// <summary>Without the rule the window does not show who already talks at another PC. Everything else works,
    /// therefore at most orange. Whether the service receives, it checks itself.</summary>
    private static CheckItem PeerFirewall()
    {
        const string name = "Abstimmung mit anderen PCs";
        try
        {
            var start = new ProcessStartInfo("netsh.exe")
            {
                ArgumentList = { "advfirewall", "firewall", "show", "rule", $"name={PeerFirewallRule}" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var netsh = Process.Start(start);
            if (netsh is null)
                return Item(name, CheckLevel.Info, "Firewall nicht lesbar");
            _ = netsh.StandardOutput.ReadToEndAsync();
            if (!netsh.WaitForExit(5000))
            {
                netsh.Kill();
                return Item(name, CheckLevel.Info, "Firewall nicht lesbar");
            }
            // netsh: 0 = rule found, 1 = no rule with this name.
            return netsh.ExitCode == 0
                ? Item(name, CheckLevel.Ok, $"UDP {PeerCodec.Port}, Firewall offen")
                : Item(name, CheckLevel.Warn, "Firewall-Regel fehlt. install.ps1 erneut ausführen");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Item(name, CheckLevel.Info, "Firewall nicht lesbar");
        }
    }

    private static CheckItem SoundOutput()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device)
                ? Item("Tonausgabe", CheckLevel.Ok, device.FriendlyName)
                : Item("Tonausgabe", CheckLevel.Fail, "kein Lautsprecher oder Kopfhörer, der Klingelton ist nicht hörbar");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Item("Tonausgabe", CheckLevel.Warn, "nicht lesbar");
        }
    }

    /// <summary>Headset or speakers, whether the door audio stays audible while talking. Information only.</summary>
    private static CheckItem TalkSound() =>
        Item("Sprechen mit", CheckLevel.Info, TalkAudio.Resolve(TalkAudio.LoadMode()).Description);

    /// <summary>Opens the microphone briefly, as when talking. Only needed for talkback, therefore at most
    /// orange.</summary>
    private static CheckItem Microphone()
    {
        string name;
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var device))
                return Item("Mikrofon", CheckLevel.Warn, "keines gefunden, Gegensprechen nicht möglich");
            name = device.FriendlyName;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Item("Mikrofon", CheckLevel.Warn, "nicht lesbar");
        }
        using var sender = TalkSender.Open(_ => { }, out var problem);
        return sender is null
            ? Item("Mikrofon", CheckLevel.Warn, problem)
            : Item("Mikrofon", CheckLevel.Ok, $"{name}, Zugriff erlaubt");
    }
}

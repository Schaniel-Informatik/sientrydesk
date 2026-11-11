using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using SIEntryDesk.Core.Diagnostics;
using SIEntryDesk.Core.Peers;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.App;

/// <summary>Checks on this PC for --check: everything the user can see without admin rights.</summary>
internal static class LocalChecks
{
    /// <summary>One by one, so that the window shows each item as soon as it is done. The microphone last, it takes
    /// longest.</summary>
    public static IReadOnlyList<Func<CheckItem>> All { get; } = [ServiceState, Autostart, TrayApp, PeerFirewall, SoundOutput, Microphone, TalkSound];

    /// <summary>Name of the rule from install.ps1 that lets the other PCs' messages in.</summary>
    private const string PeerFirewallRule = "SI EntryDesk: coordination between PCs";

    public static CheckItem Item(string name, CheckLevel level, string detail) => new(CheckReport.ThisPc, name, level, detail);

    private static CheckItem ServiceState()
    {
        try
        {
            // Directly by name instead of listing all services (review 2026-10-23).
            using var service = new ServiceController("SIEntryDesk");
            return service.Status == ServiceControllerStatus.Running
                ? Item(ServiceName, CheckLevel.Ok, Tr.Admin("läuft", "running"))
                : Item(ServiceName, CheckLevel.Fail, Tr.Admin(
                    $"{Text(service.Status)}. Als Administrator: Start-Service SIEntryDesk, Protokoll unter C:\\ProgramData\\SIEntryDesk\\logs",
                    $"{Text(service.Status)}. As administrator: Start-Service SIEntryDesk, log under C:\\ProgramData\\SIEntryDesk\\logs"));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 })
        {
            // ERROR_SERVICE_DOES_NOT_EXIST
            return Item(ServiceName, CheckLevel.Fail, Tr.Admin("nicht installiert. install.ps1 ausführen bzw. Intune-App zuweisen",
                "not installed. Run install.ps1 or assign the Intune app"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Item(ServiceName, CheckLevel.Warn, Tr.Admin("Zustand nicht lesbar", "state not readable"));
        }
    }

    private static string ServiceName => Tr.Admin("Dienst", "Service");

    private static string NotReadable => Tr.Admin("nicht lesbar", "not readable");

    private static string Text(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => Tr.Admin("gestoppt", "stopped"),
        ServiceControllerStatus.StartPending => Tr.Admin("startet gerade", "starting"),
        ServiceControllerStatus.StopPending => Tr.Admin("wird gerade gestoppt", "stopping"),
        ServiceControllerStatus.Paused => Tr.Admin("angehalten", "paused"),
        _ => status.ToString(),
    };

    private static CheckItem Autostart()
    {
        using var run = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
        return run?.GetValue("SIEntryDesk") is string
            ? Item("Autostart", CheckLevel.Ok, Tr.Admin("App startet bei jeder Anmeldung", "app starts at every sign-in"))
            : Item("Autostart", CheckLevel.Warn, Tr.Admin("nicht eingetragen, die App startet nicht von selbst. install.ps1 erneut ausführen",
                "not registered, the app does not start by itself. Run install.ps1 again"));
    }

    /// <summary>True if the check comes from the menu of the running tray app (not SIEntryDesk.exe --check).</summary>
    public static bool InTrayApp { get; set; }

    private static CheckItem TrayApp()
    {
        var running = Tr.Admin("läuft in dieser Sitzung", "running in this session");
        if (InTrayApp)
            return Item("App", CheckLevel.Ok, running);
        var session = Process.GetCurrentProcess().SessionId;
        var found = Process.GetProcessesByName("SIEntryDesk")
            .Any(p => p.Id != Environment.ProcessId && p.SessionId == session);
        return found
            ? Item("App", CheckLevel.Ok, running)
            : Item("App", CheckLevel.Fail, Tr.Admin("läuft nicht, dieser Benutzer bekommt kein Klingeln. Über das Startmenü starten",
                "not running, this user gets no ringing. Start it from the Start menu"));
    }

    /// <summary>Without the rule the window does not show who already talks at another PC. Everything else works,
    /// therefore at most orange. Whether the service receives, it checks itself.</summary>
    private static CheckItem PeerFirewall()
    {
        var name = Tr.Admin("Abstimmung mit anderen PCs", "Coordination with other PCs");
        var unreadable = Tr.Admin("Firewall nicht lesbar", "firewall not readable");
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
                return Item(name, CheckLevel.Info, unreadable);
            _ = netsh.StandardOutput.ReadToEndAsync();
            if (!netsh.WaitForExit(5000))
            {
                netsh.Kill();
                return Item(name, CheckLevel.Info, unreadable);
            }
            // netsh: 0 = rule found, 1 = no rule with this name.
            return netsh.ExitCode == 0
                ? Item(name, CheckLevel.Ok, Tr.Admin($"UDP {PeerCodec.Port}, Firewall offen", $"UDP {PeerCodec.Port}, firewall open"))
                : Item(name, CheckLevel.Warn, Tr.Admin("Firewall-Regel fehlt. install.ps1 erneut ausführen", "firewall rule missing. Run install.ps1 again"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Item(name, CheckLevel.Info, unreadable);
        }
    }

    private static CheckItem SoundOutput()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device)
                ? Item(SoundOutputName, CheckLevel.Ok, device.FriendlyName)
                : Item(SoundOutputName, CheckLevel.Fail, Tr.Admin("kein Lautsprecher oder Kopfhörer, der Klingelton ist nicht hörbar",
                    "no speakers or headphones, the ringtone is not audible"));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Item(SoundOutputName, CheckLevel.Warn, NotReadable);
        }
    }

    /// <summary>Headset or speakers, whether the door audio stays audible while talking. Information only.</summary>
    private static CheckItem TalkSound() =>
        Item(Tr.Admin("Sprechen mit", "Talk with"), CheckLevel.Info, TalkAudio.Resolve(TalkAudio.LoadMode()).Description);

    private static string SoundOutputName => Tr.Admin("Tonausgabe", "Sound output");

    private static string MicrophoneName => Tr.Admin("Mikrofon", "Microphone");

    /// <summary>Opens the microphone briefly, as when talking. Only needed for talkback, therefore at most
    /// orange.</summary>
    private static CheckItem Microphone()
    {
        string name;
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var device))
                return Item(MicrophoneName, CheckLevel.Warn, Tr.Admin("keines gefunden, Gegensprechen nicht möglich", "none found, talkback not possible"));
            name = device.FriendlyName;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Item(MicrophoneName, CheckLevel.Warn, NotReadable);
        }
        using var sender = TalkSender.Open(_ => { }, out var problem);
        return sender is null
            ? Item(MicrophoneName, CheckLevel.Warn, problem)
            : Item(MicrophoneName, CheckLevel.Ok, Tr.Admin($"{name}, Zugriff erlaubt", $"{name}, access allowed"));
    }
}

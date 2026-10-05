using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using SIEntryDesk.Core.Diagnostics;

namespace SIEntryDesk.App;

/// <summary>Prüfungen auf diesem PC für --check: alles, was der Benutzer ohne Adminrechte sehen kann.</summary>
internal static class LocalChecks
{
    /// <summary>Einzeln, damit das Fenster jeden Punkt zeigt, sobald er fertig ist. Das Mikrofon zuletzt, es dauert am längsten.</summary>
    public static IReadOnlyList<Func<CheckItem>> All { get; } = [ServiceState, Autostart, TrayApp, SoundOutput, Microphone];

    public static CheckItem Item(string name, CheckLevel level, string detail) => new(CheckReport.ThisPc, name, level, detail);

    private static CheckItem ServiceState()
    {
        try
        {
            using var service = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == "SIEntryDesk");
            if (service is null)
                return Item("Dienst", CheckLevel.Fail, "nicht installiert. install.ps1 ausführen bzw. Intune-App zuweisen");
            return service.Status == ServiceControllerStatus.Running
                ? Item("Dienst", CheckLevel.Ok, "läuft")
                : Item("Dienst", CheckLevel.Fail, $"{German(service.Status)}. Als Administrator: Start-Service SIEntryDesk, Protokoll unter C:\\ProgramData\\SIEntryDesk\\logs");
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

    /// <summary>True, wenn die Prüfung aus dem Menü der laufenden Tray-App kommt (nicht SIEntryDesk.exe --check).</summary>
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

    /// <summary>Öffnet das Mikrofon kurz, so wie beim Sprechen. Nur fürs Gegensprechen nötig, deshalb höchstens orange.</summary>
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
        using var sender = TalkSender.Start(24000, _ => Task.CompletedTask, _ => { }, out var problem);
        return sender is null
            ? Item("Mikrofon", CheckLevel.Warn, problem)
            : Item("Mikrofon", CheckLevel.Ok, $"{name}, Zugriff erlaubt");
    }
}

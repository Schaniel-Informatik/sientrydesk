using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace SIEntryDesk.App;

/// <summary>Einstellung „Tür beim Sprechen hören“ pro Benutzer.</summary>
internal enum ListenMode
{
    /// <summary>Hören, wenn das Kommunikationsgerät von Windows ein Headset oder Kopfhörer ist.</summary>
    Auto,
    Always,
    Never,
}

/// <param name="ListenWhileTalking">Ton der Tür beim Sprechen nicht stumm schalten.</param>
/// <param name="OutputDeviceId">Ton der Tür auf dieses Gerät (Kommunikationsgerät, wie bei Teams), null = Standardgerät.</param>
/// <param name="Description">Für die Prüfung, z. B. „Headset erkannt (…): Tür beim Sprechen hörbar“.</param>
internal sealed record TalkAudioPlan(bool ListenWhileTalking, string? OutputDeviceId, string Description);

/// <summary>
/// Gegensprechen mit Headset wie am Telefon (0.8.0): Ist der Ton im Ohr, gibt es kein Echo über Lautsprecher und
/// Mikrofon, der Ton der Tür bleibt beim Sprechen an und läuft über das Kommunikationsgerät. Mit Lautsprechern bleibt er
/// stumm, solange das Mikrofon aufnimmt. Windows kennt den Gerätetyp (Headset, Kopfhörer, Lautsprecher …), manche
/// USB-Headsets melden sich aber als Lautsprecher, deshalb lässt sich die Erkennung übersteuern.
/// </summary>
internal static class TalkAudio
{
    private const string SettingsKey = @"Software\SIEntryDesk";
    private const string ValueName = "ListenWhileTalking";

    // EndpointFormFactor von Windows: 3 Kopfhörer, 5 Headset, 6 Hörer.
    private static readonly HashSet<int> InEar = [3, 5, 6];

    public static ListenMode LoadMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return (key?.GetValue(ValueName) as string) switch
        {
            "always" => ListenMode.Always,
            "never" => ListenMode.Never,
            _ => ListenMode.Auto,
        };
    }

    public static void SaveMode(ListenMode mode)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(ValueName, mode switch { ListenMode.Always => "always", ListenMode.Never => "never", _ => "auto" });
    }

    public static string Label(ListenMode mode) => mode switch
    {
        ListenMode.Always => "immer",
        ListenMode.Never => "nie",
        _ => "automatisch",
    };

    /// <summary>Ist das Kommunikationsgerät für die Ausgabe ein Headset oder Kopfhörer?</summary>
    public static bool HeadsetDetected() => CommunicationsOutput() is { InEar: true };

    public static TalkAudioPlan Resolve(ListenMode mode)
    {
        var output = CommunicationsOutput();
        var name = output?.Name ?? "kein Ausgabegerät für Kommunikation";
        return mode switch
        {
            ListenMode.Never => new(false, null, "Einstellung „nie“: Tür beim Sprechen stumm"),
            ListenMode.Always => new(true, output?.Id, $"Einstellung „immer“: Tür beim Sprechen hörbar ({name})"),
            _ when output is { InEar: true } => new(true, output.Id, $"Headset erkannt ({name}): Tür beim Sprechen hörbar"),
            _ => new(false, null, $"kein Headset erkannt ({name}): Tür beim Sprechen stumm"),
        };
    }

    private sealed record Output(string Id, string Name, bool InEar);

    private static Output? CommunicationsOutput()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Communications, out var device))
                return null;
            using (device)
            {
                var key = PropertyKeys.PKEY_AudioEndpoint_FormFactor;
                var formFactor = device.Properties.Contains(key) ? Convert.ToInt32(device.Properties[key].Value) : -1;
                return new Output(device.ID, device.FriendlyName, InEar.Contains(formFactor));
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException
                                       or FormatException or OverflowException)
        {
            return null;
        }
    }
}

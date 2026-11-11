using Microsoft.Win32;
using NAudio.CoreAudioApi;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.App;

/// <summary>Setting "Sprechen mit" (talk with) per user.</summary>
internal enum ListenMode
{
    /// <summary>Detect automatically: headset if the Windows communications device is a headset or
    /// headphones.</summary>
    Auto,
    /// <summary>Headset: hear the door while talking.</summary>
    Headset,
    /// <summary>Speakers: door muted while talking.</summary>
    Speaker,
}

/// <param name="ListenWhileTalking">Do not mute the door audio while talking.</param>
/// <param name="OutputDeviceId">Door audio to this device (communications device, as with Teams), null = default
/// device.</param>
/// <param name="Description">For the check, e.g. "Headset (erkannt, …) – Tür beim Sprechen hörbar".</param>
internal sealed record TalkAudioPlan(bool ListenWhileTalking, string? OutputDeviceId, string Description);

/// <summary>
/// Talkback with a headset like on the phone (0.8.0): if the sound stays in the ear, there is no echo via speakers and
/// microphone, so the door audio stays on while talking and plays via the communications device. With speakers it stays
/// muted while the microphone records. Windows knows the device type (headset, headphones, speakers …), but some USB
/// headsets report themselves as speakers, therefore the detection can be overridden.
/// </summary>
internal static class TalkAudio
{
    private const string SettingsKey = @"Software\SIEntryDesk";
    private const string ValueName = "TalkWith";

    // Windows EndpointFormFactor: 3 headphones, 5 headset, 6 handset.
    private static readonly HashSet<int> InEar = [3, 5, 6];

    public static ListenMode LoadMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return (key?.GetValue(ValueName) as string) switch
        {
            "headset" => ListenMode.Headset,
            "speaker" => ListenMode.Speaker,
            _ => ListenMode.Auto,
        };
    }

    public static void SaveMode(ListenMode mode)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(ValueName, mode switch { ListenMode.Headset => "headset", ListenMode.Speaker => "speaker", _ => "auto" });
    }

    /// <summary>For the menu line: "Headset (erkannt)", "Lautsprecher (erkannt)", "Headset" or
    /// "Lautsprecher".</summary>
    public static string Current(ListenMode mode) => mode switch
    {
        ListenMode.Headset => Headset,
        ListenMode.Speaker => Speakers,
        _ => HeadsetDetected()
            ? Tr.User("Headset (erkannt)", "Headset (detected)", "Zestaw słuchawkowy (wykryto)")
            : Tr.User("Lautsprecher (erkannt)", "Speakers (detected)", "Głośniki (wykryto)"),
    };

    private static string Headset => Tr.User("Headset", "Headset", "Zestaw słuchawkowy");

    private static string Speakers => Tr.User("Lautsprecher", "Speakers", "Głośniki");

    /// <summary>For the choices in the menu: choice and its effect.</summary>
    public static string Choice(ListenMode mode) => mode switch
    {
        ListenMode.Headset => Tr.User("Headset – Tür beim Sprechen hören", "Headset – hear the door while talking",
            "Zestaw słuchawkowy – słychać drzwi podczas mówienia"),
        ListenMode.Speaker => Tr.User("Lautsprecher – Tür beim Sprechen stumm", "Speakers – door muted while talking",
            "Głośniki – drzwi wyciszone podczas mówienia"),
        _ => Tr.User($"automatisch erkennen (jetzt: {(HeadsetDetected() ? Headset : Speakers)})",
            $"detect automatically (now: {(HeadsetDetected() ? Headset : Speakers)})",
            $"wykryj automatycznie (teraz: {(HeadsetDetected() ? Headset : Speakers)})"),
    };

    /// <summary>Is the communications output device a headset or headphones?</summary>
    public static bool HeadsetDetected() => CommunicationsOutput() is { InEar: true };

    public static TalkAudioPlan Resolve(ListenMode mode)
    {
        var output = CommunicationsOutput();
        // For the check, therefore in the admin language.
        var name = output?.Name ?? Tr.Admin("kein Ausgabegerät für Kommunikation", "no output device for communication");
        return mode switch
        {
            ListenMode.Speaker => new(false, null, Tr.Admin("Lautsprecher (eingestellt) – Tür beim Sprechen stumm",
                "speakers (set) – door muted while talking")),
            ListenMode.Headset => new(true, output?.Id, Tr.Admin($"Headset (eingestellt, {name}) – Tür beim Sprechen hörbar",
                $"headset (set, {name}) – door audible while talking")),
            _ when output is { InEar: true } => new(true, output.Id, Tr.Admin($"Headset (erkannt, {name}) – Tür beim Sprechen hörbar",
                $"headset (detected, {name}) – door audible while talking")),
            _ => new(false, null, Tr.Admin($"Lautsprecher (erkannt, {name}) – Tür beim Sprechen stumm",
                $"speakers (detected, {name}) – door muted while talking")),
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

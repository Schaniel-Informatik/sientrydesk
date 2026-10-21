namespace SIEntryDesk.Core.Calls;

/// <summary>Wie dieser Benutzer bei einer Tür klingeln lässt (Einstellung „Klingeln“ in der App).</summary>
public enum RingMode
{
    /// <summary>Fenster und Klingelton sofort (Standard, auch für neue Türen).</summary>
    Immediate,
    /// <summary>Zuerst blinkt das Symbol, Fenster und Klingelton nach <see cref="RingSettings.Delay"/>. Wird in dieser Zeit
    /// anderswo angenommen oder bricht der Besucher ab, erscheint nichts.</summary>
    Delayed,
    /// <summary>Kein Fenster, kein Ton, kein Blinken. Das Livebild ohne Klingeln bleibt.</summary>
    Off,
}

/// <summary>
/// Einstellung „Klingeln“ pro Tür und Benutzer. Eine Bequemlichkeit, keine Sperre: Wer eine Tür auf einem PC wirklich
/// ausschliessen will (auch das Öffnen), nimmt Doors in der sientrydesk.json.
/// </summary>
public static class RingSettings
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(10);

    public static string Label(RingMode mode) => mode switch
    {
        RingMode.Delayed => $"nach {Delay.TotalSeconds:0} s",
        RingMode.Off => "aus",
        _ => "sofort",
    };

    /// <summary>Gespeicherter Wert → Einstellung. Unbekanntes gilt als „sofort“, damit nie unbemerkt eine Tür schweigt.</summary>
    public static RingMode Parse(string? value) => value switch
    {
        "delayed" => RingMode.Delayed,
        "off" => RingMode.Off,
        _ => RingMode.Immediate,
    };

    public static string ToStored(RingMode mode) => mode switch
    {
        RingMode.Delayed => "delayed",
        RingMode.Off => "off",
        _ => "immediate",
    };

    /// <summary>Türen, die nicht sofort klingeln, für den Text am Symbol, z. B. „Tür 2: aus“. Null, wenn alle sofort.</summary>
    public static string? Summary(IEnumerable<(string Name, RingMode Mode)> doors)
    {
        var parts = doors
            .Where(d => d.Mode != RingMode.Immediate)
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)
            .Select(d => $"{d.Name}: {Label(d.Mode)}")
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}

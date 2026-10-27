namespace SIEntryDesk.Core.Calls;

/// <summary>How this user lets a door ring (setting per door in the app).</summary>
public enum RingMode
{
    /// <summary>Window and ringtone immediately (default, also for new doors).</summary>
    Immediate,
    /// <summary>First the icon blinks, window and ringtone after <see cref="RingSettings.Delay"/>. If the call is
    /// answered elsewhere or the visitor cancels in the meantime, nothing appears.</summary>
    Delayed,
    /// <summary>No window, no sound, no blinking. The live view without ringing remains.</summary>
    Off,
}

/// <summary>
/// Ring setting per door and user. A convenience, not a lock: to really exclude a door on a PC (including unlocking),
/// use Doors in sientrydesk.json.
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

    /// <summary>Stored value → setting. Anything unknown counts as "immediately", so that a door never goes silent
    /// unnoticed.</summary>
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

    /// <summary>Doors that do not ring immediately, for the tray tooltip, e.g. "Tür 2: aus". Null if all ring
    /// immediately.</summary>
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

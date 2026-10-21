using Microsoft.Win32;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.App;

/// <summary>
/// Einstellung „Klingeln“ pro Tür für diesen Benutzer, unter HKCU\Software\SIEntryDesk\RingDoors\&lt;Tür-ID&gt;.
/// Der Name wird mitgespeichert, damit eine Tür im Menü bleibt, auch wenn der Dienst sie (noch) nicht meldet.
/// </summary>
internal sealed class RingPreferences
{
    private const string Key = @"Software\SIEntryDesk\RingDoors";
    private readonly Dictionary<string, (string Name, RingMode Mode)> _doors = new(StringComparer.Ordinal);

    public RingPreferences()
    {
        using var root = Registry.CurrentUser.OpenSubKey(Key);
        if (root is null)
            return;
        foreach (var doorId in root.GetSubKeyNames().Where(UntrustedText.IsSafeId))
        {
            using var door = root.OpenSubKey(doorId);
            var mode = RingSettings.Parse(door?.GetValue("Mode") as string);
            if (mode != RingMode.Immediate)
                _doors[doorId] = (UntrustedText.Clean(door?.GetValue("Name") as string, 60), mode);
        }
    }

    /// <summary>Ohne gespeicherte Einstellung und ohne Tür-ID (Dienst vor 0.7.0): sofort.</summary>
    public RingMode Get(string doorId) =>
        _doors.TryGetValue(doorId, out var door) ? door.Mode : RingMode.Immediate;

    /// <summary>Gespeicherte Abweichungen, auch für Türen, die der Dienst gerade nicht meldet.</summary>
    public IReadOnlyDictionary<string, (string Name, RingMode Mode)> Deviations => _doors;

    public void Set(string doorId, string name, RingMode mode)
    {
        if (!UntrustedText.IsSafeId(doorId))
            return;
        if (mode == RingMode.Immediate)
        {
            _doors.Remove(doorId);
            Registry.CurrentUser.DeleteSubKeyTree($@"{Key}\{doorId}", throwOnMissingSubKey: false);
            return;
        }
        _doors[doorId] = (name, mode);
        using var door = Registry.CurrentUser.CreateSubKey($@"{Key}\{doorId}");
        door.SetValue("Mode", RingSettings.ToStored(mode));
        door.SetValue("Name", name);
    }
}

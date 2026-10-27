using Microsoft.Win32;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.App;

/// <summary>
/// Ring setting per door for this user, under HKCU\Software\SIEntryDesk\RingDoors\&lt;door ID&gt;. The name is stored
/// as well, so that a door stays in the menu even if the service does not (yet) report it.
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

    /// <summary>Without a stored setting and without door ID (service before 0.7.0): immediately.</summary>
    public RingMode Get(string doorId) =>
        _doors.TryGetValue(doorId, out var door) ? door.Mode : RingMode.Immediate;

    /// <summary>Stored deviations, also for doors the service does not report at the moment.</summary>
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

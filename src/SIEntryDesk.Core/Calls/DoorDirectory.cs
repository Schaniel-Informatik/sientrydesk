using System.Text.Json;

namespace SIEntryDesk.Core.Calls;

/// <summary>A door with the Protect camera of its door station.</summary>
public sealed record KnownDoor(string DoorId, string DoorName, string CameraId);

/// <summary>
/// Which camera belongs to which door is reliably known only from the Access ring event. The directory remembers the
/// mapping at the first ring and stores it, so that the live view without ringing is available afterwards.
/// </summary>
public sealed class DoorDirectory
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, KnownDoor> _doors = new(StringComparer.Ordinal);

    /// <param name="path">JSON file, or null for in-memory only.</param>
    public DoorDirectory(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path))
            return;
        try
        {
            foreach (var door in JsonSerializer.Deserialize<List<KnownDoor>>(File.ReadAllBytes(path)) ?? [])
            {
                // The file is validated like any input.
                if (UntrustedText.IsSafeId(door.DoorId) && UntrustedText.IsSafeId(door.CameraId))
                    _doors[door.DoorId] = door with { DoorName = UntrustedText.Clean(door.DoorName) };
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>Takes over the mapping from a call. True if something changed.</summary>
    public bool Learn(CallInfo call)
    {
        if (!UntrustedText.IsSafeId(call.DoorId) || !UntrustedText.IsSafeId(call.CameraId))
            return false;
        var door = new KnownDoor(call.DoorId, call.DoorName, call.CameraId);
        lock (_gate)
        {
            if (_doors.TryGetValue(door.DoorId, out var known) && known == door)
                return false;
            _doors[door.DoorId] = door;
            Save();
            return true;
        }
    }

    /// <summary>Mapping from the configuration. It takes precedence over a learned camera, the door name is
    /// kept.</summary>
    public void Seed(string doorId, string cameraId)
    {
        if (!UntrustedText.IsSafeId(doorId) || !UntrustedText.IsSafeId(cameraId))
            return;
        lock (_gate)
        {
            var name = _doors.GetValueOrDefault(doorId)?.DoorName ?? string.Empty;
            _doors[doorId] = new KnownDoor(doorId, name, cameraId);
        }
    }

    public IReadOnlyList<KnownDoor> All()
    {
        lock (_gate)
            return _doors.Values.OrderBy(d => d.DoorName, StringComparer.CurrentCulture).ToList();
    }

    public KnownDoor? Find(string doorId)
    {
        lock (_gate)
            return _doors.GetValueOrDefault(doorId);
    }

    private void Save()
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".neu";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_doors.Values.ToList()));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without write access the mapping stays in memory until the next restart.
        }
    }
}

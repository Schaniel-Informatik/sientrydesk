using System.Text.Json;

namespace SIEntryDesk.Core.Calls;

/// <summary>Eine Tür mit der Protect-Kamera ihrer Türstation.</summary>
public sealed record KnownDoor(string DoorId, string DoorName, string CameraId);

/// <summary>
/// Welche Kamera zu welcher Tür gehört, steht zuverlässig nur im Klingel-Ereignis von Access. Das Verzeichnis merkt
/// sich die Zuordnung beim ersten Klingeln und speichert sie, damit das Livebild ohne Klingeln danach verfügbar ist.
/// </summary>
public sealed class DoorDirectory
{
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, KnownDoor> _doors = new(StringComparer.Ordinal);

    /// <param name="path">JSON-Datei, oder null für nur im Speicher.</param>
    public DoorDirectory(string? path)
    {
        _path = path;
        if (path is null || !File.Exists(path))
            return;
        try
        {
            foreach (var door in JsonSerializer.Deserialize<List<KnownDoor>>(File.ReadAllBytes(path)) ?? [])
            {
                // Die Datei wird wie jede Eingabe geprüft.
                if (UntrustedText.IsSafeId(door.DoorId) && UntrustedText.IsSafeId(door.CameraId))
                    _doors[door.DoorId] = door with { DoorName = UntrustedText.Clean(door.DoorName) };
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>Übernimmt die Zuordnung aus einem Ruf. True, wenn sich etwas geändert hat.</summary>
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
            // Ohne Schreibrecht bleibt die Zuordnung bis zum nächsten Neustart im Speicher.
        }
    }
}

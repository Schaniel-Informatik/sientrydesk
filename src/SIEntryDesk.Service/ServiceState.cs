using System.Collections.Concurrent;
using System.Reflection;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.Service;

/// <summary>Gemeinsamer Zustand für Pipe-Server und Koordinator.</summary>
internal sealed class ServiceState
{
    public static readonly string Version =
        typeof(ServiceState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "?";

    private readonly object _gate = new();
    private LinkHealth _health = LinkHealth.Starting;
    private string _problem = "Dienst startet";
    private string _warning = string.Empty;

    public CallTracker? Tracker { get; set; }

    /// <summary>Livebild eingerichtet (Protect-Schlüssel und Pin vorhanden).</summary>
    public bool VideoEnabled { get; set; }

    /// <summary>Aktuelle Türliste für das Livebild ohne Klingeln.</summary>
    public DoorsMessage Doors { get; set; } = new(false, []);

    /// <summary>Protect-Kamera-ID → Name, für die Anzeige statt der Access-Türnamen.</summary>
    public ConcurrentDictionary<string, string> CameraNames { get; } = new(StringComparer.Ordinal);

    public LinkHealth Health
    {
        get
        {
            lock (_gate)
                return _health;
        }
    }

    public void SetLink(LinkHealth health, string problem)
    {
        lock (_gate)
        {
            _health = health;
            _problem = problem;
        }
    }

    /// <summary>True, wenn sich die Warnung geändert hat.</summary>
    public bool SetWarning(string warning)
    {
        lock (_gate)
        {
            if (_warning == warning)
                return false;
            _warning = warning;
            return true;
        }
    }

    public string DisplayName(string cameraId, string fallback) =>
        cameraId.Length > 0 && CameraNames.TryGetValue(cameraId, out var name) ? name : fallback;

    public StatusMessage Status()
    {
        lock (_gate)
            return new StatusMessage(_health == LinkHealth.Ready, _problem, Version, _health, _warning);
    }

    public IEnumerable<IpcMessage> Snapshot()
    {
        yield return Status();
        yield return Doors;
        foreach (var call in Tracker?.ActiveCalls() ?? [])
            yield return ToMessage(call);
    }

    public CallStartedMessage ToMessage(CallInfo call) =>
        new(call.CallId, DisplayName(call.CameraId, call.DoorName), call.StartedAt, call.UnlockAllowed,
            VideoEnabled && call.CameraId.Length > 0);
}

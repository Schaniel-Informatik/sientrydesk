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
    private string _expiryNotice = string.Empty;

    public CallTracker? Tracker { get; set; }

    /// <summary>Livebild eingerichtet (Protect-Schlüssel und Pin vorhanden).</summary>
    public bool VideoEnabled { get; set; }

    /// <summary>Gegensprechen eingerichtet (auf diesem PC eingeschaltet und Protect-Schlüssel vorhanden).</summary>
    public bool TalkEnabled { get; set; }

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

    /// <summary>True, wenn sich etwas geändert hat. Warning färbt orange, ExpiryNotice löst zusätzlich die tägliche
    /// Meldung in der App aus.</summary>
    public bool SetWarning(string warning, string expiryNotice)
    {
        lock (_gate)
        {
            if (_warning == warning && _expiryNotice == expiryNotice)
                return false;
            _warning = warning;
            _expiryNotice = expiryNotice;
            return true;
        }
    }

    public string DisplayName(string cameraId, string fallback) =>
        cameraId.Length > 0 && CameraNames.TryGetValue(cameraId, out var name) ? name : fallback;

    public StatusMessage Status()
    {
        lock (_gate)
            return new StatusMessage(_health == LinkHealth.Ready, _problem, Version, _health,
                string.Join(", ", new[] { _warning, _expiryNotice }.Where(w => w.Length > 0)), _expiryNotice);
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
            VideoEnabled && call.CameraId.Length > 0, TalkEnabled && call.CameraId.Length > 0);
}

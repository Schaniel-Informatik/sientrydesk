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
    private bool _accessConnected;
    private string _problem = "Dienst startet";

    public CallTracker? Tracker { get; set; }

    /// <summary>Livebild eingerichtet (Protect-Schlüssel und Pin vorhanden).</summary>
    public bool VideoEnabled { get; set; }

    public void Set(bool accessConnected, string problem)
    {
        lock (_gate)
        {
            _accessConnected = accessConnected;
            _problem = problem;
        }
    }

    public StatusMessage Status()
    {
        lock (_gate)
            return new StatusMessage(_accessConnected, _problem, Version);
    }

    public IEnumerable<IpcMessage> Snapshot()
    {
        yield return Status();
        foreach (var call in Tracker?.ActiveCalls() ?? [])
            yield return ToMessage(call);
    }

    public CallStartedMessage ToMessage(CallInfo call) =>
        new(call.CallId, call.DoorName, call.StartedAt, call.UnlockAllowed, VideoEnabled && call.CameraId.Length > 0);
}

using System.Collections.Concurrent;
using System.Reflection;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.Service;

/// <summary>Shared state for pipe server and coordinator.</summary>
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

    /// <summary>State of the configuration files at startup. The service reads them only then, so the check shows which
    /// version it runs with and whether a newer one has appeared since.</summary>
    public IReadOnlyDictionary<string, DateTime?> ConfigLoaded { get; } = new Dictionary<string, DateTime?>
    {
        [ServicePaths.ConfigFile] = LastWrite(ServicePaths.ConfigFile),
        [ServicePaths.LocalConfigFile] = LastWrite(ServicePaths.LocalConfigFile),
    };

    public static DateTime? LastWrite(string path) => File.Exists(path) ? File.GetLastWriteTime(path) : null;

    /// <summary>Live view set up (Protect key and pin present).</summary>
    public bool VideoEnabled { get; set; }

    /// <summary>Talkback set up (switched on for this PC and Protect key present).</summary>
    public bool TalkEnabled { get; set; }

    /// <summary>Current door list for the live view without ringing and the per-door ring setting in the app.</summary>
    public DoorsMessage Doors { get; set; } = new(false, []);

    /// <summary>Protect camera ID → name, for display instead of the Access door names.</summary>
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

    /// <summary>True if something changed. Warning turns orange, ExpiryNotice additionally triggers the daily notice in
    /// the app.</summary>
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
                string.Join(", ", new[] { _warning, _expiryNotice }.Where(w => w.Length > 0)), _expiryNotice, TalkEnabled);
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
            VideoEnabled && call.CameraId.Length > 0, TalkEnabled && call.CameraId.Length > 0, call.DoorId);
}

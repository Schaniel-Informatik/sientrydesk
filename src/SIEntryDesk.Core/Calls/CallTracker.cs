using SIEntryDesk.Core.Access;

namespace SIEntryDesk.Core.Calls;

/// <summary>Ein laufender oder gerade beendeter Klingelruf.</summary>
/// <param name="CallId">request_id von Access.</param>
/// <param name="CameraId">Protect-Kamera-ID der Türstation, leer wenn unbekannt.</param>
public sealed record CallInfo(
    string CallId,
    string DoorId,
    string DoorName,
    string HubId,
    string CameraId,
    DateTimeOffset StartedAt,
    bool UnlockAllowed);

public abstract record CallChange(CallInfo Call);
public sealed record CallStarted(CallInfo Call) : CallChange(Call);
public sealed record CallEnded(CallInfo Call, CallEndReason Reason) : CallChange(Call);

/// <summary>Die Tür zum Ruf wurde geöffnet. OpenedBy kommt aus dem Access-Protokoll und kann nachgereicht werden.</summary>
public sealed record CallDoorOpened(CallInfo Call, string? OpenedBy) : CallChange(Call);

public enum TalkDecision
{
    Allowed,
    UnknownCall,
    CallEnded,
}

public enum UnlockDecision
{
    Allowed,
    UnknownCall,
    CallEnded,
    NotAllowedByAccess,
    AlreadyRequested,
    /// <summary>Die Tür wurde nach dem Ruf bereits geöffnet, zum Beispiel an der Tür selbst.</summary>
    AlreadyOpened,
}

/// <summary>
/// Hält den Zustand der Klingelrufe aus den Access-Ereignissen und entscheidet, ob geöffnet werden darf:
/// nur während eines laufenden Rufs, nur die Tür dieses Rufs, und pro Ruf nur einmal gleichzeitig.
/// </summary>
public sealed class CallTracker
{
    private sealed class Entry(CallInfo info)
    {
        public CallInfo Info { get; } = info;
        public bool UnlockInProgress { get; set; }
        public bool Unlocked { get; set; }
        public bool OpenedReported { get; set; }
        public string? OpenedBy { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public CallEndReason? EndReason { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly List<Entry> _recentlyEnded = [];
    private readonly TimeProvider _time;
    private readonly Func<AccessRingStarted, bool> _accept;

    /// <summary>Access beendet einen Ruf nach 60 s. Fehlt das Ende, wird der Ruf danach verworfen.</summary>
    public TimeSpan MaxCallDuration { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>So lange nach dem Ende werden Öffnungen noch dem Ruf zugeordnet.</summary>
    public TimeSpan OpenedCorrelationWindow { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>So lange darf nach „Besucher hat abgebrochen“ oder „Niemand hat abgenommen“ noch geöffnet werden,
    /// solange das Fenster mit dem Livebild offen ist. Nach „anderswo angenommen“ oder „abgelehnt“ nicht.</summary>
    public TimeSpan UnlockGraceAfterEnd { get; init; } = TimeSpan.FromSeconds(10);

    public CallTracker(TimeProvider time, Func<AccessRingStarted, bool>? accept = null)
    {
        _time = time;
        _accept = accept ?? (_ => true);
    }

    public IReadOnlyList<CallChange> Apply(AccessEvent ev)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PruneRecent(now);
            return ev switch
            {
                AccessRingStarted ring => Start(ring, now),
                AccessRingEnded end => End(end, now),
                AccessDoorUnlocked unlocked => Opened(e => e.Info.DoorId == unlocked.DoorId, openedBy: null),
                AccessUnlockLogged log => Opened(e => e.Info.HubId.Length > 0 && e.Info.HubId == log.HubId, log.ActorName),
                _ => [],
            };
        }
    }

    /// <summary>Verwirft Rufe, für die Access nach der Höchstdauer kein Ende gemeldet hat.</summary>
    public IReadOnlyList<CallChange> Expire()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PruneRecent(now);
            var changes = new List<CallChange>();
            foreach (var entry in _active.Values.Where(e => now - e.Info.StartedAt > MaxCallDuration).ToList())
            {
                _active.Remove(entry.Info.CallId);
                changes.Add(new CallEnded(entry.Info, CallEndReason.Expired));
            }
            return changes;
        }
    }

    public IReadOnlyList<CallInfo> ActiveCalls()
    {
        lock (_gate)
            return _active.Values.Select(e => e.Info).ToList();
    }

    /// <summary>Der laufende Ruf mit dieser Kennung, oder null.</summary>
    public CallInfo? FindActive(string callId)
    {
        lock (_gate)
            return _active.GetValueOrDefault(callId)?.Info;
    }

    public UnlockDecision TryBeginUnlock(string callId, out CallInfo? call)
    {
        lock (_gate)
        {
            call = null;
            if (!_active.TryGetValue(callId, out var entry))
            {
                entry = _recentlyEnded.FirstOrDefault(e => e.Info.CallId == callId);
                if (entry is null)
                    return UnlockDecision.UnknownCall;
                var reopenable = entry.EndReason is CallEndReason.Cancelled or CallEndReason.Timeout &&
                                 entry.EndedAt is { } ended && _time.GetUtcNow() - ended <= UnlockGraceAfterEnd;
                if (!reopenable)
                    return UnlockDecision.CallEnded;
                if (entry.OpenedReported)
                    return UnlockDecision.AlreadyOpened;
            }
            if (!entry.Info.UnlockAllowed)
                return UnlockDecision.NotAllowedByAccess;
            if (entry.UnlockInProgress || entry.Unlocked)
                return UnlockDecision.AlreadyRequested;

            entry.UnlockInProgress = true;
            call = entry.Info;
            return UnlockDecision.Allowed;
        }
    }

    /// <summary>
    /// Gegensprechen nur während des Rufs und bis <see cref="UnlockGraceAfterEnd"/> danach, solange das Fenster noch
    /// offen ist. Nicht nach „anderswo angenommen“ (dann spricht jemand anders) und nicht nach „abgelehnt“.
    /// </summary>
    public TalkDecision CanTalk(string callId, out CallInfo? call)
    {
        lock (_gate)
        {
            call = null;
            if (_active.TryGetValue(callId, out var entry))
            {
                call = entry.Info;
                return TalkDecision.Allowed;
            }
            entry = _recentlyEnded.FirstOrDefault(e => e.Info.CallId == callId);
            if (entry is null)
                return TalkDecision.UnknownCall;
            var inGrace = entry.EndReason is CallEndReason.Cancelled or CallEndReason.Timeout or CallEndReason.Opened &&
                          entry.EndedAt is { } ended && _time.GetUtcNow() - ended <= UnlockGraceAfterEnd;
            if (!inGrace)
                return TalkDecision.CallEnded;
            call = entry.Info;
            return TalkDecision.Allowed;
        }
    }

    /// <summary>Nach einem Fehlschlag darf es erneut versucht werden, nach Erfolg nicht.</summary>
    public void CompleteUnlock(string callId, bool success)
    {
        lock (_gate)
        {
            var entry = _active.GetValueOrDefault(callId) ?? _recentlyEnded.FirstOrDefault(e => e.Info.CallId == callId);
            if (entry is null)
                return;
            entry.UnlockInProgress = false;
            entry.Unlocked |= success;
        }
    }

    private IReadOnlyList<CallChange> Start(AccessRingStarted ring, DateTimeOffset now)
    {
        if (_active.ContainsKey(ring.RequestId) || !_accept(ring))
            return [];
        var info = new CallInfo(
            ring.RequestId, ring.DoorId, ring.DoorName, ring.HubId,
            ring.IsCamera ? ring.DeviceId : string.Empty,
            now, !ring.UnlockingNotAllowed);
        _active[ring.RequestId] = new Entry(info);
        return [new CallStarted(info)];
    }

    private IReadOnlyList<CallChange> End(AccessRingEnded end, DateTimeOffset now)
    {
        // Folgemeldungen ohne Kennung (nach dem Öffnen) betreffen keinen bestimmten Ruf.
        if (end.RequestId is null || !_active.Remove(end.RequestId, out var entry))
            return [];
        entry.EndedAt = now;
        entry.EndReason = end.Reason;
        // Ein laufendes Öffnen bleibt bestehen, sonst wäre in der Nachfrist ein zweites möglich.
        _recentlyEnded.Add(entry);
        return [new CallEnded(entry.Info, end.Reason)];
    }

    private IReadOnlyList<CallChange> Opened(Func<Entry, bool> matches, string? openedBy)
    {
        var entry = _active.Values.Where(matches).OrderByDescending(e => e.Info.StartedAt).FirstOrDefault()
                    ?? _recentlyEnded.Where(matches).OrderByDescending(e => e.EndedAt).FirstOrDefault();
        if (entry is null)
            return [];

        var name = string.IsNullOrWhiteSpace(openedBy) ? null : openedBy;
        // Melden, wenn die Öffnung neu ist oder der Name nachgereicht wird.
        if (entry.OpenedReported && (name is null || name == entry.OpenedBy))
            return [];
        entry.OpenedReported = true;
        entry.OpenedBy = name ?? entry.OpenedBy;
        return [new CallDoorOpened(entry.Info, entry.OpenedBy)];
    }

    private void PruneRecent(DateTimeOffset now) =>
        _recentlyEnded.RemoveAll(e => e.EndedAt is { } ended && now - ended > OpenedCorrelationWindow);
}

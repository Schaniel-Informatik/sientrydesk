using SIEntryDesk.Core.Access;

namespace SIEntryDesk.Core.Calls;

/// <summary>A running or just ended doorbell call.</summary>
/// <param name="CallId">request_id from Access.</param>
/// <param name="CameraId">Protect camera ID of the door station, empty if unknown.</param>
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

/// <summary>The door of the call was unlocked. OpenedBy comes from the Access log and may arrive later.</summary>
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
    /// <summary>The door was already unlocked after the call, for example at the door itself.</summary>
    AlreadyOpened,
}

/// <summary>
/// Keeps the state of doorbell calls from the Access events and decides whether unlocking is allowed:
/// only during a running call, only the door of that call, and only once at a time per call.
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

    /// <summary>Access ends a call after 60 s. If the end is missing, the call is discarded afterwards.</summary>
    public TimeSpan MaxCallDuration { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>For this long after the end, unlocks are still attributed to the call.</summary>
    public TimeSpan OpenedCorrelationWindow { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>For this long, unlocking is still allowed after "visitor cancelled" or "nobody answered", while the
    /// window with the live view is still open. Not after "answered elsewhere" or "declined".</summary>
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

    /// <summary>Discards calls for which Access reported no end after the maximum duration.</summary>
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

    /// <summary>The running call with this identifier, or null.</summary>
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
    /// Talkback only during the call and up to <see cref="UnlockGraceAfterEnd"/> afterwards, while the window is still
    /// open. Not after "answered elsewhere" (then someone else is talking) and not after "declined".
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

    /// <summary>After a failure another attempt is allowed, after success not.</summary>
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
        // Follow-up messages without identifier (after unlocking) do not concern a specific call.
        if (end.RequestId is null || !_active.Remove(end.RequestId, out var entry))
            return [];
        entry.EndedAt = now;
        entry.EndReason = end.Reason;
        // An unlock in progress stays, otherwise a second one would be possible in the grace period.
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
        // Report if the unlock is new or the name arrives later.
        if (entry.OpenedReported && (name is null || name == entry.OpenedBy))
            return [];
        entry.OpenedReported = true;
        entry.OpenedBy = name ?? entry.OpenedBy;
        return [new CallDoorOpened(entry.Info, entry.OpenedBy)];
    }

    private void PruneRecent(DateTimeOffset now) =>
        _recentlyEnded.RemoveAll(e => e.EndedAt is { } ended && now - ended > OpenedCorrelationWindow);
}

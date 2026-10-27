namespace SIEntryDesk.Core.Calls;

/// <summary>Who is currently talking at a door. ClientId is the app's connection to the service.</summary>
public sealed record TalkTurn(string DoorId, string CallId, int ClientId, string Speaker, DateTimeOffset Since);

/// <summary>
/// Grants talking at a door on this PC: only one app at a time, at most <see cref="MaxTalk"/> in one go. There is no
/// coordination between PCs, each PC has its own service.
/// </summary>
public sealed class TalkFloor(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TalkTurn> _turns = new(StringComparer.Ordinal);

    /// <summary>Protection against a stuck talk button (decision 2026-10-02).</summary>
    public TimeSpan MaxTalk { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Grants talking if nobody else is talking at this door. Otherwise busy names the speaker.</summary>
    public bool TryTake(string doorId, string callId, int clientId, string speaker, out TalkTurn turn)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_turns.TryGetValue(doorId, out var current) && current.ClientId != clientId && now - current.Since < MaxTalk)
            {
                turn = current;
                return false;
            }
            turn = new TalkTurn(doorId, callId, clientId, speaker, now);
            _turns[doorId] = turn;
            return true;
        }
    }

    /// <summary>This app's current talk turn at this door, if it is still valid.</summary>
    public TalkTurn? Current(string callId, int clientId)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            return _turns.Values.FirstOrDefault(t => t.CallId == callId && t.ClientId == clientId && now - t.Since < MaxTalk);
        }
    }

    public TalkTurn? Release(string callId, int clientId) =>
        RemoveWhere(t => t.CallId == callId && t.ClientId == clientId).FirstOrDefault();

    /// <summary>When an app disconnects.</summary>
    public IReadOnlyList<TalkTurn> ReleaseClient(int clientId) => RemoveWhere(t => t.ClientId == clientId);

    /// <summary>When a call ends.</summary>
    public IReadOnlyList<TalkTurn> ReleaseCall(string callId) => RemoveWhere(t => t.CallId == callId);

    /// <summary>Ends talking that lasts longer than <see cref="MaxTalk"/>.</summary>
    public IReadOnlyList<TalkTurn> Expire()
    {
        var now = time.GetUtcNow();
        return RemoveWhere(t => now - t.Since >= MaxTalk);
    }

    private List<TalkTurn> RemoveWhere(Func<TalkTurn, bool> predicate)
    {
        lock (_gate)
        {
            var removed = _turns.Values.Where(predicate).ToList();
            foreach (var turn in removed)
                _turns.Remove(turn.DoorId);
            return removed;
        }
    }
}

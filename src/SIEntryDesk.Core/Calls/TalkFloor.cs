namespace SIEntryDesk.Core.Calls;

/// <summary>Wer an einer Tür gerade spricht. ClientId ist die Verbindung der App zum Dienst.</summary>
public sealed record TalkTurn(string DoorId, string CallId, int ClientId, string Speaker, DateTimeOffset Since);

/// <summary>
/// Vergibt das Sprechen an einer Tür auf diesem PC: immer nur eine App gleichzeitig, höchstens <see cref="MaxTalk"/>
/// am Stück. Zwischen PCs gibt es keine Absprache, jeder PC hat seinen eigenen Dienst.
/// </summary>
public sealed class TalkFloor(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TalkTurn> _turns = new(StringComparer.Ordinal);

    /// <summary>Schutz gegen eine hängende Sprechtaste (Entscheid 2026-10-02).</summary>
    public TimeSpan MaxTalk { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gibt das Sprechen frei, wenn niemand sonst an dieser Tür spricht. Busy nennt dann den Sprecher.</summary>
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

    /// <summary>Das laufende Sprechen dieser App an dieser Tür, wenn es noch gilt.</summary>
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

    /// <summary>Beim Trennen einer App.</summary>
    public IReadOnlyList<TalkTurn> ReleaseClient(int clientId) => RemoveWhere(t => t.ClientId == clientId);

    /// <summary>Beim Ende eines Rufs.</summary>
    public IReadOnlyList<TalkTurn> ReleaseCall(string callId) => RemoveWhere(t => t.CallId == callId);

    /// <summary>Beendet Sprechen, das länger als <see cref="MaxTalk"/> dauert.</summary>
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

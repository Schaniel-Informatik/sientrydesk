namespace SIEntryDesk.Core.Peers;

/// <summary>
/// Who answered which call first, from this PC and from the others. All PCs order the same way (time, then PC, then
/// user), so that they show the same person even if the messages arrive in a different order.
/// </summary>
public sealed class CallAnswers
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PeerAnnouncement> _first = new(StringComparer.Ordinal);

    /// <summary>True if the announcement is now the first one for its call (new or earlier than the known
    /// one).</summary>
    public bool Offer(PeerAnnouncement announcement)
    {
        lock (_gate)
        {
            if (_first.TryGetValue(announcement.CallId, out var known) && Compare(known, announcement) <= 0)
                return false;
            _first[announcement.CallId] = announcement;
            return true;
        }
    }

    public PeerAnnouncement? Find(string callId)
    {
        lock (_gate)
            return _first.GetValueOrDefault(callId);
    }

    /// <summary>Forgets calls that are over.</summary>
    public void RemoveWhere(Func<string, bool> ended)
    {
        lock (_gate)
        {
            foreach (var callId in _first.Keys.Where(ended).ToList())
                _first.Remove(callId);
        }
    }

    private static int Compare(PeerAnnouncement a, PeerAnnouncement b)
    {
        var byTime = a.At.CompareTo(b.At);
        if (byTime != 0)
            return byTime;
        var byPc = string.CompareOrdinal(a.Pc, b.Pc);
        return byPc != 0 ? byPc : string.CompareOrdinal(a.User, b.User);
    }
}

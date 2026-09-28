namespace SIEntryDesk.Core.Calls;

/// <summary>Warum ein Klingelruf endet. Die Werte entsprechen dem reason_code von access.remote_view.change.</summary>
public enum CallEndReason
{
    Unknown = 0,
    Timeout = 105,
    Declined = 106,
    Opened = 107,
    Cancelled = 108,
    AnsweredElsewhere = 400,

    /// <summary>Kein Ende von Access erhalten, der Ruf wurde nach der Höchstdauer verworfen.</summary>
    Expired = -1,
}

public static class CallEndReasons
{
    public static CallEndReason FromCode(int code) =>
        code is 105 or 106 or 107 or 108 or 400 ? (CallEndReason)code : CallEndReason.Unknown;

    public static string ToGerman(CallEndReason reason) => reason switch
    {
        CallEndReason.Timeout => "Niemand hat abgenommen",
        CallEndReason.Declined => "Öffnen abgelehnt",
        CallEndReason.Opened => "Tür geöffnet",
        CallEndReason.Cancelled => "Besucher hat abgebrochen",
        CallEndReason.AnsweredElsewhere => "Anderswo angenommen",
        CallEndReason.Expired => "Ruf beendet",
        _ => "Ruf beendet",
    };
}

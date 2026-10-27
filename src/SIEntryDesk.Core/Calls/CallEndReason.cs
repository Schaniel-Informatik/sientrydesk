namespace SIEntryDesk.Core.Calls;

/// <summary>Why a doorbell call ends. The values match the reason_code of access.remote_view.change.</summary>
public enum CallEndReason
{
    Unknown = 0,
    Timeout = 105,
    Declined = 106,
    Opened = 107,
    Cancelled = 108,
    AnsweredElsewhere = 400,

    /// <summary>No end received from Access, the call was discarded after the maximum duration.</summary>
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

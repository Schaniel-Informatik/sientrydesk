using SIEntryDesk.Core.Texts;

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

    /// <summary>For the ring window, in the user language.</summary>
    public static string Text(CallEndReason reason) => reason switch
    {
        CallEndReason.Timeout => Tr.User("Niemand hat abgenommen", "Nobody answered", "Nikt nie odebrał"),
        CallEndReason.Declined => Tr.User("Öffnen abgelehnt", "Opening declined", "Otwarcie odrzucone"),
        CallEndReason.Opened => DoorOpened,
        CallEndReason.Cancelled => Tr.User("Besucher hat abgebrochen", "Visitor hung up", "Gość się rozłączył"),
        CallEndReason.AnsweredElsewhere => Tr.User("Anderswo angenommen", "Answered elsewhere", "Odebrano gdzie indziej"),
        _ => CallOver,
    };

    public static string DoorOpened => Tr.User("Tür geöffnet", "Door opened", "Drzwi otwarte");

    public static string CallOver => Tr.User("Ruf beendet", "Call ended", "Połączenie zakończone");
}

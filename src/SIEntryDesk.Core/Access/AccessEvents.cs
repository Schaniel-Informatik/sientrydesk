using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.Core.Access;

/// <summary>Ereignisse aus dem WebSocket der Access Developer API, bereits geprüft und bereinigt.</summary>
public abstract record AccessEvent;

/// <summary>Das "Hello", das Access alle 5 s sendet. Dient als Lebenszeichen der Verbindung.</summary>
public sealed record AccessHeartbeat : AccessEvent;

/// <summary>access.remote_view: Es klingelt.</summary>
/// <param name="RequestId">Kennung des Rufs, verknüpft Klingeln und Ende.</param>
/// <param name="DoorId">Tür-ID für den Aufruf zum Öffnen.</param>
/// <param name="HubId">ID des UA Hub, über sie verweisen Protokoll-Ereignisse auf die Tür.</param>
/// <param name="DeviceId">Bei Türstationen aus Protect (IsCamera) die Protect-Kamera-ID.</param>
public sealed record AccessRingStarted(
    string RequestId,
    string DoorId,
    string DoorName,
    string HubId,
    string DeviceId,
    string DeviceType,
    bool IsCamera,
    bool UnlockingNotAllowed,
    DateTimeOffset? CreatedAt) : AccessEvent;

/// <summary>access.remote_view.change: Der Ruf ist beendet. RequestId fehlt bei Folgemeldungen nach dem Öffnen.</summary>
public sealed record AccessRingEnded(string? RequestId, int ReasonCode) : AccessEvent
{
    public CallEndReason Reason => CallEndReasons.FromCode(ReasonCode);
}

/// <summary>access.data.device.remote_unlock: Eine Tür wurde per Fernzugriff geöffnet.</summary>
public sealed record AccessDoorUnlocked(string DoorId, string HubId, string DoorName) : AccessEvent;

/// <summary>access.logs.add mit einer erfolgreichen Türöffnung: nennt, wer geöffnet hat.</summary>
public sealed record AccessUnlockLogged(string HubId, string ActorName, string? CredentialProvider) : AccessEvent;

/// <summary>Alle übrigen Ereignisse, nur mit Namen.</summary>
public sealed record AccessOtherEvent(string Name) : AccessEvent;

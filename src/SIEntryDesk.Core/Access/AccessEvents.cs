using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.Core.Access;

/// <summary>Events from the WebSocket of the Access Developer API, already validated and sanitized.</summary>
public abstract record AccessEvent;

/// <summary>The "Hello" Access sends every 5 s. Serves as a sign of life of the connection.</summary>
public sealed record AccessHeartbeat : AccessEvent;

/// <summary>access.remote_view: the doorbell rings.</summary>
/// <param name="RequestId">Identifier of the call, links ring and end.</param>
/// <param name="DoorId">Door ID for the unlock call.</param>
/// <param name="HubId">ID of the UA Hub; log events refer to the door through it.</param>
/// <param name="DeviceId">For door stations from Protect (IsCamera), the Protect camera ID.</param>
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

/// <summary>access.remote_view.change: the call has ended. RequestId is missing in follow-up messages after
/// unlocking.</summary>
public sealed record AccessRingEnded(string? RequestId, int ReasonCode) : AccessEvent
{
    public CallEndReason Reason => CallEndReasons.FromCode(ReasonCode);
}

/// <summary>access.data.device.remote_unlock: a door was unlocked remotely.</summary>
public sealed record AccessDoorUnlocked(string DoorId, string HubId, string DoorName) : AccessEvent;

/// <summary>access.logs.add with a successful door unlock: tells who unlocked.</summary>
public sealed record AccessUnlockLogged(string HubId, string ActorName, string? CredentialProvider) : AccessEvent;

/// <summary>All other events, name only.</summary>
public sealed record AccessOtherEvent(string Name) : AccessEvent;

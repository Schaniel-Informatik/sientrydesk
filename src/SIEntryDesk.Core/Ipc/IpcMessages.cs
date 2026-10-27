using System.Text.Json.Serialization;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Diagnostics;

namespace SIEntryDesk.Core.Ipc;

/// <summary>
/// Messages between service and tray app. The app receives no tokens and no console addresses, only what it has to
/// show; door IDs only as identifiers for the live view and the per-door ring setting. It requests unlocking by call
/// identifier, the service decides the rest.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatusMessage), "status")]
[JsonDerivedType(typeof(CallStartedMessage), "callStarted")]
[JsonDerivedType(typeof(CallEndedMessage), "callEnded")]
[JsonDerivedType(typeof(DoorOpenedMessage), "doorOpened")]
[JsonDerivedType(typeof(UnlockRequest), "unlock")]
[JsonDerivedType(typeof(UnlockResultMessage), "unlockResult")]
[JsonDerivedType(typeof(VideoRequest), "video")]
[JsonDerivedType(typeof(VideoReadyMessage), "videoReady")]
[JsonDerivedType(typeof(VideoUnavailableMessage), "videoUnavailable")]
[JsonDerivedType(typeof(DoorsMessage), "doors")]
[JsonDerivedType(typeof(LiveViewRequest), "liveView")]
[JsonDerivedType(typeof(LiveViewReadyMessage), "liveViewReady")]
[JsonDerivedType(typeof(LiveViewUnavailableMessage), "liveViewUnavailable")]
[JsonDerivedType(typeof(TalkRequest), "talk")]
[JsonDerivedType(typeof(TalkResultMessage), "talkResult")]
[JsonDerivedType(typeof(TalkAudioMessage), "talkAudio")]
[JsonDerivedType(typeof(TalkEndedMessage), "talkEnded")]
[JsonDerivedType(typeof(CheckRequest), "check")]
[JsonDerivedType(typeof(CheckResultMessage), "checkResult")]
public abstract record IpcMessage;

/// <summary>State of the service as seen by the display.</summary>
public enum LinkHealth
{
    Starting,
    /// <summary>Rings arrive.</summary>
    Ready,
    /// <summary>Console not reachable, typically outside the company network. No alarm.</summary>
    Unreachable,
    /// <summary>Console reachable, but disturbed (token, certificate, configuration, interruption). Alarm.</summary>
    Error,
}

/// <summary>Service → app: connection state. Problem explains Unreachable/Error, Warning concerns minor issues such as
/// the live view or keys about to expire. TalkEnabled: talkback is set up on this PC (from 0.8.0, for the "Sprechen
/// mit" setting in the menu).</summary>
public sealed record StatusMessage(
    bool AccessConnected,
    string Problem,
    string ServiceVersion,
    LinkHealth Health = LinkHealth.Starting,
    string Warning = "",
    string ExpiryNotice = "",
    bool TalkEnabled = false) : IpcMessage;

/// <summary>Service → app: the doorbell rings. VideoAvailable: the app can request a live view. TalkAvailable: talkback
/// is set up on this PC and the door has a known camera. DoorId: for the per-door ring setting, empty with a service
/// before 0.7.0.</summary>
public sealed record CallStartedMessage(
    string CallId, string DoorName, DateTimeOffset StartedAt, bool UnlockAllowed, bool VideoAvailable = false,
    bool TalkAvailable = false, string DoorId = "") : IpcMessage;

/// <summary>Service → app: the call has ended, the window closes.</summary>
public sealed record CallEndedMessage(string CallId, CallEndReason Reason) : IpcMessage;

/// <summary>Service → app: the door of the call was unlocked, OpenedBy possibly arrives later.</summary>
public sealed record DoorOpenedMessage(string CallId, string? OpenedBy) : IpcMessage;

/// <summary>App → service: please unlock the door of this call.</summary>
public sealed record UnlockRequest(string CallId) : IpcMessage;

/// <summary>Service → app: result of the unlock request, only to the requesting app.</summary>
public sealed record UnlockResultMessage(string CallId, bool Success, string Message) : IpcMessage;

/// <summary>App → service: request the live view for this call.</summary>
public sealed record VideoRequest(string CallId) : IpcMessage;

/// <summary>Service → app: one-time address (rtsp://127.0.0.1:…) for the live view, valid until shortly after the call
/// ends.</summary>
public sealed record VideoReadyMessage(string CallId, string Url) : IpcMessage;

/// <summary>Service → app: no live view, with a reason to display.</summary>
public sealed record VideoUnavailableMessage(string CallId, string Reason) : IpcMessage;

public sealed record DoorEntry(string DoorId, string Name);

/// <summary>Service → app: doors of this PC, for the live view without ringing and the per-door ring setting. Enabled =
/// live view without ringing switched on for this PC. A door appears with DoorCameras in the configuration or after its
/// first ring, because only then its camera is known.</summary>
public sealed record DoorsMessage(bool Enabled, DoorEntry[] Doors) : IpcMessage
{
    public bool Equals(DoorsMessage? other) => other is not null && Enabled == other.Enabled && Doors.SequenceEqual(other.Doors);
    public override int GetHashCode() => HashCode.Combine(Enabled, Doors.Length);
}

/// <summary>App → service: live view of a door without ringing.</summary>
public sealed record LiveViewRequest(string DoorId) : IpcMessage;

/// <summary>Service → app: one-time address for the live view, valid until Until.</summary>
public sealed record LiveViewReadyMessage(string DoorId, string DoorName, string Url, DateTimeOffset Until) : IpcMessage;

/// <summary>Service → app: no live view for this door, with a reason.</summary>
public sealed record LiveViewUnavailableMessage(string DoorId, string Reason) : IpcMessage;

/// <summary>App → service: talk button pressed (Start) or released. The service gets the talkback session from Protect
/// and sends the audio to the door station itself, the app never learns its address.</summary>
public sealed record TalkRequest(string CallId, bool Start) : IpcMessage;

/// <summary>Service → app: answer to the start. SampleRate: how the app encodes (Opus, mono, 20 ms). MaxSeconds: the
/// maximum in one go.</summary>
public sealed record TalkResultMessage(string CallId, bool Granted, string Message, int SampleRate = 0, int MaxSeconds = 0)
    : IpcMessage;

/// <summary>App → service: one Opus packet of 20 ms, Base64. Only valid while the service has granted
/// talking.</summary>
public sealed record TalkAudioMessage(string CallId, string Opus) : IpcMessage;

/// <summary>Service → app: talking ended without the button being released (maximum duration, end of call,
/// error).</summary>
public sealed record TalkEndedMessage(string CallId, string Reason) : IpcMessage;

/// <summary>App → service: check with the stored credentials (SIEntryDesk.exe --check).</summary>
public sealed record CheckRequest : IpcMessage;

/// <summary>Service → app: result of the check, without secrets.</summary>
public sealed record CheckResultMessage(CheckItem[] Items) : IpcMessage;

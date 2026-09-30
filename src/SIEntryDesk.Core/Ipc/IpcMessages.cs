using System.Text.Json.Serialization;
using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.Core.Ipc;

/// <summary>
/// Nachrichten zwischen Dienst und Tray-App. Die App erhält keine Tokens, Tür-IDs oder Stream-Adressen,
/// nur was sie anzeigen muss. Öffnen fordert sie über die Ruf-Kennung an, den Rest entscheidet der Dienst.
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
public abstract record IpcMessage;

/// <summary>Zustand des Dienstes aus Sicht der Anzeige.</summary>
public enum LinkHealth
{
    Starting,
    /// <summary>Klingeln kommt an.</summary>
    Ready,
    /// <summary>Konsole nicht erreichbar, typisch ausserhalb des Firmennetzes. Kein Alarm.</summary>
    Unreachable,
    /// <summary>Konsole erreichbar, aber gestört (Token, Zertifikat, Konfiguration, Unterbruch). Alarm.</summary>
    Error,
}

/// <summary>Dienst → App: Verbindungszustand. Problem erklärt Unreachable/Error, Warning betrifft Nebensachen
/// wie das Livebild oder bald ablaufende Schlüssel.</summary>
public sealed record StatusMessage(
    bool AccessConnected,
    string Problem,
    string ServiceVersion,
    LinkHealth Health = LinkHealth.Starting,
    string Warning = "") : IpcMessage;

/// <summary>Dienst → App: Es klingelt. VideoAvailable: Die App kann ein Livebild anfordern.</summary>
public sealed record CallStartedMessage(
    string CallId, string DoorName, DateTimeOffset StartedAt, bool UnlockAllowed, bool VideoAvailable = false) : IpcMessage;

/// <summary>Dienst → App: Der Ruf ist beendet, das Fenster schliesst sich.</summary>
public sealed record CallEndedMessage(string CallId, CallEndReason Reason) : IpcMessage;

/// <summary>Dienst → App: Die Tür zum Ruf wurde geöffnet, OpenedBy ggf. nachgereicht.</summary>
public sealed record DoorOpenedMessage(string CallId, string? OpenedBy) : IpcMessage;

/// <summary>App → Dienst: Bitte die Tür zu diesem Ruf öffnen.</summary>
public sealed record UnlockRequest(string CallId) : IpcMessage;

/// <summary>Dienst → App: Ergebnis der Öffnen-Anfrage, nur an die anfragende App.</summary>
public sealed record UnlockResultMessage(string CallId, bool Success, string Message) : IpcMessage;

/// <summary>App → Dienst: Livebild zu diesem Ruf anfordern.</summary>
public sealed record VideoRequest(string CallId) : IpcMessage;

/// <summary>Dienst → App: Einmal-Adresse (rtsp://127.0.0.1:…) für das Livebild, gültig bis kurz nach dem Rufende.</summary>
public sealed record VideoReadyMessage(string CallId, string Url) : IpcMessage;

/// <summary>Dienst → App: Kein Livebild, mit Grund zur Anzeige.</summary>
public sealed record VideoUnavailableMessage(string CallId, string Reason) : IpcMessage;

public sealed record DoorEntry(string DoorId, string Name);

/// <summary>Dienst → App: Türen, deren Livebild ohne Klingeln abrufbar ist. Enabled = auf diesem PC eingeschaltet.
/// Eine Tür erscheint nach ihrem ersten Klingeln, weil erst dann ihre Kamera bekannt ist.</summary>
public sealed record DoorsMessage(bool Enabled, DoorEntry[] Doors) : IpcMessage
{
    public bool Equals(DoorsMessage? other) => other is not null && Enabled == other.Enabled && Doors.SequenceEqual(other.Doors);
    public override int GetHashCode() => HashCode.Combine(Enabled, Doors.Length);
}

/// <summary>App → Dienst: Livebild einer Tür ohne Klingeln.</summary>
public sealed record LiveViewRequest(string DoorId) : IpcMessage;

/// <summary>Dienst → App: Einmal-Adresse für das Livebild, gültig bis Until.</summary>
public sealed record LiveViewReadyMessage(string DoorId, string DoorName, string Url, DateTimeOffset Until) : IpcMessage;

/// <summary>Dienst → App: Kein Livebild für diese Tür, mit Grund.</summary>
public sealed record LiveViewUnavailableMessage(string DoorId, string Reason) : IpcMessage;

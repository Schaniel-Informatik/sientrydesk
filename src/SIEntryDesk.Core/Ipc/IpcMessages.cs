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
public abstract record IpcMessage;

/// <summary>Dienst → App: Verbindungszustand. Problem ist leer, wenn alles läuft.</summary>
public sealed record StatusMessage(bool AccessConnected, string Problem, string ServiceVersion) : IpcMessage;

/// <summary>Dienst → App: Es klingelt.</summary>
public sealed record CallStartedMessage(string CallId, string DoorName, DateTimeOffset StartedAt, bool UnlockAllowed) : IpcMessage;

/// <summary>Dienst → App: Der Ruf ist beendet, das Fenster schliesst sich.</summary>
public sealed record CallEndedMessage(string CallId, CallEndReason Reason) : IpcMessage;

/// <summary>Dienst → App: Die Tür zum Ruf wurde geöffnet, OpenedBy ggf. nachgereicht.</summary>
public sealed record DoorOpenedMessage(string CallId, string? OpenedBy) : IpcMessage;

/// <summary>App → Dienst: Bitte die Tür zu diesem Ruf öffnen.</summary>
public sealed record UnlockRequest(string CallId) : IpcMessage;

/// <summary>Dienst → App: Ergebnis der Öffnen-Anfrage, nur an die anfragende App.</summary>
public sealed record UnlockResultMessage(string CallId, bool Success, string Message) : IpcMessage;

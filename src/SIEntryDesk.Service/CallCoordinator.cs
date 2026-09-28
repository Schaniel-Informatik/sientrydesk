using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;
using SIEntryDesk.Core.Security;

namespace SIEntryDesk.Service;

/// <summary>Verbindet Access-Ereignisse, Rufzustand und die Tray-Apps. Öffnet nur nach den Regeln des CallTracker.</summary>
internal sealed class CallCoordinator(
    IOptions<EntryDeskOptions> options,
    ServiceState state,
    PipeServer pipes,
    TimeProvider time,
    ILogger<CallCoordinator> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var opt = options.Value;
        if (opt.Validate() is { } problem)
        {
            Fail($"Konfiguration unvollständig: {problem}");
            return;
        }

        Secrets secrets;
        try
        {
            secrets = SecretStore.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       or JsonException or InvalidDataException)
        {
            Fail($"Tokens fehlen oder sind nicht lesbar ({ex.GetType().Name})");
            return;
        }

        var host = opt.Host.Trim();
        var pin = CertificatePin.Parse(opt.AccessPin);
        var tracker = new CallTracker(time, opt.AcceptsDoor);
        state.Tracker = tracker;
        state.Set(false, "Noch keine Verbindung zu Access");

        using var api = new AccessApiClient(host, secrets.AccessUnlockToken ?? secrets.AccessToken, pin);
        pipes.UnlockHandler = (callId, user, token) => UnlockAsync(tracker, api, callId, user, token);
        log.LogInformation("Start: Konsole {Host}, Türen {Doors}, eigener Öffnen-Token {Separate}",
            host, opt.Doors.Count == 0 ? "alle" : string.Join(", ", opt.Doors), secrets.AccessUnlockToken is not null);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        var expiry = ExpireLoopAsync(timer, tracker, ct);
        var stream = new AccessEventStream(host, secrets.AccessToken, pin, log);
        await stream.RunAsync(
            (ev, _) =>
            {
                Publish(tracker.Apply(ev));
                return ValueTask.CompletedTask;
            },
            connected =>
            {
                state.Set(connected, connected ? string.Empty : "Keine Verbindung zu Access");
                pipes.Broadcast(state.Status());
            },
            ct).ConfigureAwait(false);
        await expiry.ConfigureAwait(false);
    }

    private void Fail(string problem)
    {
        log.LogError("{Problem}", problem);
        state.Set(false, problem);
        pipes.Broadcast(state.Status());
    }

    private async Task ExpireLoopAsync(PeriodicTimer timer, CallTracker tracker, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Publish(tracker.Expire());
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Publish(IReadOnlyList<CallChange> changes)
    {
        foreach (var change in changes)
        {
            switch (change)
            {
                case CallStarted s:
                    log.LogInformation("Klingeln: {Door} (Ruf {Call})", s.Call.DoorName, s.Call.CallId);
                    pipes.Broadcast(new CallStartedMessage(s.Call.CallId, s.Call.DoorName, s.Call.StartedAt, s.Call.UnlockAllowed));
                    break;
                case CallEnded e:
                    log.LogInformation("Ruf beendet: {Door}, {Reason} (Ruf {Call})", e.Call.DoorName, e.Reason, e.Call.CallId);
                    pipes.Broadcast(new CallEndedMessage(e.Call.CallId, e.Reason));
                    break;
                case CallDoorOpened o:
                    log.LogInformation("Tür geöffnet: {Door} von {By} (Ruf {Call})", o.Call.DoorName, o.OpenedBy ?? "?", o.Call.CallId);
                    pipes.Broadcast(new DoorOpenedMessage(o.Call.CallId, o.OpenedBy));
                    break;
            }
        }
    }

    private async Task<UnlockResultMessage> UnlockAsync(
        CallTracker tracker, AccessApiClient api, string callId, string user, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(callId))
            return new UnlockResultMessage(string.Empty, false, "Ungültige Anfrage");

        var decision = tracker.TryBeginUnlock(callId, out var call);
        if (decision != UnlockDecision.Allowed || call is null)
        {
            log.LogWarning("Öffnen abgelehnt für {User}: {Decision} (Ruf {Call})", user, decision, callId);
            return new UnlockResultMessage(callId, false, decision switch
            {
                UnlockDecision.CallEnded => "Der Ruf ist bereits beendet",
                UnlockDecision.NotAllowedByAccess => "Access erlaubt hier kein Öffnen",
                UnlockDecision.AlreadyRequested => "Wird bereits geöffnet",
                _ => "Kein laufender Ruf",
            });
        }

        var actorName = UntrustedText.Clean($"{user} via SI EntryDesk ({Environment.MachineName})", 100);
        log.LogInformation("Öffnen angefordert: {Door} von {User}", call.DoorName, user);
        try
        {
            var result = await api.UnlockAsync(call.DoorId, ActorId(user), actorName, ct).ConfigureAwait(false);
            tracker.CompleteUnlock(callId, result.Success);
            if (result.Success)
            {
                log.LogInformation("Öffnen bestätigt: {Door} von {User}", call.DoorName, user);
                return new UnlockResultMessage(callId, true, "Tür wird geöffnet");
            }
            log.LogError("Öffnen fehlgeschlagen: HTTP {Status} {Code} {Message}", result.HttpStatus, result.Code, result.Message);
            return new UnlockResultMessage(callId, false,
                result.HttpStatus is 401 or 403 ? "Keine Berechtigung zum Öffnen" : "Öffnen fehlgeschlagen");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            tracker.CompleteUnlock(callId, false);
            log.LogError("Öffnen fehlgeschlagen: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return new UnlockResultMessage(callId, false, "Keine Verbindung zur Konsole");
        }
    }

    /// <summary>Feste UUID je PC und Benutzer, damit dieselbe Person im Access-Protokoll gleich erscheint.</summary>
    private static string ActorId(string user)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"SIEntryDesk|{Environment.MachineName}|{user}".ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}

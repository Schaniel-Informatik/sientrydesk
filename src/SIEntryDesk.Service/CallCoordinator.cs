using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Video;

namespace SIEntryDesk.Service;

/// <summary>
/// Verbindet Access-Ereignisse, Rufzustand und die Tray-Apps. Öffnet nur nach den Regeln des CallTracker
/// und gibt Livebilder nur während eines laufenden Rufs frei.
/// </summary>
internal sealed class CallCoordinator(
    IOptions<EntryDeskOptions> options,
    ServiceState state,
    PipeServer pipes,
    TimeProvider time,
    ILogger<CallCoordinator> log) : BackgroundService
{
    /// <summary>So lange nach dem Rufende läuft das Bild noch, damit man sieht, wer hereinkommt.</summary>
    private static readonly TimeSpan VideoGraceAfterEnd = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan VideoMaxLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StreamCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (DateTimeOffset Fetched, IReadOnlyDictionary<string, StreamSource> Streams)> _streamCache =
        new(StringComparer.Ordinal);

    private CallTracker? _tracker;
    private AccessApiClient? _accessApi;
    private ProtectApiClient? _protectApi;
    private StreamProxy? _proxy;
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _stopping = ct;
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
        _tracker = new CallTracker(time, opt.AcceptsDoor);
        state.Tracker = _tracker;
        state.Set(false, "Noch keine Verbindung zu Access");
        _accessApi = new AccessApiClient(host, secrets.AccessUnlockToken ?? secrets.AccessToken, pin);
        SetUpVideo(opt, secrets, host);
        pipes.RequestHandler = HandleRequestAsync;

        log.LogInformation("Start: Konsole {Host}, Türen {Doors}, eigener Öffnen-Token {Separate}, Livebild {Video}",
            host, opt.Doors.Count == 0 ? "alle" : string.Join(", ", opt.Doors), secrets.AccessUnlockToken is not null,
            state.VideoEnabled ? "ein" : "aus");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
            var expiry = ExpireLoopAsync(timer, _tracker, ct);
            var stream = new AccessEventStream(host, secrets.AccessToken, pin, log);
            await stream.RunAsync(
                (ev, _) =>
                {
                    Publish(_tracker.Apply(ev));
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
        finally
        {
            if (_proxy is not null)
                await _proxy.DisposeAsync().ConfigureAwait(false);
            _protectApi?.Dispose();
            _accessApi.Dispose();
        }
    }

    private void SetUpVideo(EntryDeskOptions opt, Secrets secrets, string host)
    {
        if (!opt.VideoConfigured || secrets.ProtectKey is null)
        {
            log.LogInformation("Livebild aus: ProtectPin oder Protect-Schlüssel fehlt");
            return;
        }
        if (opt.ValidateVideo() is { } problem)
        {
            log.LogError("Livebild aus: {Problem}", problem);
            return;
        }
        _protectApi = new ProtectApiClient(host, secrets.ProtectKey, CertificatePin.Parse(opt.ProtectPin));
        _proxy = new StreamProxy(CertificatePin.Parse(opt.EffectiveStreamPin), log, time);
        _proxy.Start();
        state.VideoEnabled = true;
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
                    pipes.Broadcast(state.ToMessage(s.Call));
                    break;
                case CallEnded e:
                    log.LogInformation("Ruf beendet: {Door}, {Reason} (Ruf {Call})", e.Call.DoorName, e.Reason, e.Call.CallId);
                    pipes.Broadcast(new CallEndedMessage(e.Call.CallId, e.Reason));
                    CloseVideoLater(e.Call.CallId);
                    break;
                case CallDoorOpened o:
                    log.LogInformation("Tür geöffnet: {Door} von {By} (Ruf {Call})", o.Call.DoorName, o.OpenedBy ?? "?", o.Call.CallId);
                    pipes.Broadcast(new DoorOpenedMessage(o.Call.CallId, o.OpenedBy));
                    break;
            }
        }
    }

    private async Task<IpcMessage?> HandleRequestAsync(IpcMessage request, string user, CancellationToken ct) => request switch
    {
        UnlockRequest unlock => await UnlockAsync(unlock.CallId, user, ct).ConfigureAwait(false),
        VideoRequest video => await VideoAsync(video.CallId, user, ct).ConfigureAwait(false),
        _ => null,
    };

    private async Task<UnlockResultMessage> UnlockAsync(string callId, string user, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(callId) || _tracker is null || _accessApi is null)
            return new UnlockResultMessage(string.Empty, false, "Ungültige Anfrage");

        var decision = _tracker.TryBeginUnlock(callId, out var call);
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
            var result = await _accessApi.UnlockAsync(call.DoorId, ActorId(user), actorName, ct).ConfigureAwait(false);
            _tracker.CompleteUnlock(callId, result.Success);
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
            _tracker.CompleteUnlock(callId, false);
            log.LogError("Öffnen fehlgeschlagen: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return new UnlockResultMessage(callId, false, "Keine Verbindung zur Konsole");
        }
    }

    /// <summary>Einmal-Adresse für das Livebild, nur für einen laufenden Ruf mit bekannter Kamera.</summary>
    private async Task<IpcMessage> VideoAsync(string callId, string user, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(callId))
            return new VideoUnavailableMessage(string.Empty, "Ungültige Anfrage");
        if (_protectApi is null || _proxy is null)
            return new VideoUnavailableMessage(callId, "Livebild ist nicht eingerichtet");
        var call = _tracker?.FindActive(callId);
        if (call is null)
            return new VideoUnavailableMessage(callId, "Kein laufender Ruf");
        if (call.CameraId.Length == 0)
            return new VideoUnavailableMessage(callId, "Zu dieser Tür ist keine Kamera bekannt");

        try
        {
            var streams = await GetStreamsAsync(call.CameraId, ct).ConfigureAwait(false);
            var source = options.Value.StreamQualities
                .Select(q => streams.GetValueOrDefault(q.Trim()))
                .FirstOrDefault(s => s is not null);
            if (source is null)
            {
                log.LogWarning("Livebild: Für die Kamera von {Door} ist in Protect kein RTSPS-Stream freigegeben", call.DoorName);
                return new VideoUnavailableMessage(callId, "Livebild in Protect nicht freigegeben");
            }
            var url = _proxy.Open(callId, source, VideoMaxLifetime);
            log.LogInformation("Livebild für {User}: {Door}", user, call.DoorName);
            return new VideoReadyMessage(callId, url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            log.LogError("Livebild nicht verfügbar: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return new VideoUnavailableMessage(callId, ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                ? "Protect-Schlüssel abgelehnt"
                : "Protect nicht erreichbar");
        }
    }

    private async Task<IReadOnlyDictionary<string, StreamSource>> GetStreamsAsync(string cameraId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_streamCache.TryGetValue(cameraId, out var cached) && now - cached.Fetched < StreamCacheLifetime)
            return cached.Streams;
        var streams = await _protectApi!.GetStreamsAsync(cameraId, ct).ConfigureAwait(false);
        _streamCache[cameraId] = (now, streams);
        return streams;
    }

    private void CloseVideoLater(string callId)
    {
        if (_proxy is not { } proxy)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(VideoGraceAfterEnd, time, _stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            proxy.Close(callId);
        });
    }

    /// <summary>Feste UUID je PC und Benutzer, damit dieselbe Person im Access-Protokoll gleich erscheint.</summary>
    private static string ActorId(string user)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"SIEntryDesk|{Environment.MachineName}|{user}".ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}

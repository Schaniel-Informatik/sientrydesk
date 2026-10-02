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
/// Verbindet Access-Ereignisse, Rufzustand und die Tray-Apps. Öffnet nur nach den Regeln des CallTracker,
/// gibt Livebilder nur während eines laufenden Rufs frei und Gegensprechen nur während des Rufs und kurz danach.
/// </summary>
internal sealed class CallCoordinator(
    IOptions<EntryDeskOptions> options,
    ServiceState state,
    PipeServer pipes,
    TimeProvider time,
    ILogger<CallCoordinator> log) : BackgroundService
{
    /// <summary>So lange nach dem Rufende läuft das Bild noch, damit man sieht, wer hereinkommt.</summary>
    private static readonly TimeSpan VideoGraceAfterEnd = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan VideoMaxLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StreamCacheLifetime = TimeSpan.FromMinutes(5);
    /// <summary>Kurze Unterbrüche lösen keinen Statuswechsel aus, erst wenn sie länger dauern.</summary>
    private static readonly TimeSpan LinkDebounce = TimeSpan.FromSeconds(10);
    /// <summary>So oft werden Protect (Schlüssel, Kameranamen) und die Ablaufdaten geprüft.</summary>
    private static readonly TimeSpan HealthInterval = TimeSpan.FromMinutes(15);

    private readonly object _linkGate = new();
    private CancellationTokenSource? _pendingDegrade;
    private (LinkHealth Health, string Problem) _pendingLink;
    private string? _videoProblem;

    private readonly ConcurrentDictionary<string, (DateTimeOffset Fetched, IReadOnlyDictionary<string, StreamSource> Streams)> _streamCache =
        new(StringComparer.Ordinal);

    private CallTracker? _tracker;
    private DoorDirectory? _doors;
    private AccessApiClient? _accessApi;
    private ProtectApiClient? _protectApi;
    private StreamProxy? _proxy;
    private TalkRelay? _talk;
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
        _accessApi = new AccessApiClient(host, secrets.AccessToken, pin);
        SetUpVideo(opt, secrets, host);
        SetUpTalk(opt);
        _doors = new DoorDirectory(ServicePaths.DoorsFile);
        foreach (var (doorId, cameraId) in opt.DoorCameras)
            _doors.Seed(doorId, cameraId);
        PublishDoors();
        pipes.RequestHandler = HandleRequestAsync;

        log.LogInformation(
            "Start {Version}: Konsole {Host}, Türen {Doors}, Livebild {Video}, ohne Klingeln {LiveView}, Gegensprechen {Talk}",
            ServiceState.Version, host, opt.Doors.Count == 0 ? "alle" : string.Join(", ", opt.Doors),
            state.VideoEnabled ? "ein" : "aus", state.Doors.Enabled ? "ein" : "aus", state.TalkEnabled ? "ein" : "aus");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
            var expiry = ExpireLoopAsync(timer, _tracker, ct);
            var health = HealthLoopAsync(ct);
            var stream = new AccessEventStream(host, secrets.AccessToken, pin, log);
            await stream.RunAsync(
                (ev, _) =>
                {
                    Publish(_tracker.Apply(ev));
                    return ValueTask.CompletedTask;
                },
                OnLinkState,
                ct).ConfigureAwait(false);
            await expiry.ConfigureAwait(false);
            await health.ConfigureAwait(false);
        }
        finally
        {
            _talk?.Dispose();
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

    private void SetUpTalk(EntryDeskOptions opt)
    {
        if (!opt.Talkback)
        {
            log.LogInformation("Gegensprechen auf diesem PC abgeschaltet");
            return;
        }
        if (_protectApi is null)
        {
            log.LogInformation("Gegensprechen aus: Protect-Schlüssel oder ProtectPin fehlt");
            return;
        }
        _talk = new TalkRelay(time, log, pipes.Send);
        pipes.ClientDisconnected += _talk.EndClient;
        state.TalkEnabled = true;
    }

    private void Fail(string problem)
    {
        log.LogError("{Problem}", problem);
        state.SetLink(LinkHealth.Error, problem);
        pipes.Broadcast(state.Status());
    }

    /// <summary>
    /// Übersetzt den Zustand der Access-Verbindung für die Anzeige. Verbunden gilt sofort, ein Wechsel weg von
    /// „bereit“ erst nach <see cref="LinkDebounce"/>, damit kurze Wiederverbindungen nicht als Störung erscheinen.
    /// </summary>
    private void OnLinkState(AccessLinkState link)
    {
        lock (_linkGate)
        {
            if (link == AccessLinkState.Connected)
            {
                _pendingDegrade?.Cancel();
                _pendingDegrade = null;
                ApplyLink(LinkHealth.Ready, string.Empty);
                return;
            }

            _pendingLink = link switch
            {
                AccessLinkState.Unreachable => (LinkHealth.Unreachable, "Konsole nicht erreichbar (nicht im Firmennetz?)"),
                AccessLinkState.Rejected => (LinkHealth.Error, "Access lehnt den Token ab, dieser PC klingelt nicht"),
                AccessLinkState.CertificateMismatch => (LinkHealth.Error, "Zertifikat der Konsole passt nicht zum Pin, dieser PC klingelt nicht"),
                _ => (LinkHealth.Error, "Verbindung zu Access gestört, dieser PC klingelt nicht"),
            };
            if (state.Health != LinkHealth.Ready)
            {
                ApplyLink(_pendingLink.Health, _pendingLink.Problem);
                return;
            }
            if (_pendingDegrade is not null)
                return;

            var cts = _pendingDegrade = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(LinkDebounce, time, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                lock (_linkGate)
                {
                    if (_pendingDegrade != cts)
                        return;
                    _pendingDegrade = null;
                    ApplyLink(_pendingLink.Health, _pendingLink.Problem);
                }
            });
        }
    }

    private void ApplyLink(LinkHealth health, string problem)
    {
        state.SetLink(health, problem);
        pipes.Broadcast(state.Status());
    }

    /// <summary>Prüft Protect und die Ablaufdaten beim Start und danach regelmässig, holt dabei die Kameranamen.</summary>
    private async Task HealthLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HealthInterval, time);
        do
        {
            await RefreshProtectAsync(ct).ConfigureAwait(false);
            var expiry = options.Value.ExpiryWarning(time.GetLocalNow().DateTime) ?? string.Empty;
            if (state.SetWarning(_videoProblem ?? string.Empty, expiry))
            {
                log.LogInformation("Hinweis: {Warning}", state.Status().Warning is { Length: > 0 } w ? w : "keiner");
                pipes.Broadcast(state.Status());
            }
        }
        while (await WaitAsync(timer, ct).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RefreshProtectAsync(CancellationToken ct)
    {
        if (_protectApi is null)
            return;
        try
        {
            var names = await _protectApi.GetCameraNamesAsync(ct).ConfigureAwait(false);
            var changed = names.Count != state.CameraNames.Count ||
                          names.Any(n => !state.CameraNames.TryGetValue(n.Key, out var old) || old != n.Value);
            foreach (var (id, name) in names)
                state.CameraNames[id] = name;
            _videoProblem = null;
            if (changed)
                PublishDoors();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            if (ct.IsCancellationRequested)
                return;
            _videoProblem = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? "Livebild gestört, Protect-Schlüssel abgelehnt"
                : "Livebild gestört, Protect nicht erreichbar";
            log.LogWarning("{Problem}: {Type}: {Message}", _videoProblem, ex.GetType().Name, ex.Message);
        }
    }

    private async Task ExpireLoopAsync(PeriodicTimer timer, CallTracker tracker, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Publish(tracker.Expire());
                _talk?.Expire();
            }
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
                    if (_doors?.Learn(s.Call) == true)
                    {
                        log.LogInformation("Kamera der Tür {Door} gelernt, Livebild ohne Klingeln verfügbar", s.Call.DoorName);
                        PublishDoors();
                    }
                    break;
                case CallEnded e:
                    log.LogInformation("Ruf beendet: {Door}, {Reason} (Ruf {Call})", e.Call.DoorName, e.Reason, e.Call.CallId);
                    pipes.Broadcast(new CallEndedMessage(e.Call.CallId, e.Reason));
                    CloseVideoLater(e.Call.CallId);
                    EndTalk(e.Call.CallId, e.Reason);
                    break;
                case CallDoorOpened o:
                    log.LogInformation("Tür geöffnet: {Door} von {By} (Ruf {Call})", o.Call.DoorName, o.OpenedBy ?? "?", o.Call.CallId);
                    pipes.Broadcast(new DoorOpenedMessage(o.Call.CallId, o.OpenedBy));
                    break;
            }
        }
    }

    private async Task<IpcMessage?> HandleRequestAsync(IpcMessage request, PipeClient client, CancellationToken ct)
    {
        switch (request)
        {
            case UnlockRequest unlock:
                return await UnlockAsync(unlock.CallId, client.User, ct).ConfigureAwait(false);
            case VideoRequest video:
                return await VideoAsync(video.CallId, client.User, ct).ConfigureAwait(false);
            case LiveViewRequest live:
                return await LiveViewAsync(live.DoorId, client.User, ct).ConfigureAwait(false);
            case TalkRequest { Start: true } talk:
                return await TalkAsync(talk.CallId, client, ct).ConfigureAwait(false);
            case TalkRequest talk:
                if (UntrustedText.IsSafeId(talk.CallId))
                    _talk?.Stop(talk.CallId, client.Id);
                return null;
            case TalkAudioMessage audio:
                if (UntrustedText.IsSafeId(audio.CallId))
                    _talk?.Audio(audio.CallId, client.Id, audio.Opus);
                return null;
            default:
                return null;
        }
    }

    /// <summary>Sprechtaste gedrückt: nur während des Rufs bzw. der Nachfrist, nur mit bekannter Kamera.</summary>
    private async Task<TalkResultMessage> TalkAsync(string callId, PipeClient client, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(callId) || _tracker is null)
            return new TalkResultMessage(string.Empty, false, "Ungültige Anfrage");
        if (_talk is null || _protectApi is null)
            return new TalkResultMessage(callId, false, options.Value.Talkback
                ? "Gegensprechen braucht den Protect-Schlüssel"
                : "Gegensprechen ist auf diesem PC abgeschaltet");
        if (_tracker.CanTalk(callId, out var call) != TalkDecision.Allowed || call is null)
            return new TalkResultMessage(callId, false, "Der Ruf ist beendet");
        if (call.CameraId.Length == 0)
            return new TalkResultMessage(callId, false, "Zu dieser Tür ist keine Kamera bekannt");
        return await _talk.StartAsync(call, call.CameraId, state.DisplayName(call.CameraId, call.DoorName),
            client.Id, client.User, _protectApi, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gegensprechen endet mit dem Ruf: sofort nach „anderswo angenommen“ oder „abgelehnt“, sonst nach der Nachfrist,
    /// in der das Fenster noch offen ist.
    /// </summary>
    private void EndTalk(string callId, CallEndReason reason)
    {
        if (_talk is not { } talk || _tracker is null)
            return;
        if (reason is not (CallEndReason.Cancelled or CallEndReason.Timeout or CallEndReason.Opened))
        {
            talk.EndCall(callId, CallEndReasons.ToGerman(reason));
            return;
        }
        var grace = _tracker.UnlockGraceAfterEnd;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(grace, time, _stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            talk.EndCall(callId, "Ruf beendet");
        });
    }

    /// <summary>Türliste für das Livebild ohne Klingeln, nur Türen, die dieser PC anzeigt.</summary>
    private void PublishDoors()
    {
        var opt = options.Value;
        var enabled = opt.LiveView && state.VideoEnabled;
        var doors = enabled && _doors is not null
            ? _doors.All()
                .Where(d => opt.AcceptsDoor(d.DoorId, d.DoorName))
                .Select(d => new DoorEntry(d.DoorId, state.DisplayName(d.CameraId, d.DoorName.Length > 0 ? d.DoorName : "Tür")))
                .ToArray()
            : [];
        state.Doors = new DoorsMessage(enabled, doors);
        pipes.Broadcast(state.Doors);
    }

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
                UnlockDecision.AlreadyOpened => "Die Tür wurde bereits geöffnet",
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
        var call = _tracker?.FindActive(callId);
        if (call is null)
            return new VideoUnavailableMessage(callId, "Kein laufender Ruf");
        if (call.CameraId.Length == 0)
            return new VideoUnavailableMessage(callId, "Zu dieser Tür ist keine Kamera bekannt");

        var (url, problem) = await OpenStreamAsync(call.CameraId, call.DoorName, callId, VideoMaxLifetime, ct).ConfigureAwait(false);
        if (url is null)
            return new VideoUnavailableMessage(callId, problem!);
        log.LogInformation("Livebild beim Klingeln für {User}: {Door}", user, call.DoorName);
        return new VideoReadyMessage(callId, url);
    }

    /// <summary>Livebild ohne Klingeln, auf diesem PC abschaltbar. Jeder Abruf wird mit Benutzer protokolliert.</summary>
    private async Task<IpcMessage> LiveViewAsync(string doorId, string user, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(doorId))
            return new LiveViewUnavailableMessage(string.Empty, "Ungültige Anfrage");
        var opt = options.Value;
        if (!opt.LiveView)
            return new LiveViewUnavailableMessage(doorId, "Livebild ohne Klingeln ist auf diesem PC abgeschaltet");
        var door = _doors?.Find(doorId);
        if (door is null || !opt.AcceptsDoor(door.DoorId, door.DoorName))
            return new LiveViewUnavailableMessage(doorId, "Tür unbekannt");

        var until = time.GetUtcNow() + opt.LiveViewLifetime;
        var (url, problem) = await OpenStreamAsync(
            door.CameraId, door.DoorName, $"live:{doorId}:{Guid.NewGuid():N}", opt.LiveViewLifetime, ct).ConfigureAwait(false);
        if (url is null)
            return new LiveViewUnavailableMessage(doorId, problem!);
        log.LogInformation("Livebild ohne Klingeln: {Door} von {User} bis {Until:HH:mm:ss}", door.DoorName, user, until.ToLocalTime());
        return new LiveViewReadyMessage(doorId, state.DisplayName(door.CameraId, door.DoorName), url, until);
    }

    /// <summary>Holt die Stream-Adresse über Protect und öffnet eine Einmal-Adresse im Proxy.</summary>
    private async Task<(string? Url, string? Problem)> OpenStreamAsync(
        string cameraId, string doorName, string key, TimeSpan lifetime, CancellationToken ct)
    {
        if (_protectApi is null || _proxy is null)
            return (null, "Livebild ist nicht eingerichtet");
        try
        {
            var streams = await GetStreamsAsync(cameraId, ct).ConfigureAwait(false);
            var source = options.Value.StreamQualities
                .Select(q => streams.GetValueOrDefault(q.Trim()))
                .FirstOrDefault(s => s is not null);
            if (source is null)
            {
                log.LogWarning("Livebild: Für die Kamera von {Door} ist in Protect kein RTSPS-Stream freigegeben", doorName);
                return (null, "Livebild in Protect nicht freigegeben");
            }
            return (_proxy.Open(key, source, lifetime), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            log.LogError("Livebild nicht verfügbar: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return (null, ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
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

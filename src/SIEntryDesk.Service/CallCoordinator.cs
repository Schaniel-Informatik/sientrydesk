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
using SIEntryDesk.Core.Peers;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Video;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Service;

/// <summary>
/// Connects Access events, call state and the tray apps. Unlocks only according to the rules of the CallTracker, grants
/// live views only during a running call and talkback only during the call and shortly after.
/// </summary>
internal sealed class CallCoordinator(
    IOptions<EntryDeskOptions> options,
    ServiceState state,
    PipeServer pipes,
    TimeProvider time,
    ILogger<CallCoordinator> log) : BackgroundService
{
    /// <summary>For this long after the end of the call the video keeps running, so that one sees who comes
    /// in.</summary>
    private static readonly TimeSpan VideoGraceAfterEnd = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan VideoMaxLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StreamCacheLifetime = TimeSpan.FromMinutes(5);
    /// <summary>Short interruptions do not trigger a status change, only longer ones do.</summary>
    private static readonly TimeSpan LinkDebounce = TimeSpan.FromSeconds(10);
    /// <summary>How often Protect (key, camera names) and the expiry dates are checked.</summary>
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
    private PeerLink? _peers;
    /// <summary>Calls this PC has announced, per user: only the first talk turn is announced.</summary>
    private readonly ConcurrentDictionary<(string CallId, string User), byte> _announced = new();
    private Diagnostics? _diagnostics;
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _stopping = ct;
        // The check (--check) must answer even if the start fails because of configuration or credentials.
        _diagnostics = new Diagnostics(options, state, () => _doors, log);
        pipes.RequestHandler = HandleRequestAsync;
        var opt = options.Value;
        if (opt.Validate() is { } problem)
        {
            Fail(Tr.User($"Konfiguration unvollständig: {problem}", $"Configuration incomplete: {problem}",
                $"Niekompletna konfiguracja: {problem}"));
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
            Fail(Tr.User($"Tokens fehlen oder sind nicht lesbar ({ex.GetType().Name})",
                $"Tokens missing or not readable ({ex.GetType().Name})", $"Brak tokenów lub są nieczytelne ({ex.GetType().Name})"));
            return;
        }

        var host = opt.Host.Trim();
        var pin = CertificatePin.Parse(opt.AccessPin);
        _tracker = new CallTracker(time, opt.AcceptsDoor);
        state.Tracker = _tracker;
        _accessApi = new AccessApiClient(host, secrets.AccessToken, pin);
        SetUpVideo(opt, secrets, host);
        SetUpTalk(opt);
        _peers = new PeerLink(PeerCodec.DeriveKey(secrets.AccessToken), time, log);
        state.PeerProblem = () => _peers?.Problem;
        _doors = new DoorDirectory(ServicePaths.DoorsFile);
        foreach (var (doorId, cameraId) in opt.DoorCameras)
            _doors.Seed(doorId, cameraId);
        PublishDoors();

        log.LogInformation(
            Tr.Admin("Start {Version}: Konsole {Host}, Türen {Doors}, Livebild {Video}, ohne Klingeln {LiveView}, Gegensprechen {Talk}, Sprache {Language}",
                "Start {Version}: console {Host}, doors {Doors}, live view {Video}, without ringing {LiveView}, talkback {Talk}, language {Language}"),
            ServiceState.Version, host, opt.Doors.Count == 0 ? Tr.Admin("alle", "all") : string.Join(", ", opt.Doors),
            OnOff(state.VideoEnabled), OnOff(state.Doors.Enabled), OnOff(state.TalkEnabled), Tr.Code(Tr.Language));
        if (opt.ValidateLanguage() is { } languageProblem)
            log.LogWarning("{Problem}", languageProblem);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
            var expiry = ExpireLoopAsync(timer, _tracker, ct);
            var health = HealthLoopAsync(ct);
            var peers = _peers.RunAsync(OnPeerAnnouncement, ct);
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
            await peers.ConfigureAwait(false);
        }
        finally
        {
            _peers.Dispose();
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
            log.LogInformation(Tr.Admin("Livebild aus: ProtectPin oder Protect-Schlüssel fehlt", "Live view off: ProtectPin or Protect key missing"));
            return;
        }
        if (opt.ValidateVideo() is { } problem)
        {
            log.LogError(Tr.Admin("Livebild aus: {Problem}", "Live view off: {Problem}"), problem);
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
            log.LogInformation(Tr.Admin("Gegensprechen auf diesem PC abgeschaltet", "Talkback switched off on this PC"));
            return;
        }
        if (_protectApi is null)
        {
            log.LogInformation(Tr.Admin("Gegensprechen aus: Protect-Schlüssel oder ProtectPin fehlt", "Talkback off: Protect key or ProtectPin missing"));
            return;
        }
        _talk = new TalkRelay(time, log, pipes.Send);
        pipes.ClientDisconnected += _talk.EndClient;
        state.TalkEnabled = true;
    }

    private static string OnOff(bool on) => on ? Tr.Admin("ein", "on") : Tr.Admin("aus", "off");

    private void Fail(string problem)
    {
        log.LogError("{Problem}", problem);
        state.SetLink(LinkHealth.Error, problem);
        pipes.Broadcast(state.Status());
    }

    /// <summary>
    /// Translates the state of the Access connection for the display. Connected applies immediately, a change away from
    /// "ready" only after <see cref="LinkDebounce"/>, so that short reconnects do not appear as a disturbance.
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
                AccessLinkState.Unreachable => (LinkHealth.Unreachable, Tr.User("Konsole nicht erreichbar (nicht im Firmennetz?)",
                    "Console not reachable (outside the company network?)", "Konsola nieosiągalna (poza siecią firmową?)")),
                AccessLinkState.Rejected => (LinkHealth.Error, Tr.User("Access lehnt den Token ab, dieser PC klingelt nicht",
                    "Access rejects the token, this PC does not ring", "Access odrzuca token, ten komputer nie dzwoni")),
                AccessLinkState.CertificateMismatch => (LinkHealth.Error, Tr.User("Zertifikat der Konsole passt nicht zum Pin, dieser PC klingelt nicht",
                    "Certificate of the console does not match the pin, this PC does not ring",
                    "Certyfikat konsoli nie pasuje do pinu, ten komputer nie dzwoni")),
                _ => (LinkHealth.Error, Tr.User("Verbindung zu Access gestört, dieser PC klingelt nicht",
                    "Connection to Access disturbed, this PC does not ring", "Połączenie z Access zakłócone, ten komputer nie dzwoni")),
            };
            if (state.Health != LinkHealth.Ready)
            {
                ApplyLink(_pendingLink.Health, _pendingLink.Problem);
                return;
            }
            if (_pendingDegrade is not null)
                return;

            var cts = _pendingDegrade = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            // The task owns the linked token source and releases it at the end (review 2026-10-23: the link to
            // _stopping would otherwise stay registered until the service stops). Cancel happens only under
            // _linkGate while the task still waits, never after the release.
            _ = Task.Run(async () =>
            {
                try
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
                }
                finally
                {
                    lock (_linkGate)
                    {
                        if (_pendingDegrade == cts)
                            _pendingDegrade = null;
                    }
                    cts.Dispose();
                }
            });
        }
    }

    private void ApplyLink(LinkHealth health, string problem)
    {
        state.SetLink(health, problem);
        pipes.Broadcast(state.Status());
    }

    /// <summary>Checks Protect and the expiry dates at startup and regularly afterwards, fetching the camera
    /// names.</summary>
    private async Task HealthLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HealthInterval, time);
        do
        {
            await RefreshProtectAsync(ct).ConfigureAwait(false);
            var expiry = options.Value.ExpiryWarning(time.GetLocalNow().DateTime) ?? string.Empty;
            if (state.SetWarning(_videoProblem ?? string.Empty, expiry))
            {
                log.LogInformation(Tr.Admin("Hinweis: {Warning}", "Notice: {Warning}"),
                    state.Status().Warning is { Length: > 0 } w ? w : Tr.Admin("keiner", "none"));
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
            // A typo in DoorCameras should be noticed before someone rings.
            var unknown = options.Value.DoorCameras.Count(d => !names.ContainsKey(d.Value));
            _videoProblem = unknown == 0
                ? null
                : Tr.User($"Livebild: {unknown} Kamera(s) aus DoorCameras gibt es in Protect nicht, sientrydesk.json prüfen",
                    $"Live view: {unknown} camera(s) from DoorCameras do not exist in Protect, check sientrydesk.json",
                    $"Podgląd na żywo: kamery z DoorCameras ({unknown}) nie istnieją w Protect, sprawdź sientrydesk.json");
            if (unknown > 0)
                log.LogWarning(Tr.Admin("Livebild: {Count} Kamera(s) aus DoorCameras gibt es in Protect nicht, sientrydesk.json prüfen",
                    "Live view: {Count} camera(s) from DoorCameras do not exist in Protect, check sientrydesk.json"), unknown);
            if (changed)
                PublishDoors();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            if (ct.IsCancellationRequested)
                return;
            var rejected = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
            _videoProblem = rejected
                ? Tr.User("Livebild gestört, Protect-Schlüssel abgelehnt", "Live view disturbed, Protect key rejected",
                    "Podgląd na żywo zakłócony, klucz Protect odrzucony")
                : Tr.User("Livebild gestört, Protect nicht erreichbar", "Live view disturbed, Protect not reachable",
                    "Podgląd na żywo zakłócony, Protect nieosiągalny");
            log.LogWarning(rejected
                    ? Tr.Admin("Livebild gestört, Protect-Schlüssel abgelehnt: {Type}: {Message}", "Live view disturbed, Protect key rejected: {Type}: {Message}")
                    : Tr.Admin("Livebild gestört, Protect nicht erreichbar: {Type}: {Message}", "Live view disturbed, Protect not reachable: {Type}: {Message}"),
                ex.GetType().Name, ex.Message);
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
                // Answers are kept as long as talking is possible (call and grace period).
                bool Over(string callId) => tracker.CanTalk(callId, out _) != TalkDecision.Allowed;
                state.Answers.RemoveWhere(Over);
                foreach (var key in _announced.Keys.Where(k => Over(k.CallId)))
                    _announced.TryRemove(key, out _);
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
                    log.LogInformation(Tr.Admin("Klingeln: {Door} (Ruf {Call})", "Ring: {Door} (call {Call})"), s.Call.DoorName, s.Call.CallId);
                    pipes.Broadcast(state.ToMessage(s.Call));
                    if (_doors?.Learn(s.Call) == true)
                    {
                        log.LogInformation(Tr.Admin("Kamera der Tür {Door} gelernt, Livebild ohne Klingeln verfügbar",
                            "Camera of door {Door} learned, live view without ringing available"), s.Call.DoorName);
                        PublishDoors();
                    }
                    break;
                case CallEnded e:
                    log.LogInformation(Tr.Admin("Ruf beendet: {Door}, {Reason} (Ruf {Call})", "Call ended: {Door}, {Reason} (call {Call})"),
                        e.Call.DoorName, e.Reason, e.Call.CallId);
                    pipes.Broadcast(new CallEndedMessage(e.Call.CallId, e.Reason));
                    CloseVideoLater(e.Call.CallId);
                    EndTalk(e.Call.CallId, e.Reason);
                    break;
                case CallDoorOpened o:
                    log.LogInformation(Tr.Admin("Tür geöffnet: {Door} von {By} (Ruf {Call})", "Door opened: {Door} by {By} (call {Call})"),
                        o.Call.DoorName, o.OpenedBy ?? "?", o.Call.CallId);
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
            case CheckRequest:
                return _diagnostics is null ? null : await _diagnostics.RunAsync(client.User, ct).ConfigureAwait(false);
            case TalkAudioMessage audio:
                if (UntrustedText.IsSafeId(audio.CallId))
                    _talk?.Audio(audio.CallId, client.Id, audio.Opus);
                return null;
            default:
                return null;
        }
    }

    /// <summary>Talk button pressed: only during the call or the grace period, only with a known camera.</summary>
    private async Task<TalkResultMessage> TalkAsync(string callId, PipeClient client, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(callId) || _tracker is null)
            return new TalkResultMessage(string.Empty, false, InvalidRequest);
        if (_talk is null || _protectApi is null)
            return new TalkResultMessage(callId, false, options.Value.Talkback
                ? Tr.User("Gegensprechen braucht den Protect-Schlüssel", "Talkback needs the Protect key", "Rozmowa wymaga klucza Protect")
                : Tr.User("Gegensprechen ist auf diesem PC abgeschaltet", "Talkback is switched off on this PC",
                    "Rozmowa jest wyłączona na tym komputerze"));
        if (_tracker.CanTalk(callId, out var call) != TalkDecision.Allowed || call is null)
            return new TalkResultMessage(callId, false, Tr.User("Der Ruf ist beendet", "The call has ended", "Połączenie zostało zakończone"));
        if (call.CameraId.Length == 0)
            return new TalkResultMessage(callId, false, NoCamera);
        var result = await _talk.StartAsync(call, call.CameraId, state.DisplayName(call.CameraId, call.DoorName),
            client.Id, client.User, _protectApi, ct).ConfigureAwait(false);
        if (result.Granted)
            Announce(call, client.User);
        return result;
    }

    /// <summary>First talk turn of a user at this call: the call counts as answered, on this PC and on the
    /// others.</summary>
    private void Announce(CallInfo call, string user)
    {
        if (!_announced.TryAdd((call.CallId, user), 0))
            return;
        var answer = PeerAnnouncement.Create(call.CallId, UntrustedText.Clean(TalkRelay.ShortName(user), PeerCodec.MaxNameLength),
            UntrustedText.Clean(Environment.MachineName, PeerCodec.MaxNameLength), time.GetUtcNow());
        OfferAnswer(answer, local: true);
        _peers?.Announce(answer, _stopping);
    }

    /// <summary>From another PC: only for a call this PC knows and while talking is possible.</summary>
    private void OnPeerAnnouncement(PeerAnnouncement answer)
    {
        if (_tracker?.CanTalk(answer.CallId, out _) == TalkDecision.Allowed)
            OfferAnswer(answer, local: false);
    }

    /// <summary>Tells the apps only if this is the first answer known for the call.</summary>
    private void OfferAnswer(PeerAnnouncement answer, bool local)
    {
        if (!state.Answers.Offer(answer))
            return;
        var door = _tracker?.CanTalk(answer.CallId, out var call) == TalkDecision.Allowed ? call!.DoorName : "?";
        log.LogInformation(Tr.Admin("Gespräch angenommen: {Door} von {User} an {Pc}{Where} (Ruf {Call})",
                "Call answered: {Door} by {User} at {Pc}{Where} (call {Call})"),
            door, answer.User, answer.Pc, local ? Tr.Admin(" (dieser PC)", " (this PC)") : string.Empty, answer.CallId);
        pipes.Broadcast(ServiceState.ToMessage(answer));
    }

    /// <summary>
    /// Talkback ends with the call: immediately after "answered elsewhere" or "declined", otherwise after the grace
    /// period during which the window is still open.
    /// </summary>
    private void EndTalk(string callId, CallEndReason reason)
    {
        if (_talk is not { } talk || _tracker is null)
            return;
        if (reason is not (CallEndReason.Cancelled or CallEndReason.Timeout or CallEndReason.Opened))
        {
            talk.EndCall(callId, CallEndReasons.Text(reason));
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
            talk.EndCall(callId, CallEndReasons.CallOver);
        });
    }

    /// <summary>Doors this PC shows: for the live view without ringing (if switched on) and for the per-door ring
    /// setting in the app, which exists even without live view.</summary>
    private void PublishDoors()
    {
        var opt = options.Value;
        var doors = _doors is null
            ? []
            : _doors.All()
                .Where(d => opt.AcceptsDoor(d.DoorId, d.DoorName))
                .Select(d => new DoorEntry(d.DoorId, state.DisplayName(d.CameraId, d.DoorName.Length > 0 ? d.DoorName : Tr.User("Tür", "Door", "Drzwi"))))
                .ToArray();
        state.Doors = new DoorsMessage(opt.LiveView && state.VideoEnabled, doors);
        pipes.Broadcast(state.Doors);
    }

    private async Task<UnlockResultMessage> UnlockAsync(string callId, string user, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(callId) || _tracker is null || _accessApi is null)
            return new UnlockResultMessage(string.Empty, false, InvalidRequest);

        var decision = _tracker.TryBeginUnlock(callId, out var call);
        if (decision != UnlockDecision.Allowed || call is null)
        {
            log.LogWarning(Tr.Admin("Öffnen abgelehnt für {User}: {Decision} (Ruf {Call})", "Unlock refused for {User}: {Decision} (call {Call})"),
                user, decision, callId);
            return new UnlockResultMessage(callId, false, decision switch
            {
                UnlockDecision.CallEnded => Tr.User("Der Ruf ist bereits beendet", "The call has already ended", "Połączenie zostało już zakończone"),
                UnlockDecision.NotAllowedByAccess => Tr.User("Access erlaubt hier kein Öffnen", "Access does not allow opening here",
                    "Access nie pozwala tutaj otworzyć"),
                UnlockDecision.AlreadyRequested => Tr.User("Wird bereits geöffnet", "Already opening", "Trwa otwieranie"),
                UnlockDecision.AlreadyOpened => Tr.User("Die Tür wurde bereits geöffnet", "The door has already been opened",
                    "Drzwi zostały już otwarte"),
                _ => NoCall,
            });
        }

        var actorName = UntrustedText.Clean($"{user} via SI EntryDesk ({Environment.MachineName})", 100);
        log.LogInformation(Tr.Admin("Öffnen angefordert: {Door} von {User}", "Unlock requested: {Door} by {User}"), call.DoorName, user);
        try
        {
            var result = await _accessApi.UnlockAsync(call.DoorId, ActorId(user), actorName, ct).ConfigureAwait(false);
            _tracker.CompleteUnlock(callId, result.Success);
            if (result.Success)
            {
                log.LogInformation(Tr.Admin("Öffnen bestätigt: {Door} von {User}", "Unlock confirmed: {Door} by {User}"), call.DoorName, user);
                return new UnlockResultMessage(callId, true, Tr.User("Tür wird geöffnet", "Door is opening", "Drzwi się otwierają"));
            }
            log.LogError(Tr.Admin("Öffnen fehlgeschlagen: HTTP {Status} {Code} {Message}", "Unlock failed: HTTP {Status} {Code} {Message}"),
                result.HttpStatus, result.Code, result.Message);
            return new UnlockResultMessage(callId, false,
                result.HttpStatus is 401 or 403
                    ? Tr.User("Keine Berechtigung zum Öffnen", "No permission to open", "Brak uprawnień do otwarcia")
                    : Tr.User("Öffnen fehlgeschlagen", "Opening failed", "Otwarcie nie powiodło się"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _tracker.CompleteUnlock(callId, false);
            log.LogError(Tr.Admin("Öffnen fehlgeschlagen: {Type}: {Message}", "Unlock failed: {Type}: {Message}"), ex.GetType().Name, ex.Message);
            return new UnlockResultMessage(callId, false, Tr.User("Keine Verbindung zur Konsole", "No connection to the console",
                "Brak połączenia z konsolą"));
        }
    }

    /// <summary>One-time address for the live view, only for a running call with a known camera.</summary>
    private async Task<IpcMessage> VideoAsync(string callId, string user, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(callId))
            return new VideoUnavailableMessage(string.Empty, InvalidRequest);
        var call = _tracker?.FindActive(callId);
        if (call is null)
            return new VideoUnavailableMessage(callId, NoCall);
        if (call.CameraId.Length == 0)
            return new VideoUnavailableMessage(callId, NoCamera);

        var (url, problem) = await OpenStreamAsync(call.CameraId, call.DoorName, callId, VideoMaxLifetime, ct).ConfigureAwait(false);
        if (url is null)
            return new VideoUnavailableMessage(callId, problem!);
        log.LogInformation(Tr.Admin("Livebild beim Klingeln für {User}: {Door}", "Live view while ringing for {User}: {Door}"), user, call.DoorName);
        return new VideoReadyMessage(callId, url);
    }

    /// <summary>Live view without ringing, can be switched off on this PC. Every request is logged with the
    /// user.</summary>
    private async Task<IpcMessage> LiveViewAsync(string doorId, string user, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(doorId))
            return new LiveViewUnavailableMessage(string.Empty, InvalidRequest);
        var opt = options.Value;
        if (!opt.LiveView)
            return new LiveViewUnavailableMessage(doorId, Tr.User("Livebild ohne Klingeln ist auf diesem PC abgeschaltet",
                "Live view without ringing is switched off on this PC", "Podgląd na żywo bez dzwonka jest wyłączony na tym komputerze"));
        var door = _doors?.Find(doorId);
        if (door is null || !opt.AcceptsDoor(door.DoorId, door.DoorName))
            return new LiveViewUnavailableMessage(doorId, Tr.User("Tür unbekannt", "Unknown door", "Nieznane drzwi"));

        var until = time.GetUtcNow() + opt.LiveViewLifetime;
        var (url, problem) = await OpenStreamAsync(
            door.CameraId, door.DoorName, $"live:{doorId}:{Guid.NewGuid():N}", opt.LiveViewLifetime, ct).ConfigureAwait(false);
        if (url is null)
            return new LiveViewUnavailableMessage(doorId, problem!);
        log.LogInformation(Tr.Admin("Livebild ohne Klingeln: {Door} von {User} bis {Until:HH:mm:ss}", "Live view without ringing: {Door} by {User} until {Until:HH:mm:ss}"),
            door.DoorName, user, until.ToLocalTime());
        return new LiveViewReadyMessage(doorId, state.DisplayName(door.CameraId, door.DoorName), url, until);
    }

    /// <summary>Gets the stream address via Protect and opens a one-time address in the proxy.</summary>
    private async Task<(string? Url, string? Problem)> OpenStreamAsync(
        string cameraId, string doorName, string key, TimeSpan lifetime, CancellationToken ct)
    {
        if (_protectApi is null || _proxy is null)
            return (null, Tr.User("Livebild ist nicht eingerichtet", "Live view is not set up", "Podgląd na żywo nie jest skonfigurowany"));
        try
        {
            var streams = await GetStreamsAsync(cameraId, ct).ConfigureAwait(false);
            var source = options.Value.StreamQualities
                .Select(q => streams.GetValueOrDefault(q.Trim()))
                .FirstOrDefault(s => s is not null);
            if (source is null)
            {
                log.LogWarning(Tr.Admin("Livebild: Für die Kamera von {Door} ist in Protect kein RTSPS-Stream freigegeben",
                    "Live view: no RTSPS stream enabled in Protect for the camera of {Door}"), doorName);
                return (null, Tr.User("Livebild in Protect nicht freigegeben", "Live view not enabled in Protect",
                    "Podgląd na żywo nie jest włączony w Protect"));
            }
            return (_proxy.Open(key, source, lifetime), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            log.LogError(Tr.Admin("Livebild nicht verfügbar: {Type}: {Message}", "Live view not available: {Type}: {Message}"),
                ex.GetType().Name, ex.Message);
            return (null, ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                ? ProtectRejected
                : ProtectUnreachable);
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

    private static string InvalidRequest => Tr.User("Ungültige Anfrage", "Invalid request", "Nieprawidłowe żądanie");

    private static string NoCall => Tr.User("Kein laufender Ruf", "No call in progress", "Brak trwającego połączenia");

    private static string NoCamera => Tr.User("Zu dieser Tür ist keine Kamera bekannt", "No camera is known for this door",
        "Dla tych drzwi nie jest znana żadna kamera");

    internal static string ProtectRejected => Tr.User("Protect-Schlüssel abgelehnt", "Protect key rejected", "Klucz Protect odrzucony");

    internal static string ProtectUnreachable => Tr.User("Protect nicht erreichbar", "Protect not reachable", "Protect nieosiągalny");

    /// <summary>Fixed UUID per PC and user, so that the same person appears the same way in the Access log.</summary>
    private static string ActorId(string user)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"SIEntryDesk|{Environment.MachineName}|{user}".ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}

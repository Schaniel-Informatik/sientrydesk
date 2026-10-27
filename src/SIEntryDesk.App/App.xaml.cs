using System.IO;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.App;

public partial class App : Application
{
    private readonly Dictionary<string, RingWindow> _windows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RingWindow> _liveWindows = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Mutex? _singleInstance;
    private ServiceClient? _client;
    private TrayIcon? _tray;
    private Ringtone? _ringtone;
    private Task<LibVLC?>? _libVlc;

    /// <summary>Waiting time before "this PC does not ring" is reported, so that short disturbances stay
    /// silent.</summary>
    private static readonly TimeSpan AlarmDelay = TimeSpan.FromSeconds(30);
    private const string SettingsKey = @"Software\SIEntryDesk";

    /// <summary>Null until the first connection attempt, so that the icon does not flash red at startup.</summary>
    private ServiceClientStatus? _status;
    private readonly DispatcherTimer _alarmTimer = new() { Interval = AlarmDelay };
    private bool _alarmNotified;

    // The pause applies to this session only, a restart deliberately ends it.
    private readonly DispatcherTimer _pauseTimer = new();
    private DateTimeOffset? _pausedUntil;
    private int _missedCount;
    private DateTimeOffset _missedLast;
    private string _missedDoor = string.Empty;

    private bool _autoSound;
    private ListenMode _listenMode;

    // Ringing per door: this user's setting, doors from the service and from calls of this session, delayed calls.
    private RingPreferences _ringPrefs = null!;
    private IReadOnlyList<DoorEntry> _serviceDoors = [];
    private readonly Dictionary<string, string> _seenDoors = new(StringComparer.Ordinal);
    private sealed record PendingRing(string DoorName, DispatcherTimer Timer, Action Show);
    private readonly Dictionary<string, PendingRing> _pending = new(StringComparer.Ordinal);

    // Talkback: at most one recording at a time, with a safety net for the maximum duration. Granted: the service has
    // the session, the recording is sending. Until then it buffers.
    private sealed class ActiveTalk(TalkSender sender, string callId, RingWindow window)
    {
        public TalkSender Sender { get; } = sender;
        public string CallId { get; } = callId;
        public RingWindow Window { get; } = window;
        public bool Granted { get; set; }
    }
    private ActiveTalk? _talk;
    private readonly DispatcherTimer _talkTimer = new();

    // Reminder before the keys expire: once per day and user, checked hourly (date change).
    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromHours(1) };

    private static readonly string[] HelpArgs = ["--help", "-h", "-?", "/?", "--hilfe"];

    // Lines at most about 45 characters, otherwise the message box wraps them itself and destroys the indentation.
    private static readonly string HelpText =
        $"""
        SI EntryDesk {TrayIcon.AppVersion}

        SIEntryDesk.exe
            Tray-App, startet bei der Anmeldung
            von selbst.

        SIEntryDesk.exe --setup
            Einrichtungsassistent: sientrydesk.json
            erstellen oder eine bestehende prüfen.

        SIEntryDesk.exe --setup <Datei>
            Einrichtungsassistent mit dieser Datei.
            Die installierte Datei ist nur als
            Administrator lesbar.

        SIEntryDesk.exe --check
            Prüft diesen PC und die Verbindung
            zur Anlage, grün/orange/rot.

        SIEntryDesk.exe --help
            Diese Hilfe.

        Befehle des Dienstes:
            SIEntryDesk.Service.exe --help
        """;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => HelpArgs.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            MessageBox.Show(HelpText, "SI EntryDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Check for troubleshooting, independent of a running tray app.
        if (e.Args.Any(a => string.Equals(a, "--check", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new CheckWindow();
            MainWindow.Show();
            return;
        }

        // Setup wizard for admins, independent of a running tray app. Optionally with an existing file.
        var setup = Array.FindIndex(e.Args, a => string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase));
        if (setup >= 0)
        {
            var configPath = setup + 1 < e.Args.Length ? Path.GetFullPath(e.Args[setup + 1]) : null;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _libVlc = Task.Run(LoadLibVlc);
            MainWindow = new SetupWindow(_libVlc, configPath);
            MainWindow.Show();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\SIEntryDesk.App", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        LocalChecks.InTrayApp = true;
        // Load LibVLC in the background so that the first ring does not wait for it.
        _libVlc = Task.Run(LoadLibVlc);
        _ringtone = new Ringtone();
        _autoSound = LoadAutoSound();
        _listenMode = TalkAudio.LoadMode();
        _ringPrefs = new RingPreferences();
        _tray = new TrayIcon(new TrayActions(ShowTestRing, RequestLiveView, Pause, () => Resume(manual: true), SetAutoSound,
            StartCheck, Shutdown, SetRingMode, ShowPendingNow, SetListenMode));
        _tray.SetAutoSound(_autoSound);
        _tray.SetListenMode(_listenMode);
        UpdateRingDoors();
        _alarmTimer.Tick += (_, _) => NotifyAlarm();
        _pauseTimer.Tick += (_, _) => Resume(manual: false);
        _expiryTimer.Tick += (_, _) => CheckExpiryNotice();
        _talkTimer.Tick += (_, _) =>
        {
            if (_talk is not { } talk)
                return;
            StopTalk(talk.CallId, notifyService: true);
            talk.Window.ShowTalkProblem("Sprechen beendet: Höchstdauer erreicht");
        };
        _expiryTimer.Start();
        _client = new ServiceClient(Dispatcher);
        _client.StatusChanged += status =>
        {
            _status = status;
            UpdateTray();
            CheckExpiryNotice();
        };
        _client.MessageReceived += OnMessage;
        _ = _client.RunAsync(_cts.Token);
        UpdateTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopTalk(_talk?.CallId, notifyService: true);
        _cts.Cancel();
        foreach (var window in _windows.Values.Concat(_liveWindows.Values).ToList())
            window.Close();
        _ringtone?.Dispose();
        _tray?.Dispose();
        if (_libVlc is { IsCompletedSuccessfully: true, Result: { } libVlc })
            libVlc.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static LibVLC? LoadLibVlc()
    {
        try
        {
            // Explicit path, because the app is published as a single .exe.
            LibVLCSharp.Shared.Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
            // --rtsp-tcp: the service only accepts RTP over the RTSP connection, UDP attempts only cost time.
            // --verbose=2 only so that the log names the loaded modules; only a few of these lines are written.
            var libVlc = new LibVLC("--rtsp-tcp", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--verbose=2");
            VlcLog.Attach(libVlc);
            return libVlc;
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or IOException)
        {
            return null;
        }
    }

    private CheckWindow? _checkWindow;

    /// <summary>Check from the menu: right in this app, so that the window appears immediately.</summary>
    private void StartCheck()
    {
        if (_checkWindow is not null)
        {
            _checkWindow.Activate();
            return;
        }
        _checkWindow = new CheckWindow();
        _checkWindow.Closed += (_, _) => _checkWindow = null;
        _checkWindow.Show();
        _checkWindow.Activate();
    }

    private void OnMessage(IpcMessage message)
    {
        switch (message)
        {
            case CallStartedMessage call:
                OnCallStarted(call);
                break;
            case DoorsMessage doors:
                _serviceDoors = doors.Doors;
                _tray?.SetDoors(doors.Enabled, doors.Doors);
                UpdateRingDoors();
                break;
            case LiveViewReadyMessage live:
                ShowLiveView(live);
                break;
            case LiveViewUnavailableMessage live:
                _tray?.Notify("Kein Livebild", live.Reason);
                break;
            case CallEndedMessage end when _pending.ContainsKey(end.CallId):
                // Delayed call, answered elsewhere or cancelled in the meantime: nothing appears on this PC.
                CancelPending(end.CallId);
                break;
            case CallEndedMessage end when _windows.TryGetValue(end.CallId, out var window):
                window.EndCall(end.Reason);
                break;
            case DoorOpenedMessage opened when _windows.TryGetValue(opened.CallId, out var window):
                window.ShowOpened(opened.OpenedBy);
                break;
            case UnlockResultMessage result when _windows.TryGetValue(result.CallId, out var window):
                window.ShowUnlockResult(result.Success, result.Message);
                break;
            case VideoReadyMessage video when _windows.TryGetValue(video.CallId, out var window):
                _ = PlayVideoAsync(window, video.Url);
                break;
            case VideoUnavailableMessage video when _windows.TryGetValue(video.CallId, out var window):
                window.ShowVideoProblem(video.Reason);
                break;
            case TalkResultMessage talk when _windows.TryGetValue(talk.CallId, out var window):
                OnTalkResult(window, talk);
                break;
            case TalkEndedMessage ended when _windows.TryGetValue(ended.CallId, out var window):
                StopTalk(ended.CallId, notifyService: false);
                window.ShowTalkProblem($"Sprechen beendet: {ended.Reason}");
                break;
        }
    }

    /// <summary>Ring according to this user's setting for the door: immediately, after 10 s or not at all.</summary>
    private void OnCallStarted(CallStartedMessage call)
    {
        if (call.DoorId.Length > 0 && call.DoorName.Length > 0 && !_seenDoors.ContainsKey(call.DoorId))
        {
            _seenDoors[call.DoorId] = call.DoorName;
            UpdateRingDoors();
        }
        var mode = _ringPrefs.Get(call.DoorId);
        // "Off": show nothing and do not count as missed. The live view stays in the menu.
        if (mode == RingMode.Off || _windows.ContainsKey(call.CallId) || _pending.ContainsKey(call.CallId))
            return;
        if (IsPaused)
        {
            CountMissed(call.DoorName);
            return;
        }
        void Show()
        {
            // The ring window takes precedence over an open live view.
            foreach (var live in _liveWindows.Values.ToList())
                live.Close();
            ShowRing(call.CallId, call.DoorName, call.StartedAt, call.UnlockAllowed, call.VideoAvailable, WindowMode.Ring,
                call.TalkAvailable);
        }
        if (mode == RingMode.Delayed)
            Defer(call.CallId, call.DoorName, Show);
        else
            Show();
    }

    /// <summary>During the pause no window and no sound, only count.</summary>
    private void CountMissed(string doorName)
    {
        _missedCount++;
        _missedLast = DateTimeOffset.Now;
        _missedDoor = doorName;
        UpdateTray();
    }

    /// <summary>Delayed ringing: the icon blinks, window and ringtone after <see cref="RingSettings.Delay"/>.</summary>
    private void Defer(string callId, string doorName, Action show)
    {
        var timer = new DispatcherTimer { Interval = RingSettings.Delay };
        timer.Tick += (_, _) =>
        {
            if (!_pending.ContainsKey(callId))
                return;
            CancelPending(callId);
            if (IsPaused)
                CountMissed(doorName);
            else
                show();
        };
        _pending[callId] = new PendingRing(doorName, timer, show);
        timer.Start();
        UpdatePending();
    }

    private void CancelPending(string callId)
    {
        if (!_pending.Remove(callId, out var pending))
            return;
        pending.Timer.Stop();
        UpdatePending();
    }

    /// <summary>"Jetzt anzeigen" in the menu: show delayed calls immediately.</summary>
    private void ShowPendingNow()
    {
        foreach (var (callId, pending) in _pending.ToList())
        {
            CancelPending(callId);
            pending.Show();
        }
    }

    private void UpdatePending()
    {
        var doors = _pending.Values.Select(p => p.DoorName).Distinct().ToList();
        _tray?.SetPending(doors.Count == 0 ? null : $"Es klingelt: {string.Join(", ", doors)}");
    }

    private void SetRingMode(RingDoor door, RingMode mode)
    {
        _ringPrefs.Set(door.DoorId, door.Name, mode);
        UpdateRingDoors();
    }

    /// <summary>Doors for the ring setting: from the service, from calls of this session and stored
    /// deviations.</summary>
    private void UpdateRingDoors()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (doorId, pref) in _ringPrefs.Deviations)
            names[doorId] = pref.Name;
        foreach (var (doorId, name) in _seenDoors)
            names[doorId] = name;
        foreach (var door in _serviceDoors)
            names[door.DoorId] = door.Name;
        var doors = names
            .Select(d => new RingDoor(d.Key, d.Value.Length > 0 ? d.Value : "Tür", _ringPrefs.Get(d.Key)))
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)
            .ToList();
        _tray?.SetRingDoors(doors);
    }

    /// <summary>Talk button pressed: open the microphone immediately (pre-roll), then request the session from the
    /// service.</summary>
    private async void OnTalkPressed(RingWindow window, string callId)
    {
        // Pressed again during the tail: just keep talking, the session at the service is still running.
        if (_talk is { } running && running.CallId == callId && running.Sender.Resume())
        {
            window.ShowTalking();
            return;
        }
        StopTalk(_talk?.CallId, notifyService: true);
        var sender = TalkSender.Open(
            problem => Dispatcher.InvokeAsync(() =>
            {
                StopTalk(callId, notifyService: true);
                window.ShowTalkProblem(problem);
            }),
            out var openProblem);
        if (sender is null)
        {
            window.ShowTalkProblem(openProblem);
            return;
        }
        _talk = new ActiveTalk(sender, callId, window);
        window.ShowTalking();
        if (_client is null || !await _client.SendAsync(new TalkRequest(callId, true)))
        {
            StopTalk(callId, notifyService: false);
            window.ShowTalkProblem("Dienst nicht erreichbar");
        }
    }

    private void OnTalkResult(RingWindow window, TalkResultMessage result)
    {
        if (_talk is not { } talk || talk.CallId != result.CallId || talk.Granted)
        {
            // Answer to a request that no longer exists: end the session at the service right away.
            if (result.Granted)
                _ = _client?.SendAsync(new TalkRequest(result.CallId, false));
            return;
        }
        if (!result.Granted)
        {
            StopTalk(talk.CallId, notifyService: false);
            window.ShowTalkProblem(result.Message);
            return;
        }
        talk.Granted = true;
        var callId = result.CallId;
        var client = _client;
        talk.Sender.Begin(result.SampleRate,
            packet => client?.SendAsync(new TalkAudioMessage(callId, Convert.ToBase64String(packet))) ?? Task.CompletedTask);
        // The service ends after MaxSeconds anyway, this also stops the microphone.
        _talkTimer.Stop();
        _talkTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, result.MaxSeconds));
        _talkTimer.Start();
    }

    /// <summary>Button released: record the tail, then door audio on again, report the end after the last
    /// packet.</summary>
    private void OnTalkReleased(string callId)
    {
        if (_talk is not { } talk || talk.CallId != callId)
        {
            _ = _client?.SendAsync(new TalkRequest(callId, false));
            return;
        }
        var client = _client;
        talk.Sender.Finish(
            () => Dispatcher.InvokeAsync(() =>
            {
                // Microphone off: no echo possible anymore, the door audio may play again.
                if (_talk == talk)
                    talk.Window.ShowTalkIdle();
            }),
            async () =>
            {
                if (client is not null)
                    await client.SendAsync(new TalkRequest(callId, false)).ConfigureAwait(false);
            },
            () => Dispatcher.InvokeAsync(() =>
            {
                if (_talk != talk)
                    return;
                _talk = null;
                _talkTimer.Stop();
                talk.Sender.Dispose();
                talk.Window.ShowTalkIdle();
            }));
    }

    /// <summary>Ends the recording for this call immediately, without tail, and reports it to the service if
    /// requested.</summary>
    private void StopTalk(string? callId, bool notifyService)
    {
        if (callId is null || _talk is not { } talk || talk.CallId != callId)
            return;
        _talkTimer.Stop();
        talk.Sender.Dispose();
        _talk = null;
        if (notifyService)
            _ = _client?.SendAsync(new TalkRequest(callId, false));
    }

    private void ShowRing(string callId, string doorName, DateTimeOffset startedAt, bool unlockAllowed, bool videoAvailable,
        WindowMode mode, bool talkAvailable = false)
    {
        if (_windows.ContainsKey(callId))
            return;

        var window = new RingWindow(callId, doorName, startedAt, unlockAllowed, mode);
        window.UnlockRequested += async () =>
        {
            if (_client is null || !await _client.SendAsync(new UnlockRequest(callId)))
                window.ShowUnlockResult(false, "Dienst nicht erreichbar");
        };
        if (talkAvailable)
        {
            window.EnableTalk();
            window.TalkPressed += () => OnTalkPressed(window, callId);
            window.TalkReleased += () => OnTalkReleased(callId);
        }
        window.RingingChanged += UpdateRingtone;
        window.Closed += (_, _) =>
        {
            _windows.Remove(callId);
            StopTalk(callId, notifyService: true);
            UpdateRingtone();
        };

        Place(window, _windows.Count);
        _windows[callId] = window;
        window.Show();
        UpdateRingtone();
        _tray?.Notify("Es klingelt", window.DoorText.Text);

        if (videoAvailable)
            _ = RequestVideoAsync(window, callId);
    }

    /// <summary>Centered on the primary screen, further windows slightly offset.</summary>
    private static void Place(Window window, int index)
    {
        var area = SystemParameters.WorkArea;
        var offset = index * 30;
        window.Height = Math.Min(window.Height, area.Height - 20);
        window.Left = area.Left + (area.Width - window.Width) / 2 + offset;
        window.Top = area.Top + (area.Height - window.Height) / 2 + offset;
    }

    private async void RequestLiveView(string doorId)
    {
        if (_liveWindows.TryGetValue(doorId, out var open))
        {
            open.Activate();
            return;
        }
        if (_client is null || !await _client.SendAsync(new LiveViewRequest(doorId)))
            _tray?.Notify("Kein Livebild", "Dienst nicht erreichbar");
    }

    private void ShowLiveView(LiveViewReadyMessage live)
    {
        if (_liveWindows.ContainsKey(live.DoorId) || _windows.Count > 0)
            return;
        var window = new RingWindow("live-" + live.DoorId, live.DoorName, DateTimeOffset.Now, unlockAllowed: false,
            WindowMode.LiveView, live.Until);
        window.Closed += (_, _) => _liveWindows.Remove(live.DoorId);
        Place(window, _liveWindows.Count);
        _liveWindows[live.DoorId] = window;
        window.Show();
        _ = PlayVideoAsync(window, live.Url);
    }

    private async Task RequestVideoAsync(RingWindow window, string callId)
    {
        if (_client is null || !await _client.SendAsync(new VideoRequest(callId)))
            window.ShowVideoProblem("Dienst nicht erreichbar");
    }

    private async Task PlayVideoAsync(RingWindow window, string url)
    {
        var libVlc = _libVlc is null ? null : await _libVlc;
        if (libVlc is null)
        {
            window.ShowVideoProblem("Videoplayer konnte nicht geladen werden");
            return;
        }
        window.SetTalkAudio(TalkAudio.Resolve(_listenMode));
        window.PlayVideo(libVlc, url, _autoSound);
    }

    private bool IsPaused => _pausedUntil is { } until && until > DateTimeOffset.Now;

    private void Pause(TimeSpan duration)
    {
        _pausedUntil = DateTimeOffset.Now + duration;
        _missedCount = 0;
        _pauseTimer.Stop();
        _pauseTimer.Interval = duration;
        _pauseTimer.Start();
        // Whoever pauses in the middle of ringing wants quiet: close open ring windows, discard delayed calls.
        foreach (var window in _windows.Values.Where(w => w.Mode == WindowMode.Ring).ToList())
            window.Close();
        foreach (var callId in _pending.Keys.ToList())
            CancelPending(callId);
        UpdateTray();
    }

    private void Resume(bool manual)
    {
        _pauseTimer.Stop();
        if (_pausedUntil is null)
            return;
        _pausedUntil = null;
        if (_missedCount > 0)
            _tray?.Notify(manual ? "Klingel wieder an" : "Pause beendet, Klingel wieder an", MissedText()!);
        UpdateTray();
    }

    private string? MissedText() => _missedCount == 0
        ? null
        : $"Während der Pause {_missedCount}× geklingelt, zuletzt {_missedLast:HH:mm} {_missedDoor}";

    private void UpdateTray()
    {
        if (_tray is null)
            return;
        var state = TrayStatus.Evaluate(_status?.Connected ?? true, _status?.Service, _pausedUntil, DateTimeOffset.Now);
        _tray.Update(state, _status?.Service?.ServiceVersion);
        _tray.SetTalkEnabled(_status?.Service?.TalkEnabled == true);
        _tray.SetPause(IsPaused ? _pausedUntil : null, MissedText());

        if (!state.Alarm)
        {
            _alarmTimer.Stop();
            _alarmNotified = false;
        }
        else if (!_alarmNotified && !_alarmTimer.IsEnabled)
        {
            _alarmTimer.Start();
        }
    }

    /// <summary>Reports once per disturbance when this PC has not been able to ring for <see
    /// cref="AlarmDelay"/>.</summary>
    private void NotifyAlarm()
    {
        _alarmTimer.Stop();
        var state = TrayStatus.Evaluate(_status?.Connected ?? true, _status?.Service, _pausedUntil, DateTimeOffset.Now);
        if (!state.Alarm || _alarmNotified)
            return;
        _alarmNotified = true;
        _tray?.Notify("SI EntryDesk: dieser PC klingelt nicht", state.Text);
    }

    /// <summary>From 14 days before the keys expire, one notice per day, on every app.</summary>
    private void CheckExpiryNotice()
    {
        var notice = _status?.Service?.ExpiryNotice;
        if (string.IsNullOrEmpty(notice))
            return;
        var today = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (key.GetValue("ExpiryNoticeShown") as string == today)
            return;
        key.SetValue("ExpiryNoticeShown", today);
        _tray?.Notify("SI EntryDesk", $"{notice}. Bitte der IT melden.");
    }

    private static bool LoadAutoSound()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue("AutoDoorSound") is int value && value != 0;
    }

    private void SetListenMode(ListenMode mode)
    {
        _listenMode = mode;
        TalkAudio.SaveMode(mode);
    }

    private void SetAutoSound(bool on)
    {
        _autoSound = on;
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("AutoDoorSound", on ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>Only on this PC, without live view, unlocking disabled. If a door is set to "after 10 s", the test ring
    /// shows the delay with a blinking icon, so that the effect is visible.</summary>
    private void ShowTestRing()
    {
        var callId = "test-" + Guid.NewGuid().ToString("N");
        void Show()
        {
            ShowRing(callId, "Testklingeln", DateTimeOffset.Now, unlockAllowed: false, videoAvailable: false, WindowMode.Test);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_windows.TryGetValue(callId, out var window))
                    window.EndCall(CallEndReason.Timeout);
            };
            timer.Start();
        }
        if (_ringPrefs.Deviations.Values.Any(d => d.Mode == RingMode.Delayed))
            Defer(callId, "Testklingeln", Show);
        else
            Show();
    }

    private void UpdateRingtone()
    {
        if (_windows.Values.Any(w => w.IsRinging))
            _ringtone?.Play();
        else
            _ringtone?.Stop();
    }
}

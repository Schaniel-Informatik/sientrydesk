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

    /// <summary>Wartezeit, bevor ein „dieser PC klingelt nicht“ gemeldet wird, damit kurze Störungen still bleiben.</summary>
    private static readonly TimeSpan AlarmDelay = TimeSpan.FromSeconds(30);
    private const string SettingsKey = @"Software\SIEntryDesk";

    /// <summary>Null bis zum ersten Verbindungsversuch, damit das Symbol beim Start nicht kurz rot aufblitzt.</summary>
    private ServiceClientStatus? _status;
    private readonly DispatcherTimer _alarmTimer = new() { Interval = AlarmDelay };
    private bool _alarmNotified;

    // Pause gilt nur für diese Sitzung, ein Neustart beendet sie bewusst.
    private readonly DispatcherTimer _pauseTimer = new();
    private DateTimeOffset? _pausedUntil;
    private int _missedCount;
    private DateTimeOffset _missedLast;
    private string _missedDoor = string.Empty;

    private bool _autoSound;

    // Gegensprechen: höchstens eine Aufnahme gleichzeitig, mit Sicherheitsnetz für die Höchstdauer.
    private TalkSender? _talkSender;
    private string? _talkCallId;
    private readonly DispatcherTimer _talkTimer = new();

    // Erinnerung vor Ablauf der Schlüssel: einmal pro Tag und Benutzer, stündlich geprüft (Datumswechsel).
    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromHours(1) };

    private static readonly string[] HelpArgs = ["--help", "-h", "-?", "/?", "--hilfe"];

    // Zeilen höchstens etwa 45 Zeichen, sonst bricht das Meldungsfenster selbst um und zerstört die Einrückung.
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

        // Einrichtungsassistent für Admins, unabhängig von einer laufenden Tray-App. Optional mit bestehender Datei.
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

        // LibVLC im Hintergrund laden, damit das erste Klingeln nicht darauf wartet.
        _libVlc = Task.Run(LoadLibVlc);
        _ringtone = new Ringtone();
        _autoSound = LoadAutoSound();
        _tray = new TrayIcon(new TrayActions(ShowTestRing, RequestLiveView, Pause, () => Resume(manual: true), SetAutoSound, Shutdown));
        _tray.SetAutoSound(_autoSound);
        _alarmTimer.Tick += (_, _) => NotifyAlarm();
        _pauseTimer.Tick += (_, _) => Resume(manual: false);
        _expiryTimer.Tick += (_, _) => CheckExpiryNotice();
        _talkTimer.Tick += (_, _) =>
        {
            if (_talkCallId is { } talking && _windows.TryGetValue(talking, out var window))
                window.ReleaseTalk();
            StopTalk(_talkCallId, notifyService: true);
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
        StopTalk(_talkCallId, notifyService: true);
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
            // Pfad ausdrücklich, weil die App als einzelne .exe veröffentlicht wird.
            LibVLCSharp.Shared.Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
            // --rtsp-tcp: Der Dienst nimmt nur RTP über die RTSP-Verbindung an, UDP-Versuche kosten nur Zeit.
            return new LibVLC("--rtsp-tcp", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--quiet");
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or IOException)
        {
            return null;
        }
    }

    private void OnMessage(IpcMessage message)
    {
        switch (message)
        {
            case CallStartedMessage call when IsPaused:
                // Während der Pause kein Fenster und kein Ton, nur mitzählen.
                _missedCount++;
                _missedLast = DateTimeOffset.Now;
                _missedDoor = call.DoorName;
                UpdateTray();
                break;
            case CallStartedMessage call:
                // Das Klingelfenster hat Vorrang vor einem offenen Livebild.
                foreach (var live in _liveWindows.Values.ToList())
                    live.Close();
                ShowRing(call.CallId, call.DoorName, call.StartedAt, call.UnlockAllowed, call.VideoAvailable, WindowMode.Ring,
                    call.TalkAvailable);
                break;
            case DoorsMessage doors:
                _tray?.SetDoors(doors.Enabled, doors.Doors);
                break;
            case LiveViewReadyMessage live:
                ShowLiveView(live);
                break;
            case LiveViewUnavailableMessage live:
                _tray?.Notify("Kein Livebild", live.Reason);
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
                window.ReleaseTalk();
                window.ShowTalkProblem($"Sprechen beendet: {ended.Reason}");
                break;
        }
    }

    private void OnTalkResult(RingWindow window, TalkResultMessage result)
    {
        if (!result.Granted)
        {
            window.ShowTalkProblem(result.Message);
            return;
        }
        // Taste schon wieder losgelassen, bevor die Freigabe kam.
        if (!window.TalkHeld)
        {
            _ = _client?.SendAsync(new TalkRequest(result.CallId, false));
            return;
        }

        StopTalk(_talkCallId, notifyService: true);
        var callId = result.CallId;
        var client = _client;
        var sender = TalkSender.Start(
            result.SampleRate,
            packet => client?.SendAsync(new TalkAudioMessage(callId, Convert.ToBase64String(packet))) ?? Task.CompletedTask,
            problem => Dispatcher.InvokeAsync(() =>
            {
                StopTalk(callId, notifyService: true);
                window.ShowTalkProblem(problem);
            }),
            out var startProblem);
        if (sender is null)
        {
            _ = client?.SendAsync(new TalkRequest(callId, false));
            window.ShowTalkProblem(startProblem);
            return;
        }
        _talkSender = sender;
        _talkCallId = callId;
        window.ShowTalking();
        // Der Dienst beendet nach MaxSeconds ohnehin, das hier stoppt auch das Mikrofon.
        _talkTimer.Stop();
        _talkTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, result.MaxSeconds));
        _talkTimer.Start();
    }

    /// <summary>Beendet die Aufnahme zu diesem Ruf und meldet es auf Wunsch dem Dienst.</summary>
    private void StopTalk(string? callId, bool notifyService)
    {
        if (callId is null || _talkCallId != callId)
            return;
        _talkTimer.Stop();
        _talkSender?.Dispose();
        _talkSender = null;
        _talkCallId = null;
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
            window.TalkPressed += async () =>
            {
                window.ShowTalkConnecting();
                if (_client is null || !await _client.SendAsync(new TalkRequest(callId, true)))
                    window.ShowTalkProblem("Dienst nicht erreichbar");
            };
            window.TalkReleased += () =>
            {
                if (_talkCallId == callId)
                    StopTalk(callId, notifyService: true);
                else
                    _ = _client?.SendAsync(new TalkRequest(callId, false));
            };
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

    /// <summary>Mittig auf dem Hauptbildschirm, weitere Fenster leicht versetzt.</summary>
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
            window.ShowVideoProblem("Videoplayer konnte nicht geladen werden");
        else
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
        // Wer mitten im Klingeln pausiert, will Ruhe: offene Klingelfenster schliessen.
        foreach (var window in _windows.Values.Where(w => w.Mode == WindowMode.Ring).ToList())
            window.Close();
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

    /// <summary>Meldet einmal pro Störung, wenn dieser PC seit <see cref="AlarmDelay"/> nicht klingeln kann.</summary>
    private void NotifyAlarm()
    {
        _alarmTimer.Stop();
        var state = TrayStatus.Evaluate(_status?.Connected ?? true, _status?.Service, _pausedUntil, DateTimeOffset.Now);
        if (!state.Alarm || _alarmNotified)
            return;
        _alarmNotified = true;
        _tray?.Notify("SI EntryDesk: dieser PC klingelt nicht", state.Text);
    }

    /// <summary>Ab 14 Tagen vor Ablauf der Schlüssel einmal pro Tag eine Meldung, auf jeder App.</summary>
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

    private void SetAutoSound(bool on)
    {
        _autoSound = on;
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("AutoDoorSound", on ? 1 : 0, RegistryValueKind.DWord);
    }

    private void ShowTestRing()
    {
        var callId = "test-" + Guid.NewGuid().ToString("N");
        ShowRing(callId, "Testklingeln", DateTimeOffset.Now, unlockAllowed: false, videoAvailable: false, WindowMode.Test);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_windows.TryGetValue(callId, out var window))
                window.EndCall(CallEndReason.Timeout);
        };
        timer.Start();
    }

    private void UpdateRingtone()
    {
        if (_windows.Values.Any(w => w.IsRinging))
            _ringtone?.Play();
        else
            _ringtone?.Stop();
    }
}

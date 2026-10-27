using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.App;

public enum WindowMode
{
    /// <summary>Real doorbell call.</summary>
    Ring,
    /// <summary>Test ring on this PC only, without door and video.</summary>
    Test,
    /// <summary>Live view without ringing, opened by the user.</summary>
    LiveView,
}

/// <summary>
/// Window for a doorbell call. Stays on top of all windows, but does not take the keyboard focus, so that nobody
/// triggers anything while typing.
/// </summary>
public partial class RingWindow : Window
{
    /// <summary>Safety net in case the end of the call never arrives (Access ends after 60 s).</summary>
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(90);
    /// <summary>How long the window stays open after the end, with live view. Same length as the service's grace period
    /// for unlocking after "visitor cancelled" or "nobody answered".</summary>
    private static readonly TimeSpan LingerAfterEnd = TimeSpan.FromSeconds(10);

    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x7A));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x72));

    private readonly DispatcherTimer _closeTimer = new();
    private readonly bool _unlockAllowed;
    private bool _ended;
    private bool _opened;
    private bool _unlockPending;
    /// <summary>Unlocking still allowed after the end (108/105) until the window closes.</summary>
    private bool _reopenable;
    private bool _closed;
    private bool _muted = true;
    private MediaPlayer? _player;
    private bool _talkAvailable;
    private bool _talking;
    // With headset (0.8.0): door audio not muted while talking, output via the communications device.
    private bool _listenWhileTalking;
    private string? _outputDevice;

    private static readonly Brush TalkIdle = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB));
    private static readonly Brush TalkActive = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));

    public RingWindow(string callId, string doorName, DateTimeOffset startedAt, bool unlockAllowed, WindowMode mode,
        DateTimeOffset? until = null)
    {
        InitializeComponent();
        CallId = callId;
        Mode = mode;
        _unlockAllowed = unlockAllowed && mode == WindowMode.Ring;
        _closeTimer.Tick += (_, _) => Close();

        DoorText.Text = string.IsNullOrWhiteSpace(doorName) ? "Tür" : doorName;
        Title = $"SI EntryDesk – {DoorText.Text}";
        OpenButton.IsEnabled = _unlockAllowed;
        switch (mode)
        {
            case WindowMode.Test:
                TimeText.Text = $"{startedAt.ToLocalTime():HH:mm:ss} · Testklingeln";
                SetStatus("Test: nur auf diesem PC, Öffnen nicht möglich", Neutral);
                VideoText.Text = "Beim Testklingeln kein Livebild";
                CloseAfter(MaxLifetime);
                break;
            case WindowMode.LiveView:
                IsRinging = false;
                ShowActivated = true;
                Headline.Text = "Livebild";
                Headline.Foreground = Neutral;
                TimeText.Text = $"Ohne Klingeln · schliesst sich um {(until ?? startedAt).ToLocalTime():HH:mm:ss}";
                Title = $"SI EntryDesk – Livebild {DoorText.Text}";
                OpenButton.Visibility = Visibility.Collapsed;
                System.Windows.Controls.Grid.SetColumn(HideButton, 0);
                System.Windows.Controls.Grid.SetColumnSpan(HideButton, 3);
                HideButton.Content = "Schliessen";
                var remaining = (until ?? startedAt) - DateTimeOffset.Now;
                CloseAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1));
                break;
            default:
                TimeText.Text = $"{startedAt.ToLocalTime():HH:mm:ss}";
                if (!unlockAllowed)
                    SetStatus("Access erlaubt hier kein Öffnen", Neutral);
                CloseAfter(MaxLifetime);
                break;
        }
    }

    public WindowMode Mode { get; }

    public string CallId { get; }

    /// <summary>The ringtone plays as long as it rings.</summary>
    public bool IsRinging { get; private set; } = true;

    public event Action? UnlockRequested;
    public event Action? RingingChanged;

    /// <summary>Talk button pressed or released (also when closing or when talking ends).</summary>
    public event Action? TalkPressed;
    public event Action? TalkReleased;

    /// <summary>The talk button is currently pressed.</summary>
    public bool TalkHeld { get; private set; }

    /// <summary>Offer talkback (service: set up and camera of the door known).</summary>
    public void EnableTalk()
    {
        if (Mode != WindowMode.Ring)
            return;
        _talkAvailable = true;
        TalkButton.Visibility = Visibility.Visible;
        TalkButton.IsEnabled = true;
    }

    /// <summary>Before the live view: whether to hear the door while talking (headset) and on which device.</summary>
    internal void SetTalkAudio(TalkAudioPlan plan)
    {
        _listenWhileTalking = plan.ListenWhileTalking;
        _outputDevice = plan.OutputDeviceId;
    }

    /// <summary>Muted only while talking with speakers, otherwise there would be an echo.</summary>
    private bool PlayerMuted => _muted || (_talking && !_listenWhileTalking);

    /// <summary>Talk button pressed: the microphone runs immediately and buffers until talking is granted. With
    /// speakers the door audio is muted meanwhile, with a headset it stays on. One wants to hear the answer, so "Ton
    /// an" is switched on from now on.</summary>
    public void ShowTalking()
    {
        _talking = true;
        StopRinging();
        _muted = false;
        if (SoundButton.IsEnabled)
            SoundButton.Content = "Ton aus";
        if (_player is not null)
            _player.Mute = PlayerMuted;
        TalkButton.Content = _listenWhileTalking ? "Sie sprechen – Tür bleibt hörbar" : "Sie sprechen – loslassen zum Hören";
        TalkButton.Background = TalkActive;
    }

    /// <summary>Microphone off (after the tail, on errors): door audio back as set.</summary>
    public void ShowTalkIdle()
    {
        if (TalkHeld)
            return;
        _talking = false;
        if (_player is not null)
            _player.Mute = PlayerMuted;
        ShowTalkButtonIdle();
    }

    private void ShowTalkButtonIdle()
    {
        TalkButton.Content = "Sprechen – gedrückt halten";
        TalkButton.Background = TalkIdle;
    }

    /// <summary>Talking is not possible or was aborted: a button that is still held counts as released too.</summary>
    public void ShowTalkProblem(string message)
    {
        ReleaseTalk();
        ShowTalkIdle();
        SetStatus(message, Bad);
    }

    public void EndCall(CallEndReason reason)
    {
        if (_ended)
            return;
        _ended = true;
        StopRinging();
        // If the visitor cancelled (e.g. pressed twice) or nobody answered, unlocking is still allowed during the grace
        // period. The service checks this as well.
        _reopenable = _unlockAllowed && !_opened && reason is CallEndReason.Cancelled or CallEndReason.Timeout;
        OpenButton.IsEnabled = _reopenable && !_unlockPending;
        // Talking continues during the grace period, unless someone else answered or declined (the service checks too).
        if (_talkAvailable && reason is not (CallEndReason.Cancelled or CallEndReason.Timeout or CallEndReason.Opened))
        {
            ReleaseTalk();
            TalkButton.IsEnabled = false;
        }
        Headline.Text = "Ruf beendet";
        Headline.Foreground = Neutral;
        // After unlocking via the API, Access reports the end as "visitor cancelled" (108). The unlock takes precedence
        // in the display.
        if (!_opened)
            SetStatus(_reopenable ? $"{CallEndReasons.ToGerman(reason)}, Öffnen noch kurz möglich" : CallEndReasons.ToGerman(reason),
                reason == CallEndReason.Opened ? Good : Neutral);
        CloseAfter(LingerAfterEnd);
    }

    public void ShowOpened(string? openedBy)
    {
        _opened = true;
        _reopenable = false;
        StopRinging();
        OpenButton.IsEnabled = false;
        SetStatus(string.IsNullOrWhiteSpace(openedBy) ? "Tür geöffnet" : $"Tür geöffnet von {openedBy}", Good);
        CloseAfter(LingerAfterEnd);
    }

    public void ShowUnlockResult(bool success, string message)
    {
        _unlockPending = false;
        if (success)
        {
            _opened = true;
            _reopenable = false;
            StopRinging();
            SetStatus(message, Good);
            CloseAfter(LingerAfterEnd);
            return;
        }
        SetStatus(message, Bad);
        OpenButton.IsEnabled = _unlockAllowed && (!_ended || _reopenable);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FlashTaskbar();
    }

    /// <summary>Plays the live view via the service's one-time address. The door audio is muted, unless the user chose
    /// "Ton der Tür(en) automatisch einschalten". Then it replaces the ringtone.</summary>
    public void PlayVideo(LibVLC libVlc, string url, bool autoSound)
    {
        if (_player is not null || _closed)
            return;
        _muted = !autoSound;
        using var media = new Media(libVlc, new Uri(url), ":network-caching=300", ":rtsp-tcp");
        _player = new MediaPlayer(media);
        _player.Playing += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (_player is null)
                return;
            // Door audio to the communications device (headset). Only works once playback is running.
            if (_outputDevice is not null)
                _player.SetOutputDevice(_outputDevice);
            // While talking with speakers, the door audio stays muted, otherwise there is an echo.
            _player.Mute = PlayerMuted;
            VideoText.Text = "Livebild";
            SoundButton.IsEnabled = true;
            SoundButton.Content = _muted ? "Ton an" : "Ton aus";
            if (!_muted)
                StopRinging();
        });
        _player.EncounteredError += (_, _) => Dispatcher.InvokeAsync(() => ShowVideoProblem("Livebild unterbrochen"));
        Video.MediaPlayer = _player;
        VideoText.Text = "Livebild wird geladen …";
        _player.Play();
    }

    public void ShowVideoProblem(string reason)
    {
        VideoText.Text = $"Kein Livebild: {reason}";
        SoundButton.IsEnabled = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _closeTimer.Stop();
        ReleaseTalk();
        StopRinging();
        var player = _player;
        _player = null;
        Video.MediaPlayer = null;
        Video.Dispose();
        // Stop can block on network problems, therefore not on the UI thread.
        if (player is not null)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                player.Stop();
                player.Dispose();
            });
        base.OnClosed(e);
    }

    private void OnSoundClick(object sender, RoutedEventArgs e)
    {
        if (_player is null)
            return;
        _muted = !_muted;
        _player.Mute = PlayerMuted;
        SoundButton.Content = _muted ? "Ton an" : "Ton aus";
        // Whoever wants to hear the door no longer needs the ringtone.
        if (!_muted)
            StopRinging();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        OpenButton.IsEnabled = false;
        _unlockPending = true;
        SetStatus("Öffne …", Neutral);
        UnlockRequested?.Invoke();
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => Close();

    private void OnTalkDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!TalkButton.IsEnabled || TalkHeld)
            return;
        TalkHeld = true;
        TalkButton.CaptureMouse();
        TalkPressed?.Invoke();
    }

    private void OnTalkUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        TalkButton.ReleaseMouseCapture();
        ReleaseTalk();
    }

    private void OnTalkLost(object sender, System.Windows.Input.MouseEventArgs e) => ReleaseTalk();

    /// <summary>Button released, mouse gone, window closed or talking ended: report exactly once. With speakers the
    /// door audio stays muted until the tail is over (<see cref="ShowTalkIdle"/>), otherwise the microphone would still
    /// pick it up.</summary>
    public void ReleaseTalk()
    {
        if (!TalkHeld)
            return;
        TalkHeld = false;
        if (TalkButton.IsMouseCaptured)
            TalkButton.ReleaseMouseCapture();
        ShowTalkButtonIdle();
        TalkReleased?.Invoke();
    }

    private void SetStatus(string text, Brush color)
    {
        StatusText.Text = text;
        StatusText.Foreground = color;
    }

    private void StopRinging()
    {
        if (!IsRinging)
            return;
        IsRinging = false;
        RingingChanged?.Invoke();
    }

    private void CloseAfter(TimeSpan delay)
    {
        _closeTimer.Stop();
        _closeTimer.Interval = delay;
        _closeTimer.Start();
    }

    private void FlashTaskbar()
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = new WindowInteropHelper(this).Handle,
            Flags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG: blinken, bis das Fenster in den Vordergrund kommt
            Count = uint.MaxValue,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
}

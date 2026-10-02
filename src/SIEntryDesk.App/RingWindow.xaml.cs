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
    /// <summary>Echter Klingelruf.</summary>
    Ring,
    /// <summary>Testklingeln nur auf diesem PC, ohne Tür und Bild.</summary>
    Test,
    /// <summary>Livebild ohne Klingeln, vom Benutzer geöffnet.</summary>
    LiveView,
}

/// <summary>
/// Fenster zu einem Klingelruf. Liegt über allen Fenstern, übernimmt aber nicht die Tastatur,
/// damit niemand mitten im Tippen etwas auslöst.
/// </summary>
public partial class RingWindow : Window
{
    /// <summary>Sicherheitsnetz, falls das Ende des Rufs nie ankommt (Access beendet nach 60 s).</summary>
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(90);
    /// <summary>So lange bleibt das Fenster nach dem Ende offen, mit Livebild. Gleich lang wie die Nachfrist des Dienstes
    /// zum Öffnen nach „Besucher hat abgebrochen“ oder „Niemand hat abgenommen“.</summary>
    private static readonly TimeSpan LingerAfterEnd = TimeSpan.FromSeconds(10);

    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x7A));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x72));

    private readonly DispatcherTimer _closeTimer = new();
    private readonly bool _unlockAllowed;
    private bool _ended;
    private bool _opened;
    private bool _unlockPending;
    /// <summary>Nach dem Ende noch öffnen erlaubt (108/105), bis sich das Fenster schliesst.</summary>
    private bool _reopenable;
    private bool _closed;
    private bool _muted = true;
    private MediaPlayer? _player;
    private bool _talkAvailable;
    private bool _talking;

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

    /// <summary>Solange geklingelt wird, läuft der Klingelton.</summary>
    public bool IsRinging { get; private set; } = true;

    public event Action? UnlockRequested;
    public event Action? RingingChanged;

    /// <summary>Sprechtaste gedrückt bzw. losgelassen (auch beim Schliessen oder wenn das Sprechen endet).</summary>
    public event Action? TalkPressed;
    public event Action? TalkReleased;

    /// <summary>Die Sprechtaste ist gerade gedrückt.</summary>
    public bool TalkHeld { get; private set; }

    /// <summary>Gegensprechen anbieten (Dienst: eingerichtet und Kamera der Tür bekannt).</summary>
    public void EnableTalk()
    {
        if (Mode != WindowMode.Ring)
            return;
        _talkAvailable = true;
        TalkButton.Visibility = Visibility.Visible;
        TalkButton.IsEnabled = true;
    }

    public void ShowTalkConnecting() => TalkButton.Content = "Verbinde mit der Tür …";

    /// <summary>Der Dienst hat das Sprechen freigegeben. Der Ton der Tür ist so lange stumm, sonst gibt es Echo.</summary>
    public void ShowTalking()
    {
        _talking = true;
        StopRinging();
        if (_player is not null)
            _player.Mute = true;
        TalkButton.Content = "Sie sprechen – loslassen zum Hören";
        TalkButton.Background = TalkActive;
    }

    public void ShowTalkIdle()
    {
        if (_talking && _player is not null)
            _player.Mute = _muted;
        _talking = false;
        TalkButton.Content = "Sprechen – gedrückt halten";
        TalkButton.Background = TalkIdle;
    }

    public void ShowTalkProblem(string message)
    {
        ShowTalkIdle();
        SetStatus(message, Bad);
    }

    public void EndCall(CallEndReason reason)
    {
        if (_ended)
            return;
        _ended = true;
        StopRinging();
        // Hat der Besucher abgebrochen (z. B. zweimal gedrückt) oder niemand abgenommen, darf man in der Nachfrist
        // noch öffnen. Der Dienst prüft das ebenfalls.
        _reopenable = _unlockAllowed && !_opened && reason is CallEndReason.Cancelled or CallEndReason.Timeout;
        OpenButton.IsEnabled = _reopenable && !_unlockPending;
        // Sprechen geht in der Nachfrist weiter, ausser jemand anders hat angenommen oder abgelehnt (prüft auch der Dienst).
        if (_talkAvailable && reason is not (CallEndReason.Cancelled or CallEndReason.Timeout or CallEndReason.Opened))
        {
            ReleaseTalk();
            TalkButton.IsEnabled = false;
        }
        Headline.Text = "Ruf beendet";
        Headline.Foreground = Neutral;
        // Nach dem Öffnen über die API meldet Access das Ende als "Besucher hat abgebrochen" (108).
        // Die Öffnung hat in der Anzeige Vorrang.
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

    /// <summary>Spielt das Livebild über die Einmal-Adresse des Dienstes. Der Ton der Tür ist stumm, ausser der
    /// Benutzer hat „Ton der Tür automatisch einschalten“ gewählt. Dann ersetzt er den Klingelton.</summary>
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
            // Während gesprochen wird, bleibt der Ton der Tür stumm, sonst gibt es Echo.
            _player.Mute = _muted || _talking;
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
        // Stop kann bei Netzproblemen blockieren, deshalb nicht auf dem UI-Thread.
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
        _player.Mute = _muted;
        SoundButton.Content = _muted ? "Ton an" : "Ton aus";
        // Wer die Tür hören will, braucht den Klingelton nicht mehr.
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

    /// <summary>Taste losgelassen, Maus weg, Fenster zu oder Sprechen beendet: genau einmal melden.</summary>
    public void ReleaseTalk()
    {
        if (!TalkHeld)
            return;
        TalkHeld = false;
        if (TalkButton.IsMouseCaptured)
            TalkButton.ReleaseMouseCapture();
        ShowTalkIdle();
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

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
    private static readonly TimeSpan LingerAfterEnd = TimeSpan.FromSeconds(4);

    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x5F, 0xD0, 0x7A));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x72));

    private readonly DispatcherTimer _closeTimer = new();
    private readonly bool _unlockAllowed;
    private bool _ended;
    private bool _opened;
    private bool _closed;
    private bool _muted = true;
    private MediaPlayer? _player;

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
                TimeText.Text = $"Ohne Klingeln · schliesst sich um {(until ?? startedAt).ToLocalTime():HH:mm}";
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

    public void EndCall(CallEndReason reason)
    {
        if (_ended)
            return;
        _ended = true;
        StopRinging();
        OpenButton.IsEnabled = false;
        Headline.Text = "Ruf beendet";
        Headline.Foreground = Neutral;
        // Nach dem Öffnen über die API meldet Access das Ende als "Besucher hat abgebrochen" (108).
        // Die Öffnung hat in der Anzeige Vorrang.
        if (!_opened)
            SetStatus(CallEndReasons.ToGerman(reason), reason == CallEndReason.Opened ? Good : Neutral);
        CloseAfter(LingerAfterEnd);
    }

    public void ShowOpened(string? openedBy)
    {
        _opened = true;
        StopRinging();
        OpenButton.IsEnabled = false;
        SetStatus(openedBy is null ? "Tür geöffnet" : $"Tür geöffnet von {openedBy}", Good);
        CloseAfter(LingerAfterEnd);
    }

    public void ShowUnlockResult(bool success, string message)
    {
        if (success)
        {
            _opened = true;
            StopRinging();
            SetStatus(message, Good);
            CloseAfter(LingerAfterEnd + LingerAfterEnd);
            return;
        }
        SetStatus(message, Bad);
        OpenButton.IsEnabled = _unlockAllowed && !_ended;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        FlashTaskbar();
    }

    /// <summary>Spielt das Livebild über die Einmal-Adresse des Dienstes. Der Ton der Tür ist zuerst stumm.</summary>
    public void PlayVideo(LibVLC libVlc, string url)
    {
        if (_player is not null || _closed)
            return;
        using var media = new Media(libVlc, new Uri(url), ":network-caching=300", ":rtsp-tcp");
        _player = new MediaPlayer(media);
        _player.Playing += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (_player is null)
                return;
            _player.Mute = _muted;
            VideoText.Text = "Livebild";
            SoundButton.IsEnabled = true;
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
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        OpenButton.IsEnabled = false;
        SetStatus("Öffne …", Neutral);
        UnlockRequested?.Invoke();
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => Close();

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

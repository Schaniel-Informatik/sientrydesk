using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;
using Forms = System.Windows.Forms;

namespace SIEntryDesk.App;

internal sealed record TrayActions(
    Action TestRing,
    Action<string> LiveView,
    Action<TimeSpan> Pause,
    Action Resume,
    Action<bool> AutoSoundChanged,
    Action Check,
    Action Exit,
    Action<RingDoor, RingMode> RingModeChanged,
    Action ShowPendingNow,
    Action<ListenMode> ListenModeChanged,
    Func<bool> HeadsetDetected);

/// <summary>Eine Tür im Untermenü „Klingeln“ mit der Einstellung dieses Benutzers.</summary>
internal sealed record RingDoor(string DoorId, string Name, RingMode Mode);

/// <summary>
/// Symbol im Infobereich. Linksklick: kurzes Menü mit Status, Türen fürs Livebild (mit ihrer Einstellung), Pause und
/// „Einstellungen …“. Rechtsklick (oder „Einstellungen …“): Version, Status, jede Tür mit ihrer Einstellung „Klingeln“ zum
/// Ändern, Pause, Ton, Testklingeln, Prüfen und Beenden. Die Menüs werden bei jedem Öffnen aus dem aktuellen
/// Zustand gebaut. Während ein Ruf verzögert wird, blinkt das Symbol, und das Menü bietet „Jetzt anzeigen“.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    internal static readonly string AppVersion =
        typeof(TrayIcon).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "?";

    private static readonly TimeSpan[] PauseChoices =
        [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(120)];

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _quickMenu = new();
    private readonly Forms.ContextMenuStrip _fullMenu = new();
    private readonly Dictionary<TrayColor, Icon> _icons;
    private readonly Icon _ringIcon;
    private readonly Forms.Timer _blink = new() { Interval = 500 };
    private bool _blinkOn;
    private readonly TrayActions _actions;

    private TrayState _state = new(TrayColor.Grey, "Verbinde …", false);
    private string? _serviceVersion;
    private bool _doorsEnabled;
    private IReadOnlyList<DoorEntry> _doors = [];
    private DateTimeOffset? _pausedUntil;
    private string? _missed;
    private bool _autoSound;
    private ListenMode _listenMode;
    private bool _talkEnabled;
    private IReadOnlyList<RingDoor> _ringDoors = [];
    private string? _pending;

    public TrayIcon(TrayActions actions)
    {
        _actions = actions;
        _icons = Enum.GetValues<TrayColor>().ToDictionary(c => c, c => CreateIcon(ColorOf(c), pause: c == TrayColor.Blue));
        _ringIcon = CreateIcon(Color.FromArgb(255, 200, 0), pause: false);
        _quickMenu.Opening += (_, _) => Build(_quickMenu, full: false);
        _fullMenu.Opening += (_, _) => Build(_fullMenu, full: true);
        _icon = new Forms.NotifyIcon
        {
            Icon = _icons[TrayColor.Grey],
            Text = "SI EntryDesk",
            ContextMenuStrip = _fullMenu,
            Visible = true,
        };
        _blink.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            _icon.Icon = _blinkOn ? _ringIcon : _icons[_state.Color];
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowQuickMenu();
        };
    }

    public void Update(TrayState state, string? serviceVersion)
    {
        _state = state;
        _serviceVersion = serviceVersion;
        if (!_blink.Enabled)
            _icon.Icon = _icons[state.Color];
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        // Der Tooltip des Tray-Symbols ist auf 127 Zeichen begrenzt. Abweichungen bei „Klingeln“ stehen darin, damit
        // klar ist, warum es an einer Tür nicht klingelt. Im Menü stehen sie bei der Tür.
        var summary = _state.Color is TrayColor.Green or TrayColor.Orange
            ? RingSettings.Summary(_ringDoors.Select(d => (d.Name, d.Mode)))
            : null;
        var tooltip = _pending is not null ? $"SI EntryDesk: {_pending}"
            : summary is null ? $"SI EntryDesk: {_state.Text}"
            : $"SI EntryDesk: {_state.Text}. {summary}";
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    /// <summary>Türen für das Untermenü „Klingeln“.</summary>
    public void SetRingDoors(IReadOnlyList<RingDoor> doors)
    {
        _ringDoors = doors;
        UpdateTooltip();
    }

    /// <summary>Verzögerter Ruf: Symbol blinkt, Text am Symbol und „Jetzt anzeigen“ im Menü. Null beendet das Blinken.</summary>
    public void SetPending(string? text)
    {
        _pending = text;
        if (text is null)
        {
            _blink.Stop();
            _blinkOn = false;
            _icon.Icon = _icons[_state.Color];
        }
        else if (!_blink.Enabled)
        {
            _blinkOn = true;
            _icon.Icon = _ringIcon;
            _blink.Start();
        }
        UpdateTooltip();
    }

    public void SetDoors(bool enabled, IReadOnlyList<DoorEntry> doors)
    {
        _doorsEnabled = enabled;
        _doors = doors;
    }

    public void SetPause(DateTimeOffset? until, string? missed)
    {
        _pausedUntil = until;
        _missed = missed;
    }

    public void SetAutoSound(bool on) => _autoSound = on;

    public void SetListenMode(ListenMode mode) => _listenMode = mode;

    /// <summary>Gegensprechen auf diesem PC eingerichtet: erst dann gibt es „Tür beim Sprechen hören“.</summary>
    public void SetTalkEnabled(bool on) => _talkEnabled = on;

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _blink.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _quickMenu.Dispose();
        _fullMenu.Dispose();
        foreach (var icon in _icons.Values.Append(_ringIcon))
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
    }

    /// <summary>
    /// NotifyIcon zeigt von sich aus nur beim Rechtsklick ein Menü. Für den Linksklick wird kurz das kurze Menü
    /// eingesetzt und über die interne Methode geöffnet, damit es sich beim Klick daneben wieder schliesst.
    /// </summary>
    private void ShowQuickMenu() => ShowMenu(_quickMenu);

    /// <summary>„Einstellungen …“ im kurzen Menü: dasselbe wie ein Rechtsklick.</summary>
    private void ShowFullMenu() => ShowMenu(_fullMenu);

    private void ShowMenu(Forms.ContextMenuStrip menu)
    {
        var show = typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        if (show is null)
        {
            menu.Show(Forms.Cursor.Position);
            return;
        }
        _icon.ContextMenuStrip = menu;
        try
        {
            show.Invoke(_icon, null);
        }
        finally
        {
            _icon.ContextMenuStrip = _fullMenu;
        }
    }

    private void Build(Forms.ContextMenuStrip menu, bool full)
    {
        menu.Items.Clear();
        if (full)
        {
            var version = _serviceVersion is null || _serviceVersion == AppVersion
                ? $"SI EntryDesk {AppVersion}"
                : $"SI EntryDesk {AppVersion} (Dienst {_serviceVersion})";
            menu.Items.Add(new Forms.ToolStripMenuItem(version) { Enabled = false });
        }
        menu.Items.Add(new Forms.ToolStripMenuItem(_state.Text) { Enabled = false });
        if (_pending is not null)
            menu.Items.Add(new Forms.ToolStripMenuItem($"Jetzt anzeigen: {_pending.Replace("&", "&&", StringComparison.Ordinal)}", null,
                (_, _) => _actions.ShowPendingNow()) { Font = new Font(menu.Font, FontStyle.Bold) });

        if (full)
            AddRingDoors(menu);
        else if (_doorsEnabled)
            AddLiveViewDoors(menu);

        menu.Items.Add(new Forms.ToolStripSeparator());
        if (_pausedUntil is { } until && until > DateTimeOffset.Now)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem($"Pausiert bis {until.ToLocalTime():HH:mm}") { Enabled = false });
            menu.Items.Add("Klingel fortsetzen", null, (_, _) => _actions.Resume());
        }
        else
        {
            var pause = new Forms.ToolStripMenuItem("Klingel pausieren");
            foreach (var choice in PauseChoices)
                pause.DropDownItems.Add($"{choice.TotalMinutes:0} Minuten", null, (_, _) => _actions.Pause(choice));
            menu.Items.Add(pause);
        }
        if (_missed is not null)
            menu.Items.Add(new Forms.ToolStripMenuItem(_missed) { Enabled = false });

        if (!full)
        {
            // Für alle, die den Rechtsklick nicht kennen: öffnet dasselbe Menü.
            menu.Items.Add("Einstellungen …", null, (_, _) =>
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(ShowFullMenu));
            return;
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        var autoSound = new Forms.ToolStripMenuItem(_ringDoors.Count > 1 ? "Ton der Türen automatisch einschalten" : "Ton der Tür automatisch einschalten") { Checked = _autoSound, CheckOnClick = true };
        autoSound.CheckedChanged += (_, _) =>
        {
            _autoSound = autoSound.Checked;
            _actions.AutoSoundChanged(autoSound.Checked);
        };
        menu.Items.Add(autoSound);
        if (_talkEnabled)
            menu.Items.Add(ListenMenu());
        menu.Items.Add("Testklingeln (nur dieser PC)", null, (_, _) => _actions.TestRing());
        menu.Items.Add("Prüfen …", null, (_, _) => _actions.Check());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => _actions.Exit());
    }

    /// <summary>Linksklick: Türen für das Livebild ohne Klingeln, mit ihrer Einstellung „Klingeln“ wie beim Rechtsklick.
    /// Ein Klick öffnet das Livebild, die Einstellung ändert man über „Einstellungen …“ bzw. den Rechtsklick.</summary>
    private void AddLiveViewDoors(Forms.ContextMenuStrip menu)
    {
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (_doors.Count == 0)
            menu.Items.Add(new Forms.ToolStripMenuItem("Livebild: erscheint nach dem ersten Klingeln") { Enabled = false });
        foreach (var door in _doors)
        {
            var doorId = door.DoorId;
            var mode = _ringDoors.FirstOrDefault(d => d.DoorId == doorId)?.Mode ?? RingMode.Immediate;
            menu.Items.Add(MenuText($"{door.Name}: {RingSettings.Label(mode)}"), null, (_, _) => _actions.LiveView(doorId));
        }
    }

    /// <summary>Rechtsklick: jede Tür mit ihrer Einstellung „Klingeln“ (sofort, nach 10 s, aus) für diesen Benutzer.
    /// Hier nur die Einstellung, das Livebild gibt es im Menü per Linksklick.</summary>
    private void AddRingDoors(Forms.ContextMenuStrip menu)
    {
        menu.Items.Add(new Forms.ToolStripSeparator());
        if (_ringDoors.Count == 0)
            menu.Items.Add(new Forms.ToolStripMenuItem("Türen erscheinen nach dem ersten Klingeln") { Enabled = false });
        foreach (var door in _ringDoors)
        {
            var item = new Forms.ToolStripMenuItem(MenuText($"{door.Name}: {RingSettings.Label(door.Mode)}"));
            foreach (var mode in Enum.GetValues<RingMode>())
            {
                var choice = mode;
                item.DropDownItems.Add(new Forms.ToolStripMenuItem(RingSettings.Label(mode), null,
                    (_, _) => _actions.RingModeChanged(door, choice)) { Checked = door.Mode == mode });
            }
            menu.Items.Add(item);
        }
    }

    /// <summary>„Tür beim Sprechen hören“: automatisch (Headset erkannt?), immer oder nie.</summary>
    private Forms.ToolStripMenuItem ListenMenu()
    {
        var item = new Forms.ToolStripMenuItem($"Tür beim Sprechen hören: {TalkAudio.Label(_listenMode)}");
        foreach (var mode in Enum.GetValues<ListenMode>())
        {
            var choice = mode;
            var label = mode == ListenMode.Auto
                ? $"automatisch ({(_actions.HeadsetDetected() ? "Headset erkannt" : "kein Headset erkannt")})"
                : TalkAudio.Label(mode);
            item.DropDownItems.Add(new Forms.ToolStripMenuItem(label, null, (_, _) =>
            {
                _listenMode = choice;
                _actions.ListenModeChanged(choice);
            }) { Checked = _listenMode == mode });
        }
        return item;
    }

    /// <summary>"&" wäre im Menü ein Tastenkürzel.</summary>
    private static string MenuText(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    private static Color ColorOf(TrayColor state) => state switch
    {
        TrayColor.Green => Color.FromArgb(40, 200, 90),
        TrayColor.Orange => Color.FromArgb(255, 150, 20),
        TrayColor.Red => Color.FromArgb(220, 45, 45),
        TrayColor.Blue => Color.FromArgb(50, 120, 220),
        _ => Color.FromArgb(140, 140, 140),
    };

    /// <summary>Kreis in der Zustandsfarbe mit der Klingel aus dem Programm-Icon, bei Pause mit Pausenzeichen.
    /// Gleiche Geometrie wie build/make_icon.py, in 16er-Einheiten.</summary>
    private static Icon CreateIcon(Color color, bool pause)
    {
        var size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        var u = size / 16f;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 0.2f * u, 0.2f * u, 15.6f * u, 15.6f * u);
            using var white = new SolidBrush(Color.White);
            if (pause)
            {
                g.FillRectangle(white, 5f * u, 4.5f * u, 2.2f * u, 7f * u);
                g.FillRectangle(white, 8.8f * u, 4.5f * u, 2.2f * u, 7f * u);
            }
            else
            {
                FillBell(g, white, u);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static void FillBell(Graphics g, Brush brush, float u)
    {
        const float cx = 8f, cy = 6.8f, rx = 4.2f, ry = 4.4f, bottom = 10.6f;
        g.FillPie(brush, (cx - rx) * u, (cy - ry) * u, 2 * rx * u, 2 * ry * u, 180, 180);
        var left = new List<PointF>();
        var right = new List<PointF>();
        for (var i = 0; i <= 8; i++)
        {
            var y = cy + (bottom - cy) * i / 8f;
            var t = (y - cy) / (bottom - cy);
            var half = rx + 1.3f * t * t;
            left.Add(new PointF((cx - half) * u, y * u));
            right.Insert(0, new PointF((cx + half) * u, y * u));
        }
        g.FillPolygon(brush, [.. left, .. right]);
        const float rimY = 11.1f, rimR = 0.75f;
        g.FillRectangle(brush, 3.2f * u, (rimY - rimR) * u, 9.6f * u, 2 * rimR * u);
        g.FillEllipse(brush, (3.2f - rimR) * u, (rimY - rimR) * u, 2 * rimR * u, 2 * rimR * u);
        g.FillEllipse(brush, (12.8f - rimR) * u, (rimY - rimR) * u, 2 * rimR * u, 2 * rimR * u);
        g.FillEllipse(brush, (8f - 1.35f) * u, (12.9f - 1.35f) * u, 2.7f * u, 2.7f * u);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using SIEntryDesk.Core.Ipc;
using Forms = System.Windows.Forms;

namespace SIEntryDesk.App;

/// <summary>Symbol im Infobereich mit Status und Menü. Farbe: grün bereit, orange Problem, grau kein Dienst.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private static readonly string AppVersion =
        typeof(TrayIcon).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "?";

    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _versionItem;
    private readonly Forms.ToolStripMenuItem _testItem;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly List<Forms.ToolStripItem> _doorItems = [];
    private readonly Action<string> _onLiveView;
    private readonly Icon _ready = CreateIcon(Color.FromArgb(40, 200, 90));
    private readonly Icon _problem = CreateIcon(Color.FromArgb(255, 150, 20));
    private readonly Icon _offline = CreateIcon(Color.FromArgb(140, 140, 140));

    public TrayIcon(Action onTestRing, Action<string> onLiveView, Action onExit)
    {
        _onLiveView = onLiveView;
        _statusItem = new Forms.ToolStripMenuItem("Verbinde …") { Enabled = false };
        _versionItem = new Forms.ToolStripMenuItem($"SI EntryDesk {AppVersion}") { Enabled = false };
        _testItem = new Forms.ToolStripMenuItem("Testklingeln (nur dieser PC)", null, (_, _) => onTestRing());
        var menu = _menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_versionItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_testItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => onExit());

        _icon = new Forms.NotifyIcon
        {
            Icon = _offline,
            Text = "SI EntryDesk",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void SetStatus(ServiceClientStatus status)
    {
        var (icon, text) = status switch
        {
            { Connected: false } => (_offline, "Dienst nicht erreichbar"),
            { Service: { AccessConnected: true } } => (_ready, "Bereit"),
            { Service: { Problem: { Length: > 0 } problem } } => (_problem, problem),
            _ => (_problem, "Keine Verbindung zu Access"),
        };
        _icon.Icon = icon;
        // Der Tooltip des Tray-Symbols ist auf 127 Zeichen begrenzt.
        var tooltip = $"SI EntryDesk: {text}";
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _statusItem.Text = text;
        var serviceVersion = status.Service?.ServiceVersion;
        _versionItem.Text = serviceVersion is null || serviceVersion == AppVersion
            ? $"SI EntryDesk {AppVersion}"
            : $"SI EntryDesk {AppVersion} (Dienst {serviceVersion})";
    }

    /// <summary>Einträge für das Livebild ohne Klingeln, vor dem Testklingeln.</summary>
    public void SetDoors(bool enabled, IReadOnlyList<DoorEntry> doors)
    {
        foreach (var item in _doorItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _doorItems.Clear();
        if (!enabled)
            return;

        if (doors.Count == 0)
            _doorItems.Add(new Forms.ToolStripMenuItem("Livebild: erscheint nach dem ersten Klingeln") { Enabled = false });
        foreach (var door in doors)
        {
            var doorId = door.DoorId;
            // "&" wäre im Menü ein Tastenkürzel.
            _doorItems.Add(new Forms.ToolStripMenuItem($"Livebild {door.Name.Replace("&", "&&", StringComparison.Ordinal)}",
                null, (_, _) => _onLiveView(doorId)));
        }
        _doorItems.Add(new Forms.ToolStripSeparator());

        var index = _menu.Items.IndexOf(_testItem);
        foreach (var item in _doorItems)
            _menu.Items.Insert(index++, item);
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in new[] { _ready, _problem, _offline })
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
    }

    /// <summary>In der Grösse, die Windows für Tray-Symbole verwendet, damit nichts unscharf herunterskaliert wird.</summary>
    private static Icon CreateIcon(Color color)
    {
        var size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        float u = size / 16f;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 0, 0, size - 1, size - 1);
            // Stilisierte Klingel: Glockenkörper, Rand und Klöppel, bewusst kräftig.
            using var white = new SolidBrush(Color.White);
            g.FillPie(white, 3.5f * u, 2.5f * u, 9f * u, 10f * u, 180, 180);
            g.FillRectangle(white, 3.5f * u, 7.4f * u, 9f * u, 3f * u);
            g.FillRectangle(white, 2.5f * u, 10.2f * u, 11f * u, 1.8f * u);
            g.FillEllipse(white, 6.6f * u, 11.6f * u, 2.8f * u, 2.8f * u);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

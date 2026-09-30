using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Setup;
using SIEntryDesk.Core.Video;

namespace SIEntryDesk.App;

/// <summary>
/// Einrichtungsassistent für Admins (SIEntryDesk.exe --setup). Prüft Konsole, Zugänge und Streams und speichert die
/// sientrydesk.json. Tokens bleiben nur im Speicher dieses Fensters und werden nirgends abgelegt.
/// </summary>
public partial class SetupWindow : Window
{
    private sealed record DoorRow(AccessDoor Door, CheckBox Show, ComboBox Camera, TextBlock Stream, Button Create, Button Test);
    private sealed record CameraChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly Task<LibVLC?> _libVlc;
    private readonly List<DoorRow> _rows = [];
    private string _host = string.Empty;
    private IReadOnlyList<PortCheck> _ports = [];
    private AccessApiClient? _access;
    private ProtectApiClient? _protect;
    private StreamProxy? _proxy;
    private IReadOnlyList<AccessDoor>? _doors;
    private IReadOnlyDictionary<string, string>? _cameras;
    private AccessRights? _rights;

    internal SetupWindow(Task<LibVLC?> libVlc)
    {
        InitializeComponent();
        _libVlc = libVlc;
        var inOneYear = DateTime.Today.AddYears(1);
        TokenExpiresPicker.SelectedDate = inOneYear;
        ProtectKeyExpiresPicker.SelectedDate = inOneYear;
    }

    protected override void OnClosed(EventArgs e)
    {
        _access?.Dispose();
        _protect?.Dispose();
        if (_proxy is not null)
            _ = _proxy.DisposeAsync().AsTask();
        AccessTokenBox.Clear();
        ProtectKeyBox.Clear();
        base.OnClosed(e);
    }

    private string? Pin(int port) => _ports.FirstOrDefault(p => p.Port == port)?.Fingerprint;

    // ---------------------------------------------------------------- 1. Konsole

    private async void OnCheckHost(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            HostResult.Text = "✗ Kein gültiger Name und keine gültige IP.";
            return;
        }
        ResetAfterHost();
        _host = host;
        HostCheck.IsEnabled = false;
        HostResult.Text = "Prüfe …";
        try
        {
            _ports = await SetupChecks.CheckPortsAsync(host, CancellationToken.None);
        }
        finally
        {
            HostCheck.IsEnabled = true;
        }

        var text = new StringBuilder();
        foreach (var port in _ports)
        {
            text.AppendLine(port.Reachable
                ? $"✓ {port.Name}, Port {port.Port}: erreichbar, Zertifikat {port.Fingerprint}"
                : $"✗ {port.Name}, Port {port.Port}: nicht erreichbar ({port.Problem})");
        }
        var accessOk = Pin(AccessApiClient.Port) is not null;
        if (!accessOk)
            text.AppendLine("Ohne Port 12445 geht es nicht: Netz, VPN oder Firewall prüfen.");
        else if (Pin(443) is null || Pin(7441) is null)
            text.AppendLine("Ohne Port 443 und 7441 gibt es kein Livebild.");
        HostResult.Text = text.ToString().TrimEnd();
        PinsConfirmed.IsEnabled = accessOk;
    }

    private void ResetAfterHost()
    {
        PinsConfirmed.IsChecked = false;
        PinsConfirmed.IsEnabled = false;
        _access?.Dispose();
        _protect?.Dispose();
        _access = null;
        _protect = null;
        _doors = null;
        _cameras = null;
        _rights = null;
        AccessResult.Text = string.Empty;
        ProtectResult.Text = string.Empty;
        BuildDoorRows();
    }

    private void OnPinsConfirmed(object sender, RoutedEventArgs e)
    {
        var confirmed = PinsConfirmed.IsChecked == true;
        AccessCheck.IsEnabled = confirmed;
        ProtectCheck.IsEnabled = confirmed && Pin(443) is not null;
        UpdateSaveButton();
    }

    // ---------------------------------------------------------------- 2. Access

    private async void OnCheckAccess(object sender, RoutedEventArgs e)
    {
        var token = AccessTokenBox.Password.Trim();
        if (token.Length == 0 || Pin(AccessApiClient.Port) is not { } pin)
            return;
        AccessCheck.IsEnabled = false;
        AccessResult.Text = "Prüfe …";
        try
        {
            _access?.Dispose();
            _access = new AccessApiClient(_host, token, CertificatePin.Parse(pin));
            _rights = await SetupChecks.CheckAccessTokenAsync(_access, CancellationToken.None);
            _doors = _rights.Doors ? await _access.GetDoorsAsync(CancellationToken.None) : null;

            var text = new StringBuilder()
                .AppendLine(Mark(_rights.Events) + "Klingel-Ereignisse (Gerät = Anzeigen)")
                .AppendLine(Mark(_rights.Doors) + "Türliste (Standorte = Anzeigen)")
                .AppendLine(Mark(_rights.Unlock) + "Öffnen (Standorte = Bearbeiten)");
            text.AppendLine(_rights.Excess.Count == 0
                ? "✓ Keine überflüssigen Rechte"
                : $"⚠ Überflüssige Rechte: {string.Join(", ", _rights.Excess)}. Token löschen und neu anlegen, dort „Keinen“.");
            if (_doors is not null)
                text.AppendLine($"Türen: {string.Join(", ", _doors.Select(d => d.Name))}");
            AccessResult.Text = text.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or AuthenticationException or JsonException)
        {
            AccessResult.Text = $"✗ Keine Antwort von Access: {ex.Message}";
        }
        finally
        {
            AccessCheck.IsEnabled = true;
        }
        BuildDoorRows();
        UpdateSaveButton();
    }

    private static string Mark(bool ok) => ok ? "✓ " : "✗ ";

    // ---------------------------------------------------------------- 3. Protect

    private async void OnCheckProtect(object sender, RoutedEventArgs e)
    {
        var key = ProtectKeyBox.Password.Trim();
        if (key.Length == 0 || Pin(443) is not { } pin)
            return;
        ProtectCheck.IsEnabled = false;
        ProtectResult.Text = "Prüfe …";
        try
        {
            _protect?.Dispose();
            _protect = new ProtectApiClient(_host, key, CertificatePin.Parse(pin));
            var version = await _protect.GetVersionAsync(CancellationToken.None);
            _cameras = await _protect.GetCameraNamesAsync(CancellationToken.None);
            ProtectResult.Text = $"✓ Protect {version}\nKameras: {string.Join(", ", _cameras.Values)}";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _cameras = null;
            ProtectResult.Text = "✗ Protect lehnt den Schlüssel ab.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or AuthenticationException or JsonException)
        {
            _cameras = null;
            ProtectResult.Text = $"✗ Keine Antwort von Protect: {ex.Message}";
        }
        finally
        {
            ProtectCheck.IsEnabled = true;
        }
        BuildDoorRows();
        UpdateSaveButton();
    }

    // ---------------------------------------------------------------- 4. Türen und Kameras

    private void BuildDoorRows()
    {
        DoorGrid.Children.Clear();
        DoorGrid.RowDefinitions.Clear();
        _rows.Clear();
        DoorsPlaceholder.Visibility = _doors is null ? Visibility.Visible : Visibility.Collapsed;
        if (_doors is null)
            return;

        var suggestions = _cameras is null
            ? new Dictionary<string, string>()
            : SetupChecks.SuggestDoorCameras(_doors, _cameras);
        var choices = new List<CameraChoice> { new(null, "(keine Kamera)") };
        if (_cameras is not null)
            choices.AddRange(_cameras.Select(c => new CameraChoice(c.Key, c.Value)).OrderBy(c => c.Name));

        foreach (var door in _doors)
        {
            var row = DoorGrid.RowDefinitions.Count;
            DoorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var show = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 10, 6) };
            var name = new TextBlock { Text = door.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var camera = new ComboBox { ItemsSource = choices, Margin = new Thickness(8, 4, 8, 4), IsEnabled = _cameras is not null };
            camera.SelectedItem = choices.FirstOrDefault(c => c.Id == suggestions.GetValueOrDefault(door.Id)) ?? choices[0];
            var stream = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.DimGray };
            var create = new Button { Content = "Stream anlegen", IsEnabled = false };
            var test = new Button { Content = "Livebild testen", IsEnabled = false };

            var entry = new DoorRow(door, show, camera, stream, create, test);
            _rows.Add(entry);
            camera.SelectionChanged += async (_, _) => await RefreshStreamAsync(entry);
            create.Click += async (_, _) => await CreateStreamAsync(entry);
            test.Click += async (_, _) => await TestLiveViewAsync(entry);
            show.Checked += (_, _) => UpdateSaveButton();
            show.Unchecked += (_, _) => UpdateSaveButton();

            UIElement[] cells = [show, name, camera, stream, create, test];
            for (var col = 0; col < cells.Length; col++)
            {
                Grid.SetRow(cells[col], row);
                Grid.SetColumn(cells[col], col);
                DoorGrid.Children.Add(cells[col]);
            }
            _ = RefreshStreamAsync(entry);
        }
    }

    private static string? SelectedCamera(DoorRow row) => (row.Camera.SelectedItem as CameraChoice)?.Id;

    private async Task RefreshStreamAsync(DoorRow row)
    {
        row.Create.IsEnabled = false;
        row.Test.IsEnabled = false;
        if (_protect is null || SelectedCamera(row) is not { } camera)
        {
            row.Stream.Text = _protect is null ? "" : "–";
            return;
        }
        row.Stream.Text = "prüfe …";
        try
        {
            var streams = await _protect.GetStreamsAsync(camera, CancellationToken.None);
            row.Stream.Text = streams.Count == 0 ? "kein Stream" : $"Stream: {string.Join(", ", streams.Keys)}";
            row.Create.IsEnabled = streams.Count == 0;
            row.Test.IsEnabled = streams.Count > 0 && Pin(7441) is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or AuthenticationException or JsonException)
        {
            row.Stream.Text = "nicht lesbar";
        }
    }

    private async Task CreateStreamAsync(DoorRow row)
    {
        if (_protect is null || SelectedCamera(row) is not { } camera)
            return;
        var answer = MessageBox.Show(this,
            $"Für die Kamera „{row.Camera.SelectedItem}“ einen RTSPS-Stream in Protect anlegen?\n\n" +
            "Das ändert die Protect-Einstellungen dieser Kamera. Die Stream-Adresse wirkt wie ein Zugangsschlüssel " +
            "zum Kamerabild, SI EntryDesk gibt sie nicht an die Benutzer weiter.",
            "Stream anlegen", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        try
        {
            await _protect.CreateStreamAsync(camera, "medium", CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or AuthenticationException or JsonException)
        {
            MessageBox.Show(this, $"Stream konnte nicht angelegt werden: {ex.Message}", "Stream anlegen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await RefreshStreamAsync(row);
    }

    /// <summary>Spielt das Livebild über denselben geprüften Weg wie die App: Stream-Proxy mit Pin, LibVLC.</summary>
    private async Task TestLiveViewAsync(DoorRow row)
    {
        if (_protect is null || SelectedCamera(row) is not { } camera || Pin(7441) is not { } streamPin)
            return;
        var streams = await _protect.GetStreamsAsync(camera, CancellationToken.None);
        var source = new[] { "low", "medium", "high" }.Select(q => streams.GetValueOrDefault(q)).FirstOrDefault(s => s is not null);
        var libVlc = await _libVlc;
        if (source is null || libVlc is null)
        {
            MessageBox.Show(this, source is null ? "Kein Stream vorhanden." : "Der Videoplayer konnte nicht geladen werden.",
                "Livebild testen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_proxy is null)
        {
            _proxy = new StreamProxy(CertificatePin.Parse(streamPin), NullLogger.Instance, TimeProvider.System);
            _proxy.Start();
        }
        var lifetime = TimeSpan.FromSeconds(30);
        var url = _proxy.Open("setup-" + Guid.NewGuid().ToString("N"), source, lifetime);
        var window = new RingWindow("setup-" + row.Door.Id, $"{row.Camera.SelectedItem}", DateTimeOffset.Now, unlockAllowed: false,
            WindowMode.LiveView, DateTimeOffset.Now + lifetime) { Owner = this };
        window.Show();
        window.PlayVideo(libVlc, url, autoSound: false);
    }

    // ---------------------------------------------------------------- 5./6. Speichern

    private void UpdateSaveButton() =>
        SaveButton.IsEnabled = PinsConfirmed.IsChecked == true && _rights is { Events: true } && _doors is not null;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LiveViewSecondsBox.Text.Trim(), out var seconds) || seconds is < 15 or > 600)
        {
            SaveResult.Text = "✗ Dauer des Livebilds: eine Zahl von 15 bis 600.";
            return;
        }
        var warnings = new List<string>();
        if (_rights is { Complete: false })
            warnings.Add("Der Access-Token hat nicht alle nötigen Rechte.");
        if (_rights is { Excess.Count: > 0 })
            warnings.Add("Der Access-Token hat überflüssige Rechte.");
        if (_cameras is null)
            warnings.Add("Der Protect-Schlüssel ist nicht geprüft, das Livebild bleibt ohne ihn aus.");
        if (warnings.Count > 0 && MessageBox.Show(this, string.Join("\n", warnings) + "\n\nTrotzdem speichern?",
                "Speichern", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var shown = _rows.Where(r => r.Show.IsChecked == true).Select(r => r.Door.Id).ToList();
        var options = new EntryDeskOptions
        {
            Host = _host,
            AccessPin = Pin(AccessApiClient.Port)!,
            ProtectPin = Pin(443) ?? string.Empty,
            StreamPin = Pin(7441) ?? string.Empty,
            Doors = shown.Count == _rows.Count ? [] : shown,
            DoorCameras = _rows.Where(r => SelectedCamera(r) is not null)
                .ToDictionary(r => r.Door.Id, r => SelectedCamera(r)!, StringComparer.Ordinal),
            LiveView = LiveViewBox.IsChecked == true,
            LiveViewSeconds = seconds,
            TokenExpires = TokenExpiresPicker.SelectedDate,
            ProtectKeyExpires = ProtectKeyExpiresPicker.SelectedDate,
        };

        var dialog = new SaveFileDialog
        {
            FileName = "sientrydesk.json",
            Filter = "SI EntryDesk Konfiguration (*.json)|*.json",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        File.WriteAllText(dialog.FileName, SetupChecks.ToConfigJson(options, DateTime.Now), new UTF8Encoding(false));
        SaveResult.Text = $"✓ Gespeichert: {dialog.FileName}";
    }
}

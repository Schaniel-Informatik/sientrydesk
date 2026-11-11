using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Setup;
using SIEntryDesk.Core.Video;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.App;

/// <summary>
/// Setup wizard for admins (SIEntryDesk.exe --setup [file]). Checks console, credentials and streams and saves the
/// sientrydesk.json. An existing one can be loaded, then it shows how the installation differs from it. Tokens stay
/// only in the memory of this window and are stored nowhere.
/// </summary>
public partial class SetupWindow : Window
{
    private sealed record DoorRow(AccessDoor Door, CheckBox Show, ComboBox Camera, TextBlock Stream, Button Create, Button Test);
    private sealed record CameraChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private enum Mark
    {
        Ok,
        Fail,
        Warn,
        Info,
    }

    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E));
    private static readonly Brush FailBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x7B, 0x00));

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
    private EntryDeskOptions? _loaded;
    private string? _loadedPath;

    private static string InstalledConfigPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIEntryDesk", "sientrydesk.json");

    internal SetupWindow(Task<LibVLC?> libVlc, string? configPath)
    {
        InitializeComponent();
        ApplyTexts();
        _libVlc = libVlc;
        var inOneYear = DateTime.Today.AddYears(1);
        TokenExpiresPicker.SelectedDate = inOneYear;
        ProtectKeyExpiresPicker.SelectedDate = inOneYear;
        if (configPath is not null)
            Loaded += async (_, _) => await LoadConfigAsync(configPath);
    }

    /// <summary>Texts of the window, in the language of Windows: German, otherwise English.</summary>
    private void ApplyTexts()
    {
        var check = Tr.Admin("Prüfen", "Check");
        Title = Tr.Admin("SI EntryDesk – Einrichtung", "SI EntryDesk – Setup");
        Heading.Text = Tr.Admin("Einrichtung", "Setup");
        Intro.Text = Tr.Admin(
            "Prüft die Voraussetzungen bei UniFi und erstellt die sientrydesk.json für diese Anlage, oder prüft eine bestehende. " +
            "Tokens werden nur zum Prüfen verwendet und nicht gespeichert. Nur im Firmennetz oder über VPN ausführen.",
            "Checks the prerequisites at UniFi and creates sientrydesk.json for this installation, or checks an existing one. " +
            "Tokens are only used for checking and are not stored. Run only in the company network or via VPN.");
        LoadInstalledButton.Content = Tr.Admin("Installierte Konfiguration laden", "Load installed configuration");
        OpenConfigButton.Content = Tr.Admin("Andere sientrydesk.json öffnen …", "Open another sientrydesk.json …");
        HostHeading.Text = Tr.Admin("1. Konsole", "1. Console");
        HostHint.Text = Tr.Admin("Name oder IP der UniFi-Konsole, auf der Access und Protect laufen.",
            "Name or IP of the UniFi console that runs Access and Protect.");
        HostCheck.Content = check;
        PinsConfirmedText.Text = Tr.Admin(
            "Diese Verbindung geht sicher zur richtigen Konsole (Firmennetz oder VPN). Die Fingerabdrücke als Pins übernehmen.",
            "This connection surely goes to the right console (company network or VPN). Take over the fingerprints as pins.");
        AccessHeading.Text = Tr.Admin("2. Access-Token", "2. Access token");
        AccessHint.Text = Tr.Admin(
            "Access → Einstellungen → Allgemein → API Token. Benötigt: Gerät = Anzeigen, Standorte = Bearbeiten, alles andere " +
            "Keinen. Geprüft wird rein lesend, das Recht zum Öffnen an einer Tür, die es nicht gibt.",
            "Access → Settings → General → API Token. Needed: Device = View, Location = Edit, everything else None. The check " +
            "only reads, the permission to open is checked on a door that does not exist.");
        AccessCheck.Content = check;
        TokenExpiresLabel.Text = Tr.Admin("Gültig bis (wie in Access beim Anlegen angezeigt):", "Valid until (as shown in Access when created):");
        ProtectHeading.Text = Tr.Admin("3. Protect-Schlüssel (für das Livebild)", "3. Protect key (for the live view)");
        ProtectHint.Text = Tr.Admin(
            "UniFi OS → Einstellungen → Control Plane → Integrations. Der Schlüssel hat die Protect-Rechte des Kontos, unter dem " +
            "er angelegt wurde.",
            "UniFi OS → Settings → Control Plane → Integrations. The key has the Protect permissions of the account it was " +
            "created under.");
        ProtectCheck.Content = check;
        ProtectKeyExpiresLabel.Text = Tr.Admin("Gültig bis (wie in UniFi OS beim Anlegen angezeigt):", "Valid until (as shown in UniFi OS when created):");
        ExpiryHint.Text = Tr.Admin(
            "Das Ablaufdatum geben die Schnittstellen nicht preis, es steht nur in der UniFi-Oberfläche. Ab 14 Tagen vor dem " +
            "früheren der beiden Daten wird das Symbol orange, und jede App meldet einmal pro Tag, bis wann erneuert werden muss.",
            "The interfaces do not reveal the expiry date, it is only shown in the UniFi interface. From 14 days before the " +
            "earlier of the two dates the icon turns orange, and every app reports once a day by when to renew.");
        DoorsHeading.Text = Tr.Admin("4. Türen und Kameras", "4. Doors and cameras");
        DoorsHint.Text = Tr.Admin(
            "Zu jeder Tür die Kamera ihrer Türstation. Der Vorschlag kommt aus den Namen. Angehakte Türen erscheinen auf den " +
            "PCs, alle angehakt = alle Türen.",
            "For each door the camera of its door station. The suggestion comes from the names. Checked doors appear on the " +
            "PCs, all checked = all doors.");
        DoorsPlaceholder.Text = Tr.Admin("Erscheint nach der Prüfung von Access-Token und Protect-Schlüssel.",
            "Appears after checking the Access token and the Protect key.");
        StreamHint.Text = Tr.Admin(
            "Ohne Stream kein Livebild. Anlegen mit „Stream anlegen“ oder in Protect bei der Kamera unter Einstellungen → " +
            "Erweitert → RTSP, Qualität „Mittel“. Die Stream-Adresse wirkt wie ein Zugangsschlüssel, nicht weitergeben.",
            "No stream, no live view. Create it with \"Create stream\" or in Protect at the camera under Settings → Advanced → " +
            "RTSP, quality \"Medium\". The stream address works like an access key, do not pass it on.");
        LiveViewHeading.Text = Tr.Admin("5. Livebild ohne Klingeln", "5. Live view without ringing");
        LiveViewText.Text = Tr.Admin(
            "Benutzer dürfen das Livebild jederzeit öffnen, auch wenn niemand klingelt (Linksklick auf das Klingel-Symbol, " +
            "dann die Tür). Jeder Abruf wird mit Benutzer protokolliert.",
            "Users may open the live view at any time, even if nobody rings (left click on the bell icon, then the door). " +
            "Every request is logged with the user.");
        LiveViewSecondsLabel.Text = Tr.Admin("Das Livebild schliesst sich nach (15–600 Sekunden):", "The live view closes after (15–600 seconds):");
        LanguageHeading.Text = Tr.Admin("6. Sprache der Benutzer", "6. Language for the users");
        LanguageHint.Text = Tr.Admin(
            "Klingelfenster, Menü und Meldungen auf den PCs. Prüfen und Protokoll: Deutsch bei Deutsch, sonst Englisch.",
            "Ring window, menu and notifications on the PCs. Check and log: German for German, otherwise English.");
        SaveHeading.Text = Tr.Admin("7. Speichern", "7. Save");
        SaveButton.Content = Tr.Admin("sientrydesk.json speichern …", "Save sientrydesk.json …");
        SaveHint.Text = Tr.Admin(
            "Danach: sientrydesk.json neben install.ps1 legen. Von Hand installieren mit install.ps1 -SetTokens. Per Intune das " +
            "Programmpaket mit der sientrydesk.json und die Zugänge als eigenes Paket (SIEntryDesk-Tokens), siehe " +
            "Doku\\installation.md und Doku\\intune.md im Paket.",
            "Then: put sientrydesk.json next to install.ps1. Install by hand with install.ps1 -SetTokens. Via Intune the " +
            "program package with sientrydesk.json and the credentials as a separate package (SIEntryDesk-Tokens), see " +
            "Doku\\en\\installation.md and Doku\\en\\intune.md in the package.");
    }

    private UiLanguage SelectedLanguage =>
        LanguageEn.IsChecked == true ? UiLanguage.En : LanguagePl.IsChecked == true ? UiLanguage.Pl : UiLanguage.De;

    private static string ConfigFilter => Tr.Admin("SI EntryDesk Konfiguration (*.json)|*.json", "SI EntryDesk configuration (*.json)|*.json");

    private static string Checking => Tr.Admin("Prüfe …", "Checking …");

    /// <summary>The loaded configuration, as long as it belongs to the checked console. Otherwise nothing is
    /// compared.</summary>
    private EntryDeskOptions? Reference =>
        _loaded is { } loaded && string.Equals(loaded.Host.Trim(), _host, StringComparison.OrdinalIgnoreCase) ? loaded : null;

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

    /// <summary>Pin to save: the checked fingerprint, for a port that is currently unreachable the previous
    /// one.</summary>
    private string? PinForSave(int port) =>
        Pin(port) ?? (Reference is { } reference && SetupChecks.ConfiguredPin(reference, port) is { Length: > 0 } pin ? pin : null);

    // ---------------------------------------------------------------- 0. Existing configuration

    private async void OnLoadInstalled(object sender, RoutedEventArgs e) => await LoadConfigAsync(InstalledConfigPath);

    private async void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = ConfigFilter, FileName = "sientrydesk.json" };
        if (dialog.ShowDialog(this) == true)
            await LoadConfigAsync(dialog.FileName);
    }

    private async Task LoadConfigAsync(string path)
    {
        EntryDeskOptions options;
        try
        {
            options = SetupChecks.ParseConfigJson(await File.ReadAllTextAsync(path));
        }
        catch (UnauthorizedAccessException)
        {
            ShowLine(LoadResult, Mark.Fail, Tr.Admin(
                $"Kein Zugriff auf {path}. Die installierte Konfiguration ist nur für " +
                "Administratoren lesbar: PowerShell als Administrator öffnen und dort SIEntryDesk.exe --setup starten.",
                $"No access to {path}. The installed configuration is readable only by " +
                "administrators: open PowerShell as administrator and start SIEntryDesk.exe --setup there."));
            return;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            ShowLine(LoadResult, Mark.Fail, Tr.Admin($"Nicht gefunden: {path}", $"Not found: {path}"));
            return;
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or ArgumentException)
        {
            ShowLine(LoadResult, Mark.Fail, Tr.Admin($"{path} ist nicht lesbar: {UntrustedText.Clean(ex.Message, 200)}",
                $"{path} is not readable: {UntrustedText.Clean(ex.Message, 200)}"));
            return;
        }

        _loaded = options;
        _loadedPath = path;
        HostBox.Text = options.Host.Trim();
        LiveViewBox.IsChecked = options.LiveView;
        LiveViewSecondsBox.Text = options.LiveViewSeconds.ToString(CultureInfo.InvariantCulture);
        TokenExpiresPicker.SelectedDate = options.TokenExpires;
        ProtectKeyExpiresPicker.SelectedDate = options.ProtectKeyExpires;
        LanguageDe.IsChecked = options.UiLanguage == UiLanguage.De;
        LanguageEn.IsChecked = options.UiLanguage == UiLanguage.En;
        LanguagePl.IsChecked = options.UiLanguage == UiLanguage.Pl;
        SaveResult.Inlines.Clear();
        var lines = DescribeLoaded(path, options);
        ShowLines(LoadResult, lines);
        await CheckHostAsync();
        // Result of the fingerprints right at the top, the details are under 1. Console.
        if (Reference is not null && _ports.Count > 0)
        {
            lines.Insert(1, PinSummary(options));
            ShowLines(LoadResult, lines);
        }
    }

    private (Mark, string) PinSummary(EntryDeskOptions options)
    {
        var results = _ports.Select(p => (p.Port, State: SetupChecks.ComparePin(p, SetupChecks.ConfiguredPin(options, p.Port)))).ToList();
        string Ports(PinComparison state) => string.Join(", ", results.Where(r => r.State == state).Select(r => r.Port));
        if (results.Any(r => r.State == PinComparison.Mismatch))
            return (Mark.Fail, Tr.Admin($"Fingerabdruck auf Port {Ports(PinComparison.Mismatch)} stimmt nicht mit der Datei überein, Einzelheiten unter 1. Konsole.",
                $"Fingerprint on port {Ports(PinComparison.Mismatch)} does not match the file, details under 1. Console."));
        if (results.Any(r => r.State == PinComparison.Unreachable))
            return (Mark.Warn, Tr.Admin($"Port {Ports(PinComparison.Unreachable)} nicht erreichbar, dort ist der Fingerabdruck nicht geprüft.",
                $"Port {Ports(PinComparison.Unreachable)} not reachable, the fingerprint is not checked there."));
        return (Mark.Ok, Tr.Admin($"Fingerabdrücke aus der Datei stimmen mit der Konsole überein (Port {Ports(PinComparison.Match)}).",
            $"Fingerprints from the file match the console (port {Ports(PinComparison.Match)})."));
    }

    private static List<(Mark, string)> DescribeLoaded(string path, EntryDeskOptions options)
    {
        var lines = new List<(Mark, string)> { (Mark.Info, Tr.Admin($"Geladen: {path}", $"Loaded: {path}")) };
        if (options.Validate() is { } problem)
            lines.Add((Mark.Fail, problem));
        if (options.ValidateLanguage() is { } languageProblem)
            lines.Add((Mark.Warn, languageProblem));
        if (!options.VideoConfigured)
            lines.Add((Mark.Info, Tr.Admin("Ohne ProtectPin: kein Livebild.", "Without ProtectPin: no live view.")));
        else if (options.ValidateVideo() is { } videoProblem)
            lines.Add((Mark.Fail, videoProblem));
        if (options.LiveViewSeconds is < 15 or > 600)
            lines.Add((Mark.Warn, Tr.Admin($"LiveViewSeconds {options.LiveViewSeconds} liegt nicht zwischen 15 und 600 und wird begrenzt.",
                $"LiveViewSeconds {options.LiveViewSeconds} is not between 15 and 600 and is limited.")));

        var dates = new[] { options.TokenExpires, options.ProtectKeyExpires }.OfType<DateTime>().ToList();
        if (options.TokenExpires is null || (options.VideoConfigured && options.ProtectKeyExpires is null))
            lines.Add((Mark.Warn, Tr.Admin("Ablaufdatum fehlt (TokenExpires, ProtectKeyExpires), die Erinnerung vor Ablauf bleibt aus.",
                "Expiry date missing (TokenExpires, ProtectKeyExpires), there is no reminder before expiry.")));
        if (options.ExpiryWarning(DateTime.Today) is { } expiry)
            lines.Add((Mark.Warn, expiry));
        else if (dates.Count > 0)
            lines.Add((Mark.Ok, Tr.Admin($"Zugänge gültig bis {Tr.AdminDate(dates.Min())}", $"Credentials valid until {Tr.AdminDate(dates.Min())}")));

        lines.Add((Mark.Info, Tr.Admin(
            "Access-Token und Protect-Schlüssel stehen nicht in der Datei. Für die Prüfung von Rechten, " +
            "Türen und Kameras unten eingeben und prüfen, Abweichungen von der Datei werden markiert.",
            "Access token and Protect key are not in the file. To check permissions, doors and cameras " +
            "enter them below and check, differences from the file are marked.")));
        return lines;
    }

    // ---------------------------------------------------------------- 1. Console

    private async void OnCheckHost(object sender, RoutedEventArgs e) => await CheckHostAsync();

    private async Task CheckHostAsync()
    {
        var host = HostBox.Text.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            ShowLine(HostResult, Mark.Fail, Tr.Admin("Kein gültiger Name und keine gültige IP.", "Not a valid name and not a valid IP."));
            return;
        }
        ResetAfterHost();
        _host = host;
        HostCheck.IsEnabled = false;
        ShowLine(HostResult, Mark.Info, Checking);
        try
        {
            _ports = await SetupChecks.CheckPortsAsync(host, CancellationToken.None);
        }
        finally
        {
            HostCheck.IsEnabled = true;
        }

        var reference = Reference;
        var lines = _ports.Select(port => DescribePort(port, reference)).ToList();
        var accessOk = Pin(AccessApiClient.Port) is not null;
        if (!accessOk)
            lines.Add((Mark.Info, Tr.Admin("Ohne Port 12445 geht es nicht: Netz, VPN oder Firewall prüfen.",
                "Nothing works without port 12445: check network, VPN or firewall.")));
        else if (Pin(443) is null || Pin(7441) is null)
            lines.Add((Mark.Warn, Tr.Admin("Ohne Port 443 und 7441 gibt es kein Livebild.", "Without ports 443 and 7441 there is no live view.")));
        if (reference is not null && _ports.Any(p => SetupChecks.ComparePin(p, SetupChecks.ConfiguredPin(reference, p.Port)) == PinComparison.Mismatch))
            lines.Add((Mark.Info, Tr.Admin(
                "Die PCs lehnen diese Verbindung ab, solange der Pin nicht passt. Erst klären, ob die Konsole ein " +
                "neues Zertifikat hat. Nur dann bestätigen, speichern und die sientrydesk.json neu verteilen.",
                "The PCs reject this connection as long as the pin does not match. First find out whether the console has a " +
                "new certificate. Only then confirm, save and distribute sientrydesk.json again.")));
        ShowLines(HostResult, lines);
        PinsConfirmed.IsEnabled = accessOk;

        // If the console shows exactly the certificates of the loaded file, there is nothing new to confirm.
        if (reference is not null && SetupChecks.PinsUnchanged(reference, _ports))
            PinsConfirmed.IsChecked = true;
    }

    private static (Mark, string) DescribePort(PortCheck port, EntryDeskOptions? reference)
    {
        var prefix = $"{port.Name}, Port {port.Port}";
        if (!port.Reachable)
            return (Mark.Fail, Tr.Admin($"{prefix}: nicht erreichbar ({port.Problem})", $"{prefix}: not reachable ({port.Problem})"));
        if (reference is null)
            return (Mark.Ok, Tr.Admin($"{prefix}: erreichbar, Zertifikat {port.Fingerprint}", $"{prefix}: reachable, certificate {port.Fingerprint}"));
        return SetupChecks.ComparePin(port, SetupChecks.ConfiguredPin(reference, port.Port)) switch
        {
            PinComparison.Match => (Mark.Ok, Tr.Admin($"{prefix}: erreichbar, Fingerabdruck stimmt mit der Datei überein",
                $"{prefix}: reachable, fingerprint matches the file")),
            PinComparison.Mismatch => (Mark.Fail, Tr.Admin($"{prefix}: Fingerabdruck {port.Fingerprint} stimmt nicht mit der Datei überein",
                $"{prefix}: fingerprint {port.Fingerprint} does not match the file")),
            _ => (Mark.Info, Tr.Admin($"{prefix}: erreichbar, Fingerabdruck {port.Fingerprint}, in der Datei ohne Pin",
                $"{prefix}: reachable, fingerprint {port.Fingerprint}, no pin in the file")),
        };
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
        AccessResult.Inlines.Clear();
        ProtectResult.Inlines.Clear();
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
        ShowLine(AccessResult, Mark.Info, Checking);
        try
        {
            _access?.Dispose();
            _access = new AccessApiClient(_host, token, CertificatePin.Parse(pin));
            _rights = await SetupChecks.CheckAccessTokenAsync(_access, CancellationToken.None);
            _doors = _rights.Doors ? await _access.GetDoorsAsync(CancellationToken.None) : null;

            var lines = new List<(Mark, string)>
            {
                (_rights.Events ? Mark.Ok : Mark.Fail, Tr.Admin("Klingel-Ereignisse (Gerät = Anzeigen)", "Ring events (Device = View)")),
                (_rights.Doors ? Mark.Ok : Mark.Fail, Tr.Admin("Türliste (Standorte = Anzeigen)", "Door list (Location = View)")),
                (_rights.Unlock ? Mark.Ok : Mark.Fail, Tr.Admin("Öffnen (Standorte = Bearbeiten)", "Opening (Location = Edit)")),
                _rights.Excess.Count == 0
                    ? (Mark.Ok, Tr.Admin("Keine überflüssigen Rechte", "No unnecessary permissions"))
                    : (Mark.Warn, Tr.Admin($"Überflüssige Rechte: {string.Join(", ", _rights.Excess)}. Token löschen und neu anlegen, dort „Keinen“.",
                        $"Unnecessary permissions: {string.Join(", ", _rights.Excess)}. Delete the token and create it again, with \"None\" there.")),
            };
            if (_doors is not null)
                lines.Add((Mark.Info, Tr.Admin($"Türen: {string.Join(", ", _doors.Select(d => d.Name))}", $"Doors: {string.Join(", ", _doors.Select(d => d.Name))}")));
            ShowLines(AccessResult, lines);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or AuthenticationException or JsonException)
        {
            ShowLine(AccessResult, Mark.Fail, Tr.Admin($"Keine Antwort von Access: {ex.Message}", $"No answer from Access: {ex.Message}"));
        }
        finally
        {
            AccessCheck.IsEnabled = true;
        }
        BuildDoorRows();
        UpdateSaveButton();
    }

    private static void ShowLine(TextBlock target, Mark mark, string text) => ShowLines(target, [(mark, text)]);

    /// <summary>Result lines with a colored mark: ✓ green, ✗ red, ⚠ orange.</summary>
    private static void ShowLines(TextBlock target, IEnumerable<(Mark Mark, string Text)> lines)
    {
        target.Inlines.Clear();
        var first = true;
        foreach (var (mark, text) in lines)
        {
            if (!first)
                target.Inlines.Add(new LineBreak());
            first = false;
            var (glyph, brush) = mark switch
            {
                Mark.Ok => ("✓ ", OkBrush),
                Mark.Fail => ("✗ ", FailBrush),
                Mark.Warn => ("⚠ ", WarnBrush),
                _ => (string.Empty, (Brush?)null),
            };
            if (glyph.Length > 0)
                target.Inlines.Add(new Run(glyph) { Foreground = brush, FontWeight = FontWeights.Bold });
            target.Inlines.Add(new Run(text));
        }
    }

    // ---------------------------------------------------------------- 3. Protect

    private async void OnCheckProtect(object sender, RoutedEventArgs e)
    {
        var key = ProtectKeyBox.Password.Trim();
        if (key.Length == 0 || Pin(443) is not { } pin)
            return;
        ProtectCheck.IsEnabled = false;
        ShowLine(ProtectResult, Mark.Info, Checking);
        try
        {
            _protect?.Dispose();
            _protect = new ProtectApiClient(_host, key, CertificatePin.Parse(pin));
            var version = await _protect.GetVersionAsync(CancellationToken.None);
            _cameras = await _protect.GetCameraNamesAsync(CancellationToken.None);
            ShowLines(ProtectResult, [(Mark.Ok, $"Protect {version}"),
                (Mark.Info, Tr.Admin($"Kameras: {string.Join(", ", _cameras.Values)}", $"Cameras: {string.Join(", ", _cameras.Values)}"))]);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _cameras = null;
            ShowLine(ProtectResult, Mark.Fail, Tr.Admin("Protect lehnt den Schlüssel ab.", "Protect rejects the key."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or AuthenticationException or JsonException)
        {
            _cameras = null;
            ShowLine(ProtectResult, Mark.Fail, Tr.Admin($"Keine Antwort von Protect: {ex.Message}", $"No answer from Protect: {ex.Message}"));
        }
        finally
        {
            ProtectCheck.IsEnabled = true;
        }
        BuildDoorRows();
        UpdateSaveButton();
    }

    // ---------------------------------------------------------------- 4. Doors and cameras

    private void BuildDoorRows()
    {
        DoorGrid.Children.Clear();
        DoorGrid.RowDefinitions.Clear();
        _rows.Clear();
        DoorsResult.Inlines.Clear();
        DoorsPlaceholder.Visibility = _doors is null ? Visibility.Visible : Visibility.Collapsed;
        if (_doors is null)
            return;

        // With a loaded configuration its mapping applies, suggestions appear only as a hint.
        var reference = Reference;
        var suggestions = reference is not null
            ? reference.DoorCameras
            : _cameras is null
                ? new Dictionary<string, string>()
                : SetupChecks.SuggestDoorCameras(_doors, _cameras);
        var choices = new List<CameraChoice> { new(null, Tr.Admin("(keine Kamera)", "(no camera)")) };
        if (_cameras is not null)
            choices.AddRange(_cameras.Select(c => new CameraChoice(c.Key, c.Value)).OrderBy(c => c.Name));

        foreach (var door in _doors)
        {
            var row = DoorGrid.RowDefinitions.Count;
            DoorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var show = new CheckBox { IsChecked = reference?.AcceptsDoor(door.Id, door.Name) ?? true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 10, 6) };
            var name = new TextBlock { Text = door.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var camera = new ComboBox { ItemsSource = choices, Margin = new Thickness(8, 4, 8, 4), IsEnabled = _cameras is not null };
            camera.SelectedItem = choices.FirstOrDefault(c => c.Id == suggestions.GetValueOrDefault(door.Id)) ?? choices[0];
            var stream = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.DimGray };
            var create = new Button { Content = CreateStreamText, IsEnabled = false };
            var test = new Button { Content = TestLiveViewText, IsEnabled = false };

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

        if (reference is not null)
            ShowLines(DoorsResult, SetupChecks.CompareDoors(reference, _doors, _cameras).Select(f => (f.Level switch
            {
                FindingLevel.Ok => Mark.Ok,
                FindingLevel.Warn => Mark.Warn,
                _ => Mark.Fail,
            }, f.Text)));
    }

    private static string? SelectedCamera(DoorRow row) => (row.Camera.SelectedItem as CameraChoice)?.Id;

    private async Task RefreshStreamAsync(DoorRow row)
    {
        row.Create.IsEnabled = false;
        row.Test.IsEnabled = false;
        if (_protect is null || SelectedCamera(row) is not { } camera)
        {
            row.Stream.Inlines.Clear();
            if (_protect is not null)
                ShowLine(row.Stream, Mark.Info, "–");
            UpdateStreamHint();
            return;
        }
        ShowLine(row.Stream, Mark.Info, Tr.Admin("prüfe …", "checking …"));
        try
        {
            var streams = await _protect.GetStreamsAsync(camera, CancellationToken.None);
            if (streams.Count == 0)
                ShowLine(row.Stream, Mark.Fail, Tr.Admin("kein Stream", "no stream"));
            else
                ShowLine(row.Stream, Mark.Ok, $"Stream: {string.Join(", ", streams.Keys)}");
            row.Create.IsEnabled = streams.Count == 0;
            row.Test.IsEnabled = streams.Count > 0 && Pin(7441) is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or AuthenticationException or JsonException)
        {
            ShowLine(row.Stream, Mark.Warn, Tr.Admin("nicht lesbar", "not readable"));
        }
        UpdateStreamHint();
    }

    /// <summary>Hint where to create a missing stream, as soon as a door with a camera lacks a stream.</summary>
    private void UpdateStreamHint()
    {
        StreamHint.Visibility = _rows.Any(r => r.Create.IsEnabled) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CreateStreamAsync(DoorRow row)
    {
        if (_protect is null || SelectedCamera(row) is not { } camera)
            return;
        var answer = MessageBox.Show(this, Tr.Admin(
                $"Für die Kamera „{row.Camera.SelectedItem}“ einen RTSPS-Stream in Protect anlegen?\n\n" +
                "Das ändert die Protect-Einstellungen dieser Kamera. Die Stream-Adresse wirkt wie ein Zugangsschlüssel " +
                "zum Kamerabild, SI EntryDesk gibt sie nicht an die Benutzer weiter.",
                $"Create an RTSPS stream in Protect for the camera \"{row.Camera.SelectedItem}\"?\n\n" +
                "This changes the Protect settings of this camera. The stream address works like an access key " +
                "to the camera image, SI EntryDesk does not pass it on to the users."),
            CreateStreamText, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        try
        {
            await _protect.CreateStreamAsync(camera, "medium", CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or AuthenticationException or JsonException)
        {
            MessageBox.Show(this, Tr.Admin($"Stream konnte nicht angelegt werden: {ex.Message}", $"Stream could not be created: {ex.Message}"),
                CreateStreamText,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await RefreshStreamAsync(row);
    }

    /// <summary>Plays the live view via the same checked path as the app: stream proxy with pin, LibVLC.</summary>
    private async Task TestLiveViewAsync(DoorRow row)
    {
        if (_protect is null || SelectedCamera(row) is not { } camera || Pin(7441) is not { } streamPin)
            return;
        var streams = await _protect.GetStreamsAsync(camera, CancellationToken.None);
        var source = new[] { "low", "medium", "high" }.Select(q => streams.GetValueOrDefault(q)).FirstOrDefault(s => s is not null);
        var libVlc = await _libVlc;
        if (source is null || libVlc is null)
        {
            MessageBox.Show(this, source is null
                    ? Tr.Admin("Kein Stream vorhanden.", "No stream available.")
                    : Tr.Admin("Der Videoplayer konnte nicht geladen werden.", "The video player could not be loaded."),
                TestLiveViewText, MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private static string CreateStreamText => Tr.Admin("Stream anlegen", "Create stream");

    private static string TestLiveViewText => Tr.Admin("Livebild testen", "Test live view");

    // ---------------------------------------------------------------- 5.–7. Save

    private void UpdateSaveButton() =>
        SaveButton.IsEnabled = PinsConfirmed.IsChecked == true && _rights is { Events: true } && _doors is not null;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LiveViewSecondsBox.Text.Trim(), out var seconds) || seconds is < 15 or > 600)
        {
            ShowLine(SaveResult, Mark.Fail, Tr.Admin("Dauer des Livebilds: eine Zahl von 15 bis 600.", "Duration of the live view: a number from 15 to 600."));
            return;
        }
        var warnings = new List<string>();
        if (_rights is { Complete: false })
            warnings.Add(Tr.Admin("Der Access-Token hat nicht alle nötigen Rechte.", "The Access token does not have all needed permissions."));
        if (_rights is { Excess.Count: > 0 })
            warnings.Add(Tr.Admin("Der Access-Token hat überflüssige Rechte.", "The Access token has unnecessary permissions."));
        if (_cameras is null)
            warnings.Add(Tr.Admin("Der Protect-Schlüssel ist nicht geprüft, das Livebild bleibt ohne ihn aus.",
                "The Protect key is not checked, without it there is no live view."));
        if (warnings.Count > 0 && MessageBox.Show(this, string.Join("\n", warnings) + Tr.Admin("\n\nTrotzdem speichern?", "\n\nSave anyway?"),
                Tr.Admin("Speichern", "Save"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var shown = _rows.Where(r => r.Show.IsChecked == true).Select(r => r.Door.Id).ToList();
        var options = new EntryDeskOptions
        {
            Language = Tr.Code(SelectedLanguage),
            Host = _host,
            AccessPin = Pin(AccessApiClient.Port)!,
            ProtectPin = PinForSave(443) ?? string.Empty,
            StreamPin = PinForSave(7441) ?? string.Empty,
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
            Filter = ConfigFilter,
            OverwritePrompt = true,
        };
        // Suggest next to the loaded file, except for the installed one: the next update overwrites it.
        if (_loadedPath is not null && !string.Equals(_loadedPath, InstalledConfigPath, StringComparison.OrdinalIgnoreCase))
            dialog.InitialDirectory = Path.GetDirectoryName(_loadedPath);
        if (dialog.ShowDialog(this) != true)
            return;
        File.WriteAllText(dialog.FileName, SetupChecks.ToConfigJson(options, DateTime.Now), new UTF8Encoding(false));
        ShowLine(SaveResult, Mark.Ok, Tr.Admin($"Gespeichert: {dialog.FileName}", $"Saved: {dialog.FileName}"));
    }
}

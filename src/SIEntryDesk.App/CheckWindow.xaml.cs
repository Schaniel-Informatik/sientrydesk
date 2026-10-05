using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SIEntryDesk.Core.Diagnostics;
using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.App;

/// <summary>
/// SIEntryDesk.exe --check: grün/orange/rot je Voraussetzung. Die App prüft den PC, der Dienst die Anlage mit seinen
/// gespeicherten Zugängen. Braucht keine Eingaben und keine Adminrechte.
/// </summary>
public partial class CheckWindow : Window
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(60);

    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x7B, 0x00));
    private static readonly Brush FailBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

    private readonly CancellationTokenSource _cts = new();
    private readonly List<CheckItem> _local = [];
    private readonly List<CheckItem> _service = [];
    private bool _servicePending;
    private DateTimeOffset _at;

    public CheckWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync();
    }

    private IEnumerable<CheckItem> Items => _local.Concat(_service);

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }

    private async void OnRun(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var header = $"SI EntryDesk {TrayIcon.AppVersion}, {Environment.MachineName}, {Environment.UserName}";
        Clipboard.SetText(CheckReport.ToText(Items, header, _at));
        CopyButton.Content = "Kopiert";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>PC und Anlage gleichzeitig, jeder Punkt erscheint, sobald er fertig ist.</summary>
    private async Task RunAsync()
    {
        RunButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        CopyButton.Content = "Ergebnis kopieren";
        Summary.Text = "Prüft …";
        Summary.Foreground = InfoBrush;
        _local.Clear();
        _service.Clear();
        _servicePending = true;
        Render();

        var service = AskServiceAsync();
        foreach (var check in LocalChecks.All)
        {
            _local.Add(await Task.Run(check));
            Render();
        }
        await service;
        _servicePending = false;
        _at = DateTimeOffset.Now;
        Render();

        (Summary.Text, Summary.Foreground) = CheckReport.Overall(Items) switch
        {
            CheckLevel.Fail => ("Es gibt Fehler, siehe rote Punkte", FailBrush),
            CheckLevel.Warn => ("Funktioniert, mit Hinweisen", WarnBrush),
            _ => ("Alles in Ordnung", OkBrush),
        };
        RunButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
    }

    /// <summary>Eigene Verbindung zum Dienst, unabhängig von der Tray-App, und die Prüfung der Anlage.</summary>
    private async Task AskServiceAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var client = new ServiceClient(Dispatcher);
        var connected = new TaskCompletionSource<StatusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<CheckResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += status =>
        {
            if (status.Connected && status.Service is { } service)
                connected.TrySetResult(service);
        };
        client.MessageReceived += message =>
        {
            if (message is CheckResultMessage check)
                result.TrySetResult(check);
        };
        _ = client.RunAsync(cts.Token);
        try
        {
            var status = await connected.Task.WaitAsync(ConnectTimeout, cts.Token);
            _service.Add(LocalChecks.Item("Verbindung zum Dienst", CheckLevel.Ok, "steht"));
            _service.Add(status.ServiceVersion == TrayIcon.AppVersion
                ? LocalChecks.Item("Version", CheckLevel.Ok, TrayIcon.AppVersion)
                : LocalChecks.Item("Version", CheckLevel.Warn, $"App {TrayIcon.AppVersion}, Dienst {status.ServiceVersion}. Neu installieren"));
            Render();

            if (!await client.SendAsync(new CheckRequest()))
                throw new TimeoutException();
            var check = await result.Task.WaitAsync(ResultTimeout, cts.Token);
            _service.AddRange(check.Items);
        }
        catch (TimeoutException)
        {
            _service.Add(LocalChecks.Item("Verbindung zum Dienst", CheckLevel.Fail,
                connected.Task.IsCompleted ? "Dienst antwortet nicht auf die Prüfung" : "keine Verbindung, der Dienst läuft nicht oder blockiert"));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Cancel();
        }
    }

    private void Render()
    {
        Show(Items);
        if (_servicePending)
            Results.Children.Add(new TextBlock
            {
                Text = $"{CheckReport.Installation}: wird geprüft …",
                Foreground = InfoBrush,
                Margin = new Thickness(0, 12, 0, 0),
            });
    }

    private void Show(IEnumerable<CheckItem> items)
    {
        Results.Children.Clear();
        foreach (var group in items.GroupBy(i => i.Area))
        {
            Results.Children.Add(new TextBlock { Text = group.Key, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            foreach (var item in group)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
                line.Inlines.Add(new Run(CheckReport.Mark(item.Level) + " ")
                {
                    Foreground = item.Level switch
                    {
                        CheckLevel.Ok => OkBrush,
                        CheckLevel.Warn => WarnBrush,
                        CheckLevel.Fail => FailBrush,
                        _ => InfoBrush,
                    },
                    FontWeight = FontWeights.Bold,
                });
                line.Inlines.Add(new Run(item.Name + ": ") { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new Run(item.Detail));
                Results.Children.Add(line);
            }
        }
    }
}

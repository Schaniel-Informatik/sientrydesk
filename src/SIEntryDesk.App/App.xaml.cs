using System.Windows;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.App;

public partial class App : Application
{
    private readonly Dictionary<string, RingWindow> _windows = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Mutex? _singleInstance;
    private ServiceClient? _client;
    private TrayIcon? _tray;
    private Ringtone? _ringtone;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\SIEntryDesk.App", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        _ringtone = new Ringtone();
        _tray = new TrayIcon(ShowTestRing, Shutdown);
        _client = new ServiceClient(Dispatcher);
        _client.StatusChanged += status => _tray.SetStatus(status);
        _client.MessageReceived += OnMessage;
        _ = _client.RunAsync(_cts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        foreach (var window in _windows.Values.ToList())
            window.Close();
        _ringtone?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnMessage(IpcMessage message)
    {
        switch (message)
        {
            case CallStartedMessage call:
                ShowRing(call.CallId, call.DoorName, call.StartedAt, call.UnlockAllowed, isTest: false);
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
        }
    }

    private void ShowRing(string callId, string doorName, DateTimeOffset startedAt, bool unlockAllowed, bool isTest)
    {
        if (_windows.ContainsKey(callId))
            return;

        var window = new RingWindow(callId, doorName, startedAt, unlockAllowed, isTest);
        window.UnlockRequested += async () =>
        {
            if (_client is null || !await _client.SendAsync(new UnlockRequest(callId)))
                window.ShowUnlockResult(false, "Dienst nicht erreichbar");
        };
        window.RingingChanged += UpdateRingtone;
        window.Closed += (_, _) =>
        {
            _windows.Remove(callId);
            UpdateRingtone();
        };

        // Mittig auf dem Hauptbildschirm, weitere Rufe leicht versetzt.
        var area = SystemParameters.WorkArea;
        var offset = _windows.Count * 30;
        window.Left = area.Left + (area.Width - window.Width) / 2 + offset;
        window.Top = area.Top + (area.Height - window.Height) / 2 + offset;

        _windows[callId] = window;
        window.Show();
        UpdateRingtone();
        _tray?.Notify("Es klingelt", window.DoorText.Text);
    }

    private void ShowTestRing()
    {
        var callId = "test-" + Guid.NewGuid().ToString("N");
        ShowRing(callId, "Testklingeln", DateTimeOffset.Now, unlockAllowed: false, isTest: true);
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

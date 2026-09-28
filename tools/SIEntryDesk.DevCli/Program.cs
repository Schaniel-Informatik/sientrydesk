// Entwicklerwerkzeug: lauscht mit der Core-Logik auf Klingelrufe, wie es der Dienst tut.
// Zugangsdaten nur aus Umgebungsvariablen (op run), siehe tools/feasibility/README.md:
//   SIED_HOST, SIED_ACCESS_TOKEN, SIED_PIN_ACCESS, optional SIED_DOORS (kommagetrennt)
// Aufruf: dotnet run --project tools/SIEntryDesk.DevCli -- listen [minuten]

using Microsoft.Extensions.Logging;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Security;

if (args is not ["listen", ..])
{
    Console.Error.WriteLine("Aufruf: SIEntryDesk.DevCli listen [minuten]");
    return 2;
}
var minutes = args.Length > 1 && double.TryParse(args[1], out var m) ? m : 5;

string Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim()
        : throw new InvalidOperationException($"Umgebungsvariable {name} fehlt");

var options = new EntryDeskOptions
{
    Host = Env("SIED_HOST"),
    AccessPin = Env("SIED_PIN_ACCESS"),
    Doors = (Environment.GetEnvironmentVariable("SIED_DOORS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
};
if (options.Validate() is { } problem)
{
    Console.Error.WriteLine($"Einstellungen: {problem}");
    return 2;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
}));
var log = loggerFactory.CreateLogger("DevCli");
var tracker = new CallTracker(TimeProvider.System, options.AcceptsDoor);
var stream = new AccessEventStream(options.Host, Env("SIED_ACCESS_TOKEN"), CertificatePin.Parse(options.AccessPin), log);

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

void Report(IEnumerable<CallChange> changes)
{
    foreach (var change in changes)
    {
        var text = change switch
        {
            CallStarted s => $">>> KLINGELN  {s.Call.DoorName}  Ruf={s.Call.CallId}  Öffnen erlaubt={s.Call.UnlockAllowed}  Kamera={s.Call.CameraId}",
            CallEnded e => $"<<< ENDE      {e.Call.DoorName}  Ruf={e.Call.CallId}  {CallEndReasons.ToGerman(e.Reason)}",
            CallDoorOpened o => $"*** GEÖFFNET  {o.Call.DoorName}  Ruf={o.Call.CallId}  von={o.OpenedBy ?? "?"}",
            _ => change.ToString(),
        };
        log.LogInformation("{Text}", text);
    }
}

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
var expiry = Task.Run(async () =>
{
    try
    {
        while (await timer.WaitForNextTickAsync(cts.Token))
            Report(tracker.Expire());
    }
    catch (OperationCanceledException) { }
});

var heartbeats = 0;
log.LogInformation("Lausche {Minutes} min auf {Host}", minutes, options.Host);
await stream.RunAsync(
    (ev, _) =>
    {
        if (ev is AccessHeartbeat)
            heartbeats++;
        Report(tracker.Apply(ev));
        return ValueTask.CompletedTask;
    },
    connected => log.LogInformation("Verbindung: {State}", connected ? "verbunden" : "getrennt"),
    cts.Token);
await expiry;
log.LogInformation("Beendet, {Count} Lebenszeichen empfangen", heartbeats);
return 0;

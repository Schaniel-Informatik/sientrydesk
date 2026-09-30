// Entwicklerwerkzeug: lauscht mit der Core-Logik auf Klingelrufe, wie es der Dienst tut.
// Zugangsdaten nur aus Umgebungsvariablen (op run), siehe tools/feasibility/README.md:
//   SIED_HOST, SIED_ACCESS_TOKEN, SIED_PIN_ACCESS, optional SIED_DOORS (kommagetrennt)
// Aufruf: dotnet run --project tools/SIEntryDesk.DevCli -- listen [minuten]
//         dotnet run --project tools/SIEntryDesk.DevCli -- video <kamera-id> [--play]
//         (zusätzlich SIED_PROTECT_KEY, SIED_PIN_PROTECT, SIED_PIN_RTSPS)

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Video;

if (args is ["video", var cameraId, ..])
    return await VideoTestAsync(cameraId, args.Contains("--play"));

if (args is not ["listen", ..])
{
    Console.Error.WriteLine("Aufruf: SIEntryDesk.DevCli listen [minuten] | video <kamera-id> [--play]");
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
    link => log.LogInformation("Verbindung: {State}", link),
    cts.Token);
await expiry;
log.LogInformation("Beendet, {Count} Lebenszeichen empfangen", heartbeats);
return 0;

// Holt die Stream-Adresse über die Protect-API, öffnet eine Einmal-Adresse im Proxy und prüft sie mit ffprobe.
static async Task<int> VideoTestAsync(string cameraId, bool play)
{
    static string Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim()
            : throw new InvalidOperationException($"Umgebungsvariable {name} fehlt");

    using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    }));
    var log = loggerFactory.CreateLogger("DevCli");
    var host = Get("SIED_HOST");

    using var protect = new ProtectApiClient(host, Get("SIED_PROTECT_KEY"), CertificatePin.Parse(Get("SIED_PIN_PROTECT")));
    var streams = await protect.GetStreamsAsync(cameraId, CancellationToken.None);
    log.LogInformation("Streams: {Qualities}", streams.Count == 0 ? "keine" : string.Join(", ", streams.Keys));
    var source = new[] { "low", "medium", "high" }.Select(q => streams.GetValueOrDefault(q)).FirstOrDefault(s => s is not null);
    if (source is null)
        return 1;

    await using var proxy = new StreamProxy(CertificatePin.Parse(Get("SIED_PIN_RTSPS")), log, TimeProvider.System);
    proxy.Start();
    var url = proxy.Open("test", source, TimeSpan.FromMinutes(2));
    log.LogInformation("Einmal-Adresse: {Url}", url);

    var watch = Stopwatch.StartNew();
    // Mit --udp ohne erzwungenes TCP, wie ein Player, der zuerst UDP versucht.
    string[] transport = Environment.GetCommandLineArgs().Contains("--udp") ? ["-v", "debug"] : ["-v", "error", "-rtsp_transport", "tcp"];
    var probe = Process.Start(new ProcessStartInfo("ffprobe",
        [.. transport, "-show_entries", "stream=codec_type,codec_name,width,height,sample_rate", "-of", "compact", url])
    { RedirectStandardOutput = true, RedirectStandardError = true })!;
    var output = await probe.StandardOutput.ReadToEndAsync();
    var errors = await probe.StandardError.ReadToEndAsync();
    await probe.WaitForExitAsync();
    log.LogInformation("ffprobe über den Proxy: Code {Code} nach {Seconds:0.0} s\n{Output}{Errors}",
        probe.ExitCode, watch.Elapsed.TotalSeconds, output, errors);

    if (Environment.GetCommandLineArgs().Contains("--decode"))
    {
        // 15 s vollständig dekodieren: zeigt, ob Bild und Ton über den Proxy dauerhaft flüssig ankommen.
        var decode = Process.Start(new ProcessStartInfo("ffmpeg",
            ["-hide_banner", "-nostats", "-loglevel", "info", "-rtsp_transport", "tcp", "-i", url, "-t", "15",
             "-map", "0", "-f", "null", "-"]) { RedirectStandardError = true })!;
        var decodeLog = await decode.StandardError.ReadToEndAsync();
        await decode.WaitForExitAsync();
        var summary = decodeLog.Split('\n').Where(l => l.Contains("frame=") || l.Contains("video:") || l.Contains("Error") || l.Contains("error"));
        log.LogInformation("ffmpeg 15 s: Code {Code}\n{Summary}", decode.ExitCode, string.Join('\n', summary.TakeLast(4)));
    }

    if (play)
    {
        log.LogInformation("ffplay für 20 s");
        using var player = Process.Start(new ProcessStartInfo("ffplay",
            ["-hide_banner", "-loglevel", "error", "-fflags", "nobuffer", "-flags", "low_delay", "-framedrop",
             "-rtsp_transport", "tcp", "-window_title", "SI EntryDesk Proxy-Test", url]))!;
        await Task.Delay(TimeSpan.FromSeconds(20));
        if (!player.HasExited)
            player.Kill();
    }

    // Nach dem Schliessen muss die Adresse ungültig sein.
    proxy.Close("test");
    var after = Process.Start(new ProcessStartInfo("ffprobe", ["-v", "error", "-rtsp_transport", "tcp", url])
        { RedirectStandardError = true })!;
    var afterErrors = await after.StandardError.ReadToEndAsync();
    await after.WaitForExitAsync();
    log.LogInformation("Nach dem Schliessen: Code {Code} {Errors}", after.ExitCode, afterErrors.Trim());
    return probe.ExitCode;
}

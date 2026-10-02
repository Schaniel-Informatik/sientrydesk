// Entwicklerwerkzeug: lauscht mit der Core-Logik auf Klingelrufe, wie es der Dienst tut.
// Zugangsdaten nur aus Umgebungsvariablen (op run), siehe tools/feasibility/README.md:
//   SIED_HOST, SIED_ACCESS_TOKEN, SIED_PIN_ACCESS, optional SIED_DOORS (kommagetrennt)
// Aufruf: dotnet run --project tools/SIEntryDesk.DevCli -- listen [minuten]
//         dotnet run --project tools/SIEntryDesk.DevCli -- video <kamera-id> [--play]
//         dotnet run --project tools/SIEntryDesk.DevCli -- talk <kamera-id> [sekunden]
//         (zusätzlich SIED_PROTECT_KEY, SIED_PIN_PROTECT, SIED_PIN_RTSPS)

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Setup;
using SIEntryDesk.Core.Video;
using SIEntryDesk.Audio;
using SIEntryDesk.Core.Talk;

if (args is ["talk", var talkCamera, ..])
    return await TalkTestAsync(talkCamera, args.Length > 2 && int.TryParse(args[2], out var s) ? Math.Clamp(s, 1, 10) : 3);

if (args is ["video", var cameraId, ..])
    return await VideoTestAsync(cameraId, args.Contains("--play"));

if (args is ["setup-check"])
    return await SetupCheckAsync();

if (args is not ["listen", ..])
{
    Console.Error.WriteLine("Aufruf: SIEntryDesk.DevCli listen [minuten] | video <kamera-id> [--play] | talk <kamera-id> [sekunden] | setup-check");
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

// Gegensprechen ohne Windows: Talkback-Sitzung wie der Dienst, Testton mit derselben Opus- und RTP-Umsetzung wie die App.
// Nur mit jemandem an der Tür, der sagt, ob der Ton ankommt.
static async Task<int> TalkTestAsync(string cameraId, int seconds)
{
    static string Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim()
            : throw new InvalidOperationException($"Umgebungsvariable {name} fehlt");

    using var protect = new ProtectApiClient(Get("SIED_HOST"), Get("SIED_PROTECT_KEY"), CertificatePin.Parse(Get("SIED_PIN_PROTECT")));
    var problem = string.Empty;
    var target = await protect.CreateTalkbackSessionAsync(cameraId, p => problem = p, CancellationToken.None);
    if (target is null)
    {
        Console.Error.WriteLine($"Keine brauchbare Talkback-Sitzung: {problem}");
        return 1;
    }
    Console.WriteLine($"Talkback: Türstation {target.Address}:{target.Port}, Opus {target.SamplingRate} Hz");

    var encoder = new OpusVoiceEncoder(target.SamplingRate);
    var rtp = new RtpPacketizer();
    using var udp = new System.Net.Sockets.UdpClient(target.Address.AddressFamily);
    udp.Connect(target.EndPoint);

    // Zwei Töne im Wechsel (660/880 Hz, halbe Lautstärke), deutlich als Test erkennbar.
    var frame = new float[encoder.FrameSamples];
    var frames = seconds * 50;
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
    var watch = Stopwatch.StartNew();
    long bytes = 0;
    for (var n = 0; n < frames; n++)
    {
        var hz = n / 25 % 2 == 0 ? 660f : 880f;
        for (var i = 0; i < frame.Length; i++)
        {
            var t = (n * frame.Length + i) / (float)target.SamplingRate;
            frame[i] = 0.5f * MathF.Sin(2 * MathF.PI * hz * t);
        }
        var packet = rtp.Next(encoder.Encode(frame));
        bytes += packet.Length;
        udp.Send(packet);
        await timer.WaitForNextTickAsync();
    }
    Console.WriteLine($"{frames} Pakete ({bytes / 1024.0:F1} KB) in {watch.Elapsed.TotalSeconds:F1} s gesendet. Ton an der Tür gehört?");
    return 0;
}

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


// Die Prüfungen des Einrichtungsassistenten gegen eine echte Konsole, alle lesend. Ausgabe ohne Geheimnisse.
static async Task<int> SetupCheckAsync()
{
    static string Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim()
            : throw new InvalidOperationException($"Umgebungsvariable {name} fehlt");
    var host = Get("SIED_HOST");
    var ct = CancellationToken.None;

    Console.WriteLine("== 1. Konsole und Zertifikate");
    var ports = await SetupChecks.CheckPortsAsync(host, ct);
    foreach (var p in ports)
        Console.WriteLine($"  {p.Name,-20} Port {p.Port,5}: {(p.Reachable ? "erreichbar" : "NICHT erreichbar")} {p.Fingerprint?[..17]}… {p.Problem}");
    var accessPin = ports.First(p => p.Port == AccessApiClient.Port).Fingerprint!;
    var protectPin = ports.First(p => p.Port == 443).Fingerprint!;
    Console.WriteLine($"  Access-Pin entspricht Konfiguration: {CertificatePin.Parse(accessPin).ToString() == CertificatePin.Parse(Get("SIED_PIN_ACCESS")).ToString()}");

    Console.WriteLine("== 2. Access-Token");
    using var access = new AccessApiClient(host, Get("SIED_ACCESS_TOKEN"), CertificatePin.Parse(accessPin));
    var rights = await SetupChecks.CheckAccessTokenAsync(access, ct);
    Console.WriteLine($"  Ereignisse (Gerät anzeigen): {rights.Events} | Türen (Standorte anzeigen): {rights.Doors} | Öffnen (Standorte bearbeiten): {rights.Unlock}");
    Console.WriteLine($"  Überflüssige Rechte: {(rights.Excess.Count == 0 ? "keine" : string.Join(", ", rights.Excess))} | vollständig und minimal: {rights.Minimal}");
    var doors = await access.GetDoorsAsync(ct);
    Console.WriteLine($"  Türen: {string.Join(", ", doors.Select(d => d.Name))}");

    Console.WriteLine("== 3. Protect-Schlüssel");
    using var protect = new ProtectApiClient(host, Get("SIED_PROTECT_KEY"), CertificatePin.Parse(protectPin));
    Console.WriteLine($"  Protect {await protect.GetVersionAsync(ct)}");
    var cameras = await protect.GetCameraNamesAsync(ct);
    Console.WriteLine($"  Kameras: {string.Join(", ", cameras.Values)}");

    Console.WriteLine("== 4. Vorschlag Türen → Kameras");
    var suggestions = SetupChecks.SuggestDoorCameras(doors, cameras);
    foreach (var door in doors)
    {
        var cam = suggestions.GetValueOrDefault(door.Id);
        var streams = cam is null ? null : await protect.GetStreamsAsync(cam, ct);
        Console.WriteLine($"  {door.Name,-28} → {(cam is null ? "(keine Zuordnung)" : cameras[cam]),-22} Stream: {(streams is null ? "-" : streams.Count == 0 ? "fehlt" : string.Join(",", streams.Keys))}");
    }
    return rights.Complete ? 0 : 1;
}

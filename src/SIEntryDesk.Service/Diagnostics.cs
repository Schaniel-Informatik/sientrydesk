using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SIEntryDesk.Core;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Diagnostics;
using SIEntryDesk.Core.Ipc;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Setup;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Service;

/// <summary>
/// Check for SIEntryDesk.exe --check: configuration, credentials and connections to the installation, with the stored
/// credentials. Runs even if the service failed at startup. Read-only calls only, the unlock permission is checked on a
/// door that does not exist. The result contains no secrets.
/// </summary>
internal sealed class Diagnostics(
    IOptions<EntryDeskOptions> options, ServiceState state, Func<DoorDirectory?> doors, ILogger log)
{
    private static readonly TimeSpan PortTimeout = TimeSpan.FromSeconds(8);
    private readonly SemaphoreSlim _running = new(1, 1);

    public async Task<CheckResultMessage> RunAsync(string user, CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
            return new CheckResultMessage([Item(Tr.Admin("Prüfung", "Check"), CheckLevel.Info,
                Tr.Admin("Läuft bereits, gleich noch einmal versuchen", "Already running, try again in a moment"))]);
        try
        {
            var items = await CheckAsync(ct).ConfigureAwait(false);
            log.LogInformation(Tr.Admin("Prüfung für {User}: {Fail} rot, {Warn} orange", "Check for {User}: {Fail} red, {Warn} orange"),
                user, items.Count(i => i.Level == CheckLevel.Fail), items.Count(i => i.Level == CheckLevel.Warn));
            return new CheckResultMessage(items.ToArray());
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<List<CheckItem>> CheckAsync(CancellationToken ct)
    {
        var items = new List<CheckItem>();
        var opt = options.Value;

        // Configuration
        if (!File.Exists(ServicePaths.ConfigFile) && !File.Exists(ServicePaths.LegacyConfigFile))
        {
            items.Add(Item(ConfigName, CheckLevel.Fail, Tr.Admin($"{ServicePaths.ConfigFile} fehlt. install.ps1 mit sientrydesk.json ausführen",
                $"{ServicePaths.ConfigFile} is missing. Run install.ps1 with sientrydesk.json")));
            return items;
        }
        if (opt.Validate() is { } problem)
        {
            items.Add(Item(ConfigName, CheckLevel.Fail, Tr.Admin($"{problem}. sientrydesk.json mit SIEntryDesk.exe --setup prüfen",
                $"{problem}. Check sientrydesk.json with SIEntryDesk.exe --setup")));
            return items;
        }
        var host = opt.Host.Trim();
        items.Add(Item(ConfigName, CheckLevel.Ok, Tr.Admin($"Konsole {host}", $"Console {host}")));
        items.Add(opt.ValidateLanguage() is { } languageProblem
            ? Item(Tr.Admin("Sprache", "Language"), CheckLevel.Warn, languageProblem)
            : Item(Tr.Admin("Sprache", "Language"), CheckLevel.Info, Tr.Admin(
                $"Benutzer {Tr.Code(Tr.Language)}, Prüfung und Protokoll {Tr.Code(Tr.AdminLanguage)}",
                $"users {Tr.Code(Tr.Language)}, check and log {Tr.Code(Tr.AdminLanguage)}")));
        items.AddRange(ConfigFiles());

        // Credentials
        Secrets? secrets = null;
        try
        {
            secrets = SecretStore.Load();
            items.Add(secrets.ProtectKey is null
                ? Item(CredentialsName, opt.VideoConfigured ? CheckLevel.Warn : CheckLevel.Ok,
                    Tr.Admin("gespeichert, ohne Protect-Schlüssel (kein Livebild, kein Gegensprechen)",
                        "stored, without Protect key (no live view, no talkback)"))
                : Item(CredentialsName, CheckLevel.Ok, Tr.Admin("Access-Token und Protect-Schlüssel gespeichert", "Access token and Protect key stored")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       or JsonException or InvalidDataException)
        {
            items.Add(Item(CredentialsName, CheckLevel.Fail, Tr.Admin("fehlen oder nicht lesbar. set-tokens.ps1 bzw. Paket der Zugänge erneut ausführen",
                "missing or not readable. Run set-tokens.ps1 or the credentials package again")));
        }

        // All ports at the same time: if the console is unreachable, the check takes at most one PortTimeout this way.
        var accessPin = CertificatePin.Parse(opt.AccessPin);
        var video = opt.VideoConfigured && opt.ValidateVideo() is null;
        var protectPin = video ? CertificatePin.Parse(opt.ProtectPin) : null;
        var accessPort = PortAsync("Access (Port 12445)", host, AccessApiClient.Port, accessPin, ct);
        var protectPort = video ? PortAsync("Protect (Port 443)", host, 443, protectPin!, ct) : null;
        var streamPort = video ? PortAsync(Tr.Admin("Videostream (Port 7441)", "Video stream (port 7441)"), host, 7441,
            CertificatePin.Parse(opt.EffectiveStreamPin), ct) : null;
        var ports = new[] { accessPort, protectPort, streamPort }.OfType<Task<PortResult>>().ToArray();
        await Task.WhenAll(ports).ConfigureAwait(false);
        var unreachable = ports.All(p => p.Result.Unreachable);
        if (unreachable)
        {
            var numbers = video ? "12445, 443, 7441" : "12445";
            items.Add(Item(Tr.Admin("Konsole", "Console"), CheckLevel.Fail, Tr.Admin($"{host} nicht erreichbar (Port {numbers}). Firmennetz oder VPN, Firewall prüfen",
                $"{host} not reachable (port {numbers}). Check company network or VPN, firewall")));
        }

        // Access
        if (!unreachable)
            items.Add(accessPort.Result.Item);
        if (accessPort.Result.Ok && secrets is not null)
            await AccessTokenAsync(items, host, secrets.AccessToken, accessPin, ct).ConfigureAwait(false);

        // With an unreachable console the "Konsole" line already says it all.
        if (!(unreachable && state.Health == LinkHealth.Unreachable))
            items.Add(state.Health switch
            {
                LinkHealth.Ready => Item(EventsName, CheckLevel.Ok, Tr.Admin("verbunden, dieser PC klingelt", "connected, this PC rings")),
                LinkHealth.Starting => Item(EventsName, CheckLevel.Warn, Tr.Admin("Dienst verbindet sich noch, in einer Minute erneut prüfen",
                    "service is still connecting, check again in a minute")),
                _ => Item(EventsName, CheckLevel.Fail, state.Status().Problem),
            });

        // Protect: live view and talkback
        if (!opt.VideoConfigured)
        {
            items.Add(Item(LiveViewName, CheckLevel.Info, Tr.Admin("nicht eingerichtet (kein ProtectPin in der sientrydesk.json)",
                "not set up (no ProtectPin in sientrydesk.json)")));
        }
        else if (opt.ValidateVideo() is { } videoProblem)
        {
            items.Add(Item(LiveViewName, CheckLevel.Fail, videoProblem));
        }
        else if (!unreachable)
        {
            items.Add(protectPort!.Result.Item);
            items.Add(streamPort!.Result.Item);
            if (protectPort.Result.Ok && secrets?.ProtectKey is { } key)
                await ProtectAsync(items, host, key, protectPin!, opt, ct).ConfigureAwait(false);
        }

        var talk = Tr.Admin("Gegensprechen", "Talkback");
        items.Add(!opt.Talkback
            ? Item(talk, CheckLevel.Info, Tr.Admin("auf diesem PC abgeschaltet", "switched off on this PC"))
            : state.TalkEnabled
                ? Item(talk, CheckLevel.Ok, Tr.Admin("eingerichtet. Ob der Ton an der Tür ankommt, zeigt nur ein Test mit jemandem dort",
                    "set up. Whether the sound arrives at the door only a test with someone there shows"))
                : Item(talk, CheckLevel.Warn, Tr.Admin("braucht Protect-Schlüssel und ProtectPin", "needs Protect key and ProtectPin")));

        // The firewall rule is checked by the app. Here only whether the service itself receives.
        if (state.PeerProblem() is { } peerProblem)
            items.Add(new CheckItem(CheckReport.ThisPc, Tr.Admin("Abstimmung mit anderen PCs", "Coordination with other PCs"), CheckLevel.Warn, peerProblem));

        items.Add(Expiry(opt));
        return items;
    }

    private sealed record PortResult(CheckItem Item, bool Ok, bool Unreachable);

    /// <summary>Reachability and fingerprint. A different certificate means: new certificate or wrong
    /// console.</summary>
    private static async Task<PortResult> PortAsync(string name, string host, int port, CertificatePin pin, CancellationToken ct)
    {
        try
        {
            var fingerprint = await CertificateProbe.FetchFingerprintAsync(host, port, PortTimeout, ct).ConfigureAwait(false);
            if (CertificatePin.Parse(fingerprint).ToString() == pin.ToString())
                return new PortResult(Item(name, CheckLevel.Ok, Tr.Admin("erreichbar, Zertifikat passt zum Pin", "reachable, certificate matches the pin")),
                    true, false);
            return new PortResult(Item(name, CheckLevel.Fail, Tr.Admin(
                $"Zertifikat passt nicht zum Pin (erhalten {fingerprint}). Neues Zertifikat auf der Konsole? Dann Pin anpassen",
                $"Certificate does not match the pin (received {fingerprint}). New certificate on the console? Then adjust the pin")),
                false, false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or AuthenticationException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return new PortResult(Item(name, CheckLevel.Fail, Tr.Admin("nicht erreichbar. Firmennetz oder VPN, Firewall prüfen",
                "not reachable. Check company network or VPN, firewall")), false, true);
        }
    }

    /// <summary>Which version of the configuration the service runs with. It reads it only at startup, a newer file
    /// only applies after a restart of the service.</summary>
    private IEnumerable<CheckItem> ConfigFiles()
    {
        foreach (var (path, loaded) in state.ConfigLoaded)
        {
            var name = Path.GetFileName(path);
            var current = ServiceState.LastWrite(path);
            if (loaded is null && current is null)
                continue;
            if (loaded == current)
                yield return Item(name, CheckLevel.Ok, Tr.Admin($"vom {Stamp(loaded!.Value)}, gilt seit dem Start des Dienstes",
                    $"of {Stamp(loaded!.Value)}, in effect since the service started"));
            else if (loaded is null)
                yield return Item(name, CheckLevel.Warn, Tr.Admin(
                    $"seit dem Start des Dienstes neu ({Stamp(current!.Value)}). Gilt erst nach einem Neustart des Dienstes oder des PCs",
                    $"new since the service started ({Stamp(current!.Value)}). Takes effect only after restarting the service or the PC"));
            else if (current is null)
                yield return Item(name, CheckLevel.Warn, Tr.Admin(
                    $"seit dem Start des Dienstes gelöscht, er läuft noch mit der Fassung vom {Stamp(loaded.Value)}. Dienst neu starten",
                    $"deleted since the service started, it still runs with the version of {Stamp(loaded.Value)}. Restart the service"));
            else
                yield return Item(name, CheckLevel.Warn, Tr.Admin(
                    $"am {Stamp(current.Value)} geändert, der Dienst läuft noch mit der Fassung vom {Stamp(loaded.Value)}. Dienst oder PC neu starten",
                    $"changed on {Stamp(current.Value)}, the service still runs with the version of {Stamp(loaded.Value)}. Restart the service or the PC"));
        }
    }

    private static string Stamp(DateTime time) => $"{Tr.AdminDate(time)} {time.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    private static async Task AccessTokenAsync(
        List<CheckItem> items, string host, string token, CertificatePin pin, CancellationToken ct)
    {
        try
        {
            using var access = new AccessApiClient(host, token, pin);
            var rights = await SetupChecks.CheckAccessTokenAsync(access, ct).ConfigureAwait(false);
            if (!rights.Events && !rights.Doors && !rights.Unlock)
            {
                items.Add(Item("Access-Token", CheckLevel.Fail, Tr.Admin("abgelehnt oder ohne Rechte. Neuen Token anlegen (Gerät = Anzeigen, Standorte = Bearbeiten)",
                    "rejected or without permissions. Create a new token (Device = View, Location = Edit)")));
                return;
            }
            var missing = new[]
                {
                    (rights.Events, Tr.Admin("Gerät = Anzeigen", "Device = View")),
                    (rights.Doors, Tr.Admin("Standorte = Anzeigen", "Location = View")),
                    (rights.Unlock, Tr.Admin("Standorte = Bearbeiten", "Location = Edit")),
                }
                .Where(r => !r.Item1).Select(r => r.Item2).ToList();
            items.Add(missing.Count == 0
                ? Item("Access-Token", CheckLevel.Ok, Tr.Admin("gültig, Rechte für Klingeln, Türen und Öffnen", "valid, permissions for ringing, doors and opening"))
                : Item("Access-Token", CheckLevel.Fail, Tr.Admin($"es fehlt: {string.Join(", ", missing)}", $"missing: {string.Join(", ", missing)}")));
            if (rights.Excess.Count > 0)
                items.Add(Item("Access-Token", CheckLevel.Warn, Tr.Admin(
                    $"überflüssige Rechte: {string.Join(", ", rights.Excess)}. Token neu anlegen, dort „Keinen“",
                    $"unnecessary permissions: {string.Join(", ", rights.Excess)}. Create the token again, with \"None\" there")));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            if (ct.IsCancellationRequested)
                throw;
            items.Add(Item("Access-Token", CheckLevel.Fail, Tr.Admin("Access antwortet nicht", "Access does not answer")));
        }
    }

    private async Task ProtectAsync(
        List<CheckItem> items, string host, string key, CertificatePin pin, EntryDeskOptions opt, CancellationToken ct)
    {
        try
        {
            using var protect = new ProtectApiClient(host, key, pin);
            var version = await protect.GetVersionAsync(ct).ConfigureAwait(false);
            items.Add(Item(ProtectKeyName, CheckLevel.Ok, Tr.Admin($"gültig, Protect {version}", $"valid, Protect {version}")));

            var cameras = await protect.GetCameraNamesAsync(ct).ConfigureAwait(false);
            var known = (doors()?.All() ?? []).Where(d => opt.AcceptsDoor(d.DoorId, d.DoorName)).ToList();
            if (known.Count == 0)
                items.Add(Item(LiveViewName, CheckLevel.Warn, Tr.Admin("noch keine Tür bekannt. DoorCameras in der sientrydesk.json oder einmal klingeln",
                    "no door known yet. DoorCameras in sientrydesk.json, or ring once")));
            foreach (var door in known)
                items.Add(await DoorStreamAsync(protect, door, cameras, ct).ConfigureAwait(false));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            items.Add(Item(ProtectKeyName, CheckLevel.Fail, Tr.Admin("abgelehnt. Neuen Schlüssel anlegen und mit set-tokens.ps1 bzw. Paket der Zugänge setzen",
                "rejected. Create a new key and set it with set-tokens.ps1 or the credentials package")));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            if (ct.IsCancellationRequested)
                throw;
            items.Add(Item(ProtectKeyName, CheckLevel.Fail, Tr.Admin("Protect antwortet nicht", "Protect does not answer")));
        }
    }

    /// <summary>One door at a time, so that a wrong camera ID does not hide the other doors.</summary>
    private async Task<CheckItem> DoorStreamAsync(
        ProtectApiClient protect, KnownDoor door, IReadOnlyDictionary<string, string> cameras, CancellationToken ct)
    {
        var doorName = door.DoorName.Length > 0 ? door.DoorName : Tr.Admin("Tür", "Door");
        if (door.CameraId.Length == 0)
            return Item($"{LiveViewName} {doorName}", CheckLevel.Warn, Tr.Admin("Kamera unbekannt, wird beim ersten Klingeln gelernt",
                "camera unknown, learned at the first ring"));
        if (!cameras.TryGetValue(door.CameraId, out var cameraName))
            return Item($"{LiveViewName} {doorName}", CheckLevel.Fail, Tr.Admin(
                $"Kamera {door.CameraId} gibt es in Protect nicht. DoorCameras in der sientrydesk.json prüfen",
                $"camera {door.CameraId} does not exist in Protect. Check DoorCameras in sientrydesk.json"));
        var name = $"{LiveViewName} {cameraName}";
        try
        {
            var streams = await protect.GetStreamsAsync(door.CameraId, ct).ConfigureAwait(false);
            return streams.Count > 0
                ? Item(name, CheckLevel.Ok, Tr.Admin($"RTSPS-Stream vorhanden ({string.Join(", ", streams.Keys)})",
                    $"RTSPS stream available ({string.Join(", ", streams.Keys)})"))
                : Item(name, CheckLevel.Fail, Tr.Admin("kein RTSPS-Stream. In Protect bei der Kamera einschalten (Qualität Mittel)",
                    "no RTSPS stream. Switch it on in Protect for the camera (quality Medium)"));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden))
        {
            return Item(name, CheckLevel.Fail, Tr.Admin($"Stream nicht lesbar (Protect meldet {(int?)ex.StatusCode})",
                $"stream not readable (Protect reports {(int?)ex.StatusCode})"));
        }
    }

    private static CheckItem Expiry(EntryDeskOptions opt)
    {
        var dates = new[] { opt.TokenExpires, opt.ProtectKeyExpires }.OfType<DateTime>().Select(d => d.Date).ToList();
        var name = Tr.Admin("Ablaufdaten", "Expiry dates");
        if (dates.Count == 0)
            return Item(name, CheckLevel.Warn, Tr.Admin("nicht eingetragen (TokenExpires, ProtectKeyExpires), keine Erinnerung vor Ablauf",
                "not entered (TokenExpires, ProtectKeyExpires), no reminder before expiry"));
        var first = dates.Min();
        if (first < DateTime.Today)
            return Item(name, CheckLevel.Fail, Tr.Admin($"Zugänge am {Tr.AdminDate(first)} abgelaufen. Erneuern",
                $"credentials expired on {Tr.AdminDate(first)}. Renew them"));
        return opt.ExpiryWarning(DateTime.Today) is { } warning
            ? Item(name, CheckLevel.Warn, warning)
            : Item(name, CheckLevel.Ok, Tr.Admin($"gültig bis {Tr.AdminDate(first)}", $"valid until {Tr.AdminDate(first)}"));
    }

    private static string ConfigName => Tr.Admin("Konfiguration", "Configuration");

    private static string CredentialsName => Tr.Admin("Zugänge", "Credentials");

    private static string EventsName => Tr.Admin("Klingel-Ereignisse", "Ring events");

    private static string LiveViewName => Tr.Admin("Livebild", "Live view");

    private static string ProtectKeyName => Tr.Admin("Protect-Schlüssel", "Protect key");

    private static CheckItem Item(string name, CheckLevel level, string detail) =>
        new(CheckReport.Installation, name, level, detail);
}

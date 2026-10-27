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
            return new CheckResultMessage([Item("Prüfung", CheckLevel.Info, "Läuft bereits, gleich noch einmal versuchen")]);
        try
        {
            var items = await CheckAsync(ct).ConfigureAwait(false);
            log.LogInformation("Prüfung für {User}: {Fail} rot, {Warn} orange",
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
            items.Add(Item("Konfiguration", CheckLevel.Fail, $"{ServicePaths.ConfigFile} fehlt. install.ps1 mit sientrydesk.json ausführen"));
            return items;
        }
        if (opt.Validate() is { } problem)
        {
            items.Add(Item("Konfiguration", CheckLevel.Fail, $"{problem}. sientrydesk.json mit SIEntryDesk.exe --setup prüfen"));
            return items;
        }
        var host = opt.Host.Trim();
        items.Add(Item("Konfiguration", CheckLevel.Ok, $"Konsole {host}"));
        items.AddRange(ConfigFiles());

        // Credentials
        Secrets? secrets = null;
        try
        {
            secrets = SecretStore.Load();
            items.Add(secrets.ProtectKey is null
                ? Item("Zugänge", opt.VideoConfigured ? CheckLevel.Warn : CheckLevel.Ok,
                    "gespeichert, ohne Protect-Schlüssel (kein Livebild, kein Gegensprechen)")
                : Item("Zugänge", CheckLevel.Ok, "Access-Token und Protect-Schlüssel gespeichert"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       or JsonException or InvalidDataException)
        {
            items.Add(Item("Zugänge", CheckLevel.Fail, "fehlen oder nicht lesbar. set-tokens.ps1 bzw. Paket Zugänge erneut ausführen"));
        }

        // All ports at the same time: if the console is unreachable, the check takes at most one PortTimeout this way.
        var accessPin = CertificatePin.Parse(opt.AccessPin);
        var video = opt.VideoConfigured && opt.ValidateVideo() is null;
        var protectPin = video ? CertificatePin.Parse(opt.ProtectPin) : null;
        var accessPort = PortAsync("Access (Port 12445)", host, AccessApiClient.Port, accessPin, ct);
        var protectPort = video ? PortAsync("Protect (Port 443)", host, 443, protectPin!, ct) : null;
        var streamPort = video ? PortAsync("Videostream (Port 7441)", host, 7441, CertificatePin.Parse(opt.EffectiveStreamPin), ct) : null;
        var ports = new[] { accessPort, protectPort, streamPort }.OfType<Task<PortResult>>().ToArray();
        await Task.WhenAll(ports).ConfigureAwait(false);
        var unreachable = ports.All(p => p.Result.Unreachable);
        if (unreachable)
        {
            var numbers = video ? "12445, 443, 7441" : "12445";
            items.Add(Item("Konsole", CheckLevel.Fail, $"{host} nicht erreichbar (Port {numbers}). Firmennetz oder VPN, Firewall prüfen"));
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
                LinkHealth.Ready => Item("Klingel-Ereignisse", CheckLevel.Ok, "verbunden, dieser PC klingelt"),
                LinkHealth.Starting => Item("Klingel-Ereignisse", CheckLevel.Warn, "Dienst verbindet sich noch, in einer Minute erneut prüfen"),
                _ => Item("Klingel-Ereignisse", CheckLevel.Fail, state.Status().Problem),
            });

        // Protect: live view and talkback
        if (!opt.VideoConfigured)
        {
            items.Add(Item("Livebild", CheckLevel.Info, "nicht eingerichtet (kein ProtectPin in der sientrydesk.json)"));
        }
        else if (opt.ValidateVideo() is { } videoProblem)
        {
            items.Add(Item("Livebild", CheckLevel.Fail, videoProblem));
        }
        else if (!unreachable)
        {
            items.Add(protectPort!.Result.Item);
            items.Add(streamPort!.Result.Item);
            if (protectPort.Result.Ok && secrets?.ProtectKey is { } key)
                await ProtectAsync(items, host, key, protectPin!, opt, ct).ConfigureAwait(false);
        }

        items.Add(!opt.Talkback
            ? Item("Gegensprechen", CheckLevel.Info, "auf diesem PC abgeschaltet")
            : state.TalkEnabled
                ? Item("Gegensprechen", CheckLevel.Ok, "eingerichtet. Ob der Ton an der Tür ankommt, zeigt nur ein Test mit jemandem dort")
                : Item("Gegensprechen", CheckLevel.Warn, "braucht Protect-Schlüssel und ProtectPin"));

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
                return new PortResult(Item(name, CheckLevel.Ok, "erreichbar, Zertifikat passt zum Pin"), true, false);
            return new PortResult(Item(name, CheckLevel.Fail,
                $"Zertifikat passt nicht zum Pin (erhalten {fingerprint}). Neues Zertifikat auf der Konsole? Dann Pin anpassen"),
                false, false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or AuthenticationException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return new PortResult(Item(name, CheckLevel.Fail, "nicht erreichbar. Firmennetz oder VPN, Firewall prüfen"), false, true);
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
                yield return Item(name, CheckLevel.Ok, $"vom {Stamp(loaded!.Value)}, gilt seit dem Start des Dienstes");
            else if (loaded is null)
                yield return Item(name, CheckLevel.Warn, $"seit dem Start des Dienstes neu ({Stamp(current!.Value)}). Gilt erst nach einem Neustart des Dienstes oder des PCs");
            else if (current is null)
                yield return Item(name, CheckLevel.Warn, $"seit dem Start des Dienstes gelöscht, er läuft noch mit der Fassung vom {Stamp(loaded.Value)}. Dienst neu starten");
            else
                yield return Item(name, CheckLevel.Warn, $"am {Stamp(current.Value)} geändert, der Dienst läuft noch mit der Fassung vom {Stamp(loaded.Value)}. Dienst oder PC neu starten");
        }
    }

    private static string Stamp(DateTime time) => time.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static async Task AccessTokenAsync(
        List<CheckItem> items, string host, string token, CertificatePin pin, CancellationToken ct)
    {
        try
        {
            using var access = new AccessApiClient(host, token, pin);
            var rights = await SetupChecks.CheckAccessTokenAsync(access, ct).ConfigureAwait(false);
            if (!rights.Events && !rights.Doors && !rights.Unlock)
            {
                items.Add(Item("Access-Token", CheckLevel.Fail, "abgelehnt oder ohne Rechte. Neuen Token anlegen (Gerät = Anzeigen, Standorte = Bearbeiten)"));
                return;
            }
            var missing = new[] { (rights.Events, "Gerät = Anzeigen"), (rights.Doors, "Standorte = Anzeigen"), (rights.Unlock, "Standorte = Bearbeiten") }
                .Where(r => !r.Item1).Select(r => r.Item2).ToList();
            items.Add(missing.Count == 0
                ? Item("Access-Token", CheckLevel.Ok, "gültig, Rechte für Klingeln, Türen und Öffnen")
                : Item("Access-Token", CheckLevel.Fail, $"es fehlt: {string.Join(", ", missing)}"));
            if (rights.Excess.Count > 0)
                items.Add(Item("Access-Token", CheckLevel.Warn, $"überflüssige Rechte: {string.Join(", ", rights.Excess)}. Token neu anlegen, dort „Keinen“"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            if (ct.IsCancellationRequested)
                throw;
            items.Add(Item("Access-Token", CheckLevel.Fail, "Access antwortet nicht"));
        }
    }

    private async Task ProtectAsync(
        List<CheckItem> items, string host, string key, CertificatePin pin, EntryDeskOptions opt, CancellationToken ct)
    {
        try
        {
            using var protect = new ProtectApiClient(host, key, pin);
            var version = await protect.GetVersionAsync(ct).ConfigureAwait(false);
            items.Add(Item("Protect-Schlüssel", CheckLevel.Ok, $"gültig, Protect {version}"));

            var cameras = await protect.GetCameraNamesAsync(ct).ConfigureAwait(false);
            var known = (doors()?.All() ?? []).Where(d => opt.AcceptsDoor(d.DoorId, d.DoorName)).ToList();
            if (known.Count == 0)
                items.Add(Item("Livebild", CheckLevel.Warn, "noch keine Tür bekannt. DoorCameras in der sientrydesk.json oder einmal klingeln"));
            foreach (var door in known)
                items.Add(await DoorStreamAsync(protect, door, cameras, ct).ConfigureAwait(false));
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            items.Add(Item("Protect-Schlüssel", CheckLevel.Fail, "abgelehnt. Neuen Schlüssel anlegen und mit set-tokens.ps1 bzw. Paket Zugänge setzen"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or AuthenticationException or JsonException)
        {
            if (ct.IsCancellationRequested)
                throw;
            items.Add(Item("Protect-Schlüssel", CheckLevel.Fail, "Protect antwortet nicht"));
        }
    }

    /// <summary>One door at a time, so that a wrong camera ID does not hide the other doors.</summary>
    private async Task<CheckItem> DoorStreamAsync(
        ProtectApiClient protect, KnownDoor door, IReadOnlyDictionary<string, string> cameras, CancellationToken ct)
    {
        var doorName = door.DoorName.Length > 0 ? door.DoorName : "Tür";
        if (door.CameraId.Length == 0)
            return Item($"Livebild {doorName}", CheckLevel.Warn, "Kamera unbekannt, wird beim ersten Klingeln gelernt");
        if (!cameras.TryGetValue(door.CameraId, out var cameraName))
            return Item($"Livebild {doorName}", CheckLevel.Fail,
                $"Kamera {door.CameraId} gibt es in Protect nicht. DoorCameras in der sientrydesk.json prüfen");
        var name = $"Livebild {cameraName}";
        try
        {
            var streams = await protect.GetStreamsAsync(door.CameraId, ct).ConfigureAwait(false);
            return streams.Count > 0
                ? Item(name, CheckLevel.Ok, $"RTSPS-Stream vorhanden ({string.Join(", ", streams.Keys)})")
                : Item(name, CheckLevel.Fail, "kein RTSPS-Stream. In Protect bei der Kamera einschalten (Qualität Mittel)");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden))
        {
            return Item(name, CheckLevel.Fail, $"Stream nicht lesbar (Protect meldet {(int?)ex.StatusCode})");
        }
    }

    private static CheckItem Expiry(EntryDeskOptions opt)
    {
        var dates = new[] { opt.TokenExpires, opt.ProtectKeyExpires }.OfType<DateTime>().Select(d => d.Date).ToList();
        if (dates.Count == 0)
            return Item("Ablaufdaten", CheckLevel.Warn, "nicht eingetragen (TokenExpires, ProtectKeyExpires), keine Erinnerung vor Ablauf");
        var first = dates.Min();
        if (first < DateTime.Today)
            return Item("Ablaufdaten", CheckLevel.Fail, $"Zugänge am {first.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)} abgelaufen. Erneuern");
        return opt.ExpiryWarning(DateTime.Today) is { } warning
            ? Item("Ablaufdaten", CheckLevel.Warn, warning)
            : Item("Ablaufdaten", CheckLevel.Ok, $"gültig bis {first.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");
    }

    private static CheckItem Item(string name, CheckLevel level, string detail) =>
        new(CheckReport.Installation, name, level, detail);
}

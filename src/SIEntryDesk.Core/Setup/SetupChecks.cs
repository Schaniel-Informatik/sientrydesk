using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Security;

namespace SIEntryDesk.Core.Setup;

/// <summary>Erreichbarkeit eines Ports der Konsole und der Fingerabdruck seines Zertifikats.</summary>
public sealed record PortCheck(string Name, int Port, bool Reachable, string? Fingerprint, string? Problem);

/// <summary>
/// Ergebnis der Rechteprüfung eines Access-Tokens. Benötigt: Gerät anzeigen (Ereignisse), Standorte anzeigen (Türliste),
/// Standorte bearbeiten (Öffnen). Excess nennt Rechte, die der Token hat, aber nicht braucht.
/// </summary>
public sealed record AccessRights(bool Events, bool Doors, bool Unlock, IReadOnlyList<string> Excess)
{
    public bool Complete => Events && Doors && Unlock;
    public bool Minimal => Complete && Excess.Count == 0;
}

/// <summary>Vergleich eines Ports der Konsole mit dem Pin einer bestehenden Konfiguration.</summary>
public enum PinComparison
{
    /// <summary>Erreichbar, Fingerabdruck wie konfiguriert.</summary>
    Match,

    /// <summary>Erreichbar, aber ein anderer Fingerabdruck als konfiguriert.</summary>
    Mismatch,

    /// <summary>Erreichbar, in der Konfiguration steht kein Pin.</summary>
    NotConfigured,

    /// <summary>Nicht erreichbar, kein Vergleich möglich.</summary>
    Unreachable,
}

public enum FindingLevel
{
    Ok,
    Warn,
    Fail,
}

/// <summary>Ergebnis beim Vergleich einer bestehenden Konfiguration mit der Anlage.</summary>
public sealed record ConfigFinding(FindingLevel Level, string Text);

/// <summary>Prüfungen für den Einrichtungsassistenten. Alle lesend, mit Ausnahme von <see cref="ProtectApiClient.CreateStreamAsync"/>.</summary>
public static class SetupChecks
{
    public static readonly (string Name, int Port)[] Ports =
        [("Access-API", AccessApiClient.Port), ("UniFi OS / Protect", 443), ("Videostream", 7441)];

    public static async Task<IReadOnlyList<PortCheck>> CheckPortsAsync(string host, CancellationToken ct)
    {
        var checks = Ports.Select(async p =>
        {
            try
            {
                var fp = await CertificateProbe.FetchFingerprintAsync(host, p.Port, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                return new PortCheck(p.Name, p.Port, true, fp, null);
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                           or AuthenticationException or InvalidOperationException)
            {
                return new PortCheck(p.Name, p.Port, false, null,
                    ex is OperationCanceledException ? "keine Antwort" : UntrustedText.Clean(ex.Message, 100));
            }
        });
        return await Task.WhenAll(checks).ConfigureAwait(false);
    }

    /// <summary>Rechte, die ein Betriebs-Token nicht haben soll, je mit einem rein lesenden Aufruf.</summary>
    private static readonly (string Name, HttpMethod Method, string Path)[] ExcessProbes =
    [
        ("Benutzer und Gruppe", HttpMethod.Get, "/api/v1/developer/users?page_num=1&page_size=1"),
        ("Besucher", HttpMethod.Get, "/api/v1/developer/visitors?page_num=1&page_size=1"),
        ("Zugriffsrichtlinie", HttpMethod.Get, "/api/v1/developer/access_policies"),
        ("Anmeldeinformationen", HttpMethod.Get, "/api/v1/developer/credentials/nfc_cards/tokens?page_num=1&page_size=1"),
        ("Webhooks", HttpMethod.Get, "/api/v1/developer/webhooks/endpoints"),
        ("Systemprotokoll", HttpMethod.Post, "/api/v1/developer/system/logs?page_num=1&page_size=1"),
    ];

    private const string NonexistentDoor = "00000000-0000-4000-8000-000000000000";

    /// <summary>
    /// Prüft die Rechte eines Access-Tokens, ohne etwas zu ändern. Das Recht zum Öffnen wird mit einer Tür geprüft,
    /// die es nicht gibt: ohne Recht antwortet Access mit CODE_UNAUTHORIZED, mit Recht mit CODE_NOT_EXISTS.
    /// </summary>
    public static async Task<AccessRights> CheckAccessTokenAsync(AccessApiClient api, CancellationToken ct)
    {
        var events = (await api.ProbeAsync(HttpMethod.Get, "/api/v1/developer/devices", null, ct).ConfigureAwait(false)).Success;
        var doors = (await api.ProbeAsync(HttpMethod.Get, "/api/v1/developer/doors", null, ct).ConfigureAwait(false)).Success;
        var unlock = await api.ProbeAsync(HttpMethod.Put, $"/api/v1/developer/doors/{NonexistentDoor}/unlock",
            new Dictionary<string, string> { ["actor_id"] = NonexistentDoor, ["actor_name"] = "SI EntryDesk Rechteprüfung" }, ct).ConfigureAwait(false);

        var excess = new List<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (name, method, path) in ExcessProbes)
        {
            // Beim Systemprotokoll nur eine Minute und ein Eintrag, damit keine Daten mitkommen.
            object? body = method == HttpMethod.Post
                ? new Dictionary<string, object> { ["topic"] = "critical", ["since"] = now - 60, ["until"] = now }
                : null;
            if ((await api.ProbeAsync(method, path, body, ct).ConfigureAwait(false)).Success)
                excess.Add(name);
        }
        return new AccessRights(events, doors, unlock.Code == "CODE_NOT_EXISTS", excess);
    }

    /// <summary>
    /// Schlägt zu jeder Tür die Kamera mit dem passenden Namen vor, z. B. Tür „Eingang Nord HUB“ und Kamera
    /// „Eingang Nord“. Nur eindeutige Treffer, den Rest ordnet der Admin selbst zu.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SuggestDoorCameras(
        IEnumerable<AccessDoor> doors, IReadOnlyDictionary<string, string> cameras)
    {
        var suggestions = new Dictionary<string, string>(StringComparer.Ordinal);
        var cameraKeys = cameras.Select(c => (Id: c.Key, Key: NameKey(c.Value))).Where(c => c.Key.Length > 0).ToList();
        foreach (var door in doors)
        {
            var doorKey = NameKey(door.Name);
            if (doorKey.Length == 0)
                continue;
            var matches = cameraKeys.Where(c => c.Key == doorKey).ToList();
            if (matches.Count == 0)
                matches = cameraKeys.Where(c => doorKey.Contains(c.Key, StringComparison.Ordinal) ||
                                                c.Key.Contains(doorKey, StringComparison.Ordinal)).ToList();
            if (matches.Count == 1)
                suggestions[door.Id] = matches[0].Id;
        }
        return suggestions;
    }

    /// <summary>Kleinbuchstaben, nur Buchstaben und Ziffern, ohne Füllwörter wie „HUB“, „Tür“, „Door“.</summary>
    internal static string NameKey(string name)
    {
        var words = name.ToLowerInvariant()
            .Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("hub" or "tür" or "tuer" or "door" or "uah" or "g6" or "entry" or "pro"));
        var sb = new StringBuilder();
        foreach (var c in string.Concat(words))
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions ConfigReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Liest eine bestehende sientrydesk.json. Kommentare und unbekannte Felder sind erlaubt wie beim Dienst.</summary>
    public static EntryDeskOptions ParseConfigJson(string json)
    {
        var options = JsonSerializer.Deserialize<EntryDeskOptions>(json, ConfigReadOptions)
                      ?? throw new JsonException("Die Datei enthält keine Konfiguration.");
        options.Host ??= string.Empty;
        options.AccessPin ??= string.Empty;
        options.ProtectPin ??= string.Empty;
        options.StreamPin ??= string.Empty;
        options.Doors ??= [];
        options.DoorCameras ??= new(StringComparer.Ordinal);
        options.StreamQualities ??= ["low", "medium", "high"];
        return options;
    }

    /// <summary>Pin, den eine Konfiguration für diesen Port vorgibt, oder leer.</summary>
    public static string ConfiguredPin(EntryDeskOptions options, int port) => port switch
    {
        AccessApiClient.Port => options.AccessPin,
        443 => options.ProtectPin,
        7441 => options.EffectiveStreamPin,
        _ => string.Empty,
    };

    public static PinComparison ComparePin(PortCheck port, string? configuredPin)
    {
        if (!port.Reachable || port.Fingerprint is null)
            return PinComparison.Unreachable;
        if (string.IsNullOrWhiteSpace(configuredPin))
            return PinComparison.NotConfigured;
        try
        {
            var actual = CertificatePin.Parse(port.Fingerprint).ToString();
            return string.Equals(actual, CertificatePin.Parse(configuredPin).ToString(), StringComparison.Ordinal)
                ? PinComparison.Match
                : PinComparison.Mismatch;
        }
        catch (FormatException)
        {
            return PinComparison.Mismatch;
        }
    }

    /// <summary>
    /// True, wenn die Konsole genau die Zertifikate der Konfiguration zeigt: Access erreichbar und gleich, jeder andere
    /// erreichbare Port ebenfalls gleich. Dann gibt es nichts neu zu bestätigen. Ein Port ohne Pin in der Konfiguration
    /// zählt als neu, weil Speichern ihn übernehmen würde.
    /// </summary>
    public static bool PinsUnchanged(EntryDeskOptions options, IReadOnlyList<PortCheck> ports) =>
        ports.Any(p => p.Port == AccessApiClient.Port && ComparePin(p, options.AccessPin) == PinComparison.Match) &&
        ports.All(p => ComparePin(p, ConfiguredPin(options, p.Port)) is PinComparison.Match or PinComparison.Unreachable);

    /// <summary>
    /// Vergleicht Türen und Kamerazuordnung einer bestehenden Konfiguration mit dem, was Access und Protect melden.
    /// <paramref name="cameras"/> ist null, solange der Protect-Schlüssel nicht geprüft ist.
    /// </summary>
    public static IReadOnlyList<ConfigFinding> CompareDoors(
        EntryDeskOptions options, IReadOnlyList<AccessDoor> doors, IReadOnlyDictionary<string, string>? cameras)
    {
        var findings = new List<ConfigFinding>();
        foreach (var entry in options.Doors.Where(e => !doors.Any(d => Names(e, d))))
            findings.Add(new(FindingLevel.Warn, $"Doors nennt „{entry.Trim()}“, diese Tür gibt es in Access nicht."));

        foreach (var (doorId, cameraId) in options.DoorCameras)
        {
            var door = doors.FirstOrDefault(d => d.Id == doorId);
            if (door is null)
                findings.Add(new(FindingLevel.Warn, $"DoorCameras nennt eine Tür, die es in Access nicht gibt ({doorId})."));
            else if (cameras is not null && !cameras.ContainsKey(cameraId))
                findings.Add(new(FindingLevel.Fail, $"{door.Name}: Die Kamera aus der Konfiguration gibt es in Protect nicht, kein Livebild."));
        }

        if (cameras is not null)
        {
            var suggestions = SuggestDoorCameras(doors, cameras);
            foreach (var door in doors.Where(d => options.AcceptsDoor(d.Id, d.Name) && !options.DoorCameras.ContainsKey(d.Id)))
            {
                var hint = suggestions.TryGetValue(door.Id, out var camera) ? $" Vorschlag: {cameras[camera]}." : string.Empty;
                findings.Add(new(FindingLevel.Warn, $"{door.Name}: keine Kamera zugeordnet, Livebild ohne Klingeln erst nach dem ersten Klingeln.{hint}"));
            }
        }

        if (findings.Count == 0)
            findings.Add(new(FindingLevel.Ok, cameras is null
                ? "Türen wie in der Konfiguration. Die Kameras folgen mit der Prüfung des Protect-Schlüssels."
                : "Türen und Kameras wie in der Konfiguration."));
        return findings;

        static bool Names(string entry, AccessDoor door) =>
            string.Equals(entry.Trim(), door.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Trim(), door.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Anlagen-Konfiguration als Text für sientrydesk.json, mit Kopfkommentar auf eigenen Zeilen.</summary>
    public static string ToConfigJson(EntryDeskOptions options, DateTime created)
    {
        var config = new Dictionary<string, object?>
        {
            ["Host"] = options.Host.Trim(),
            ["AccessPin"] = options.AccessPin,
            ["ProtectPin"] = options.ProtectPin,
        };
        if (!string.IsNullOrWhiteSpace(options.StreamPin) && options.StreamPin != options.ProtectPin)
            config["StreamPin"] = options.StreamPin;
        config["Doors"] = options.Doors;
        config["DoorCameras"] = options.DoorCameras;
        config["LiveView"] = options.LiveView;
        config["LiveViewSeconds"] = Math.Clamp(options.LiveViewSeconds, 15, 600);
        if (options.TokenExpires is { } token)
            config["TokenExpires"] = token.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (options.ProtectKeyExpires is { } key)
            config["ProtectKeyExpires"] = key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return $"// SI EntryDesk: Anlagen-Konfiguration, erstellt mit dem Einrichtungsassistenten am {created:dd.MM.yyyy HH:mm}.\n" +
               "// Keine Geheimnisse. Zugänge von Hand mit install.ps1 -SetTokens, per Intune mit dem Paket SI EntryDesk Zugänge.\n" +
               json + "\n";
    }
}

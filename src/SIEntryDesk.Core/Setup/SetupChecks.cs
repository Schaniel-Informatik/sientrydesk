using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
               "// Keine Geheimnisse. Tokens mit install.ps1 -SetTokens bzw. set-tokens.ps1 setzen.\n" + json + "\n";
    }
}

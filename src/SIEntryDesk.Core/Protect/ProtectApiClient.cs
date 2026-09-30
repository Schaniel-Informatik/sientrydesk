using System.Text.Json;
using System.Text.RegularExpressions;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Video;

namespace SIEntryDesk.Core.Protect;

/// <summary>Lesende Aufrufe der Protect Integration API über UniFi OS (Port 443), gepinnt, ohne Proxy.</summary>
public sealed partial class ProtectApiClient : IDisposable
{
    private const string BasePath = "/proxy/protect/integration/v1";
    private readonly HttpClient _http;
    private readonly string _host;

    public ProtectApiClient(string host, string apiKey, CertificatePin pin)
    {
        _host = host;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = { RemoteCertificateValidationCallback = pin.Validate },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttps, host).Uri,
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
    }

    /// <summary>
    /// Vorhandene RTSPS-Streams einer Kamera je Qualität. Verwendet werden nur Port und Pfad aus der Antwort,
    /// der Host bleibt der konfigurierte, damit eine manipulierte Antwort nicht auf ein fremdes Ziel lenkt.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, StreamSource>> GetStreamsAsync(string cameraId, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(cameraId))
            throw new ArgumentException("Ungültige Kamera-ID.", nameof(cameraId));
        using var response = await _http.GetAsync($"{BasePath}/cameras/{cameraId}/rtsps-stream", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);

        var streams = new Dictionary<string, StreamSource>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return streams;
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(property.Value.GetString(), UriKind.Absolute, out var uri) &&
                uri.Scheme == "rtsps" && uri.Port is > 0 and < 65536 && StreamPath().IsMatch(uri.AbsolutePath))
            {
                streams[property.Name] = new StreamSource(_host, uri.Port, uri.AbsolutePath);
            }
        }
        return streams;
    }

    /// <summary>Kamera-ID → Name aus Protect. Dient auch als Lebenszeichen für Schlüssel und Erreichbarkeit.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetCameraNamesAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{BasePath}/cameras", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var camera in doc.RootElement.EnumerateArray())
        {
            if (camera.ValueKind == JsonValueKind.Object &&
                camera.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                camera.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                UntrustedText.IsSafeId(id.GetString()))
            {
                var clean = UntrustedText.Clean(name.GetString(), 60);
                if (clean.Length > 0)
                    names[id.GetString()!] = clean;
            }
        }
        return names;
    }

    [GeneratedRegex("^/[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex StreamPath();

    public void Dispose() => _http.Dispose();
}

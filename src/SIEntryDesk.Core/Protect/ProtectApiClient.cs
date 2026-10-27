using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Talk;
using SIEntryDesk.Core.Video;

namespace SIEntryDesk.Core.Protect;

/// <summary>Calls to the Protect Integration API via UniFi OS (port 443), pinned, without a proxy.</summary>
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
    /// Available RTSPS streams of a camera per quality. Only port and path are taken from the response, the host stays
    /// the configured one, so that a manipulated response cannot redirect to a foreign target.
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

    /// <summary>Protect version, as a simple check of the key.</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{BasePath}/meta/info", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("applicationVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? UntrustedText.Clean(v.GetString(), 30)
            : "?";
    }

    /// <summary>Creates an RTSPS stream for a camera. Changes the camera configuration, only after
    /// confirmation.</summary>
    public async Task<IReadOnlyDictionary<string, StreamSource>> CreateStreamAsync(string cameraId, string quality, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(cameraId) || quality is not ("low" or "medium" or "high"))
            throw new ArgumentException("Ungültige Kamera oder Qualität.");
        using var response = await _http.PostAsJsonAsync($"{BasePath}/cameras/{cameraId}/rtsps-stream",
            new Dictionary<string, string[]> { ["qualities"] = [quality] }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await GetStreamsAsync(cameraId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a talkback session to the camera (official Integration API). The response names the target for the audio,
    /// usually the door station itself. The API knows no lifetime and no way to close it, therefore a new one per talk
    /// turn.
    /// </summary>
    public async Task<TalkbackTarget?> CreateTalkbackSessionAsync(string cameraId, Action<string> problem, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(cameraId))
            throw new ArgumentException("Ungültige Kamera-ID.", nameof(cameraId));
        using var response = await _http.PostAsync($"{BasePath}/cameras/{cameraId}/talkback-session", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            problem("Antwort von Protect ist kein Objekt");
            return null;
        }
        var target = TalkbackTarget.TryCreate(
            String(root, "url"), String(root, "codec"),
            root.TryGetProperty("samplingRate", out var rate) && rate.ValueKind == JsonValueKind.Number && rate.TryGetInt32(out var r) ? r : 0,
            out var why);
        if (target is null)
            problem(why);
        return target;

        static string? String(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>Camera ID → name from Protect. Also serves as a sign of life for key and reachability.</summary>
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

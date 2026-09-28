using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SIEntryDesk.Core.Security;

namespace SIEntryDesk.Core.Access;

public sealed record AccessResult(int HttpStatus, string? Code, string Message)
{
    public bool Success => HttpStatus == 200 && Code == "SUCCESS";
}

/// <summary>REST-Aufrufe der Access Developer API (Port 12445) mit gepinntem Zertifikat, ohne Proxy.</summary>
public sealed class AccessApiClient : IDisposable
{
    public const int Port = 12445;
    private readonly HttpClient _http;

    public AccessApiClient(string host, string token, CertificatePin pin)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = { RemoteCertificateValidationCallback = pin.Validate },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttps, host, Port).Uri,
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Öffnet eine Tür (Recht edit:space). actor_id und actor_name erscheinen im Access-Protokoll.
    /// </summary>
    public async Task<AccessResult> UnlockAsync(string doorId, string actorId, string actorName, CancellationToken ct)
    {
        if (!UntrustedText.IsSafeId(doorId))
            throw new ArgumentException("Ungültige Tür-ID.", nameof(doorId));
        var body = new Dictionary<string, string> { ["actor_id"] = actorId, ["actor_name"] = actorName };
        using var response = await _http.PutAsJsonAsync($"/api/v1/developer/doors/{doorId}/unlock", body, ct)
            .ConfigureAwait(false);
        return await ReadResultAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<AccessResult> ReadResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        string message = string.Empty;
        try
        {
            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                    code = UntrustedText.Clean(c.GetString(), 60);
                if (doc.RootElement.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
                    message = UntrustedText.Clean(m.GetString());
            }
        }
        catch (JsonException)
        {
            message = "Antwort nicht lesbar";
        }
        return new AccessResult((int)response.StatusCode, code, message);
    }

    public void Dispose() => _http.Dispose();
}

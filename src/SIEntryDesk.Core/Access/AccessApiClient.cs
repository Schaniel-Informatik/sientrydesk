using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SIEntryDesk.Core.Security;

namespace SIEntryDesk.Core.Access;

public sealed record AccessResult(int HttpStatus, string? Code, string Message)
{
    public bool Success => HttpStatus == 200 && Code == "SUCCESS";
}

/// <summary>A door from Access (permission view:space).</summary>
public sealed record AccessDoor(string Id, string Name, string FullName);

/// <summary>REST calls to the Access Developer API (port 12445) with a pinned certificate, without a proxy.</summary>
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
    /// Unlocks a door (permission edit:space). actor_id and actor_name appear in the Access log.
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

    /// <summary>All doors (permission view:space).</summary>
    public async Task<IReadOnlyList<AccessDoor>> GetDoorsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("/api/v1/developer/doors", ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var doors = new List<AccessDoor>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return doors;
        foreach (var door in data.EnumerateArray())
        {
            if (door.ValueKind != JsonValueKind.Object)
                continue;
            var id = Text(door, "id");
            if (UntrustedText.IsSafeId(id))
                doors.Add(new AccessDoor(id!, UntrustedText.Clean(Text(door, "name")), UntrustedText.Clean(Text(door, "full_name"))));
        }
        return doors;

        static string? Text(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>For the permission check: any call, result only as status and code, the data is discarded.</summary>
    public async Task<AccessResult> ProbeAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
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

using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Core.Access;

/// <summary>State of the connection to Access, for display on the PC.</summary>
public enum AccessLinkState
{
    Connected,
    /// <summary>Console not reachable, typically outside the company network. No alarm.</summary>
    Unreachable,
    /// <summary>Token rejected (401/403).</summary>
    Rejected,
    /// <summary>Certificate does not match the pin.</summary>
    CertificateMismatch,
    /// <summary>Connection reached, but disturbed or interrupted.</summary>
    Interrupted,
}

/// <summary>
/// Persistent connection to the event WebSocket of the Access Developer API. Access does not answer WebSocket pings,
/// but sends a "Hello" every 5 s. If it stays silent for longer, the connection is re-established so that no ring gets
/// lost in a dead connection.
/// </summary>
public sealed class AccessEventStream
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AuthFailureBackoff = TimeSpan.FromMinutes(5);
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly Uri _uri;
    private readonly string _token;
    private readonly CertificatePin _pin;
    private readonly ILogger _log;

    public AccessEventStream(string host, string token, CertificatePin pin, ILogger log)
    {
        _uri = new UriBuilder("wss", host, AccessApiClient.Port, "/api/v1/developer/devices/notifications").Uri;
        _token = token;
        _pin = pin;
        _log = log;
    }

    public async Task RunAsync(
        Func<AccessEvent, CancellationToken, ValueTask> onEvent,
        Action<AccessLinkState> onStateChanged,
        CancellationToken ct)
    {
        var backoff = MinBackoff;
        while (!ct.IsCancellationRequested)
        {
            var connected = false;
            string? rejectedFingerprint = null;
            using var ws = new ClientWebSocket();
            try
            {
                ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
                ws.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (_pin.Matches(certificate))
                        return true;
                    rejectedFingerprint = certificate is null ? Tr.Admin("kein Zertifikat", "no certificate") : CertificatePin.FingerprintOf(certificate);
                    return false;
                };
                ws.Options.Proxy = null;
                ws.Options.KeepAliveInterval = TimeSpan.Zero;
                ws.Options.CollectHttpResponseDetails = true;

                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await ws.ConnectAsync(_uri, connectCts.Token).ConfigureAwait(false);
                }

                connected = true;
                backoff = MinBackoff;
                _log.LogInformation(Tr.Admin("Access-Ereignisse verbunden", "Access events connected"));
                onStateChanged(AccessLinkState.Connected);
                await ReceiveLoopAsync(ws, onEvent, ct).ConfigureAwait(false);
                _log.LogWarning(Tr.Admin("Access hat die Verbindung geschlossen", "Access closed the connection"));
                onStateChanged(AccessLinkState.Interrupted);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var status = (int)ws.HttpStatusCode;
                AccessLinkState state;
                if (rejectedFingerprint is not null)
                {
                    _log.LogError(Tr.Admin(
                        "Zertifikat der Konsole passt nicht zum Pin, Verbindung abgelehnt. Erhalten: {Fingerprint}. " +
                        "Nur wenn die Konsole ihr Zertifikat nachweislich erneuert hat, den Pin anpassen",
                        "Certificate of the console does not match the pin, connection rejected. Received: {Fingerprint}. " +
                        "Only adjust the pin if the console has demonstrably renewed its certificate"), rejectedFingerprint);
                    backoff = MaxBackoff;
                    state = AccessLinkState.CertificateMismatch;
                }
                else if (status is 401 or 403)
                {
                    _log.LogError(Tr.Admin("Access lehnt den Token ab (HTTP {Status}). Token und Recht view:device prüfen",
                        "Access rejects the token (HTTP {Status}). Check token and permission view:device"), status);
                    backoff = AuthFailureBackoff;
                    state = AccessLinkState.Rejected;
                }
                else if (!connected && IsUnreachable(ex))
                {
                    _log.LogWarning(Tr.Admin("Konsole nicht erreichbar: {Message}", "Console not reachable: {Message}"),
                        ex is OperationCanceledException ? Tr.Admin("keine Antwort innerhalb von 10 s", "no answer within 10 s") : ex.Message);
                    state = AccessLinkState.Unreachable;
                }
                else
                {
                    _log.LogWarning(Tr.Admin("Access-Verbindung unterbrochen: {Type}: {Message}", "Access connection interrupted: {Type}: {Message}"),
                        ex.GetType().Name, ex.Message);
                    state = AccessLinkState.Interrupted;
                }
                onStateChanged(state);
            }

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (backoff < AuthFailureBackoff)
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>No TCP connection possible (timeout, network or host unreachable, DNS).</summary>
    private static bool IsUnreachable(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException or OperationCanceledException or TimeoutException)
                return true;
        }
        return false;
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        Func<AccessEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(IdleTimeout);
                do
                {
                    try
                    {
                        result = await ws.ReceiveAsync(buffer, idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"{IdleTimeout.TotalSeconds:0} s without a sign of life from Access");
                    }
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                        throw new InvalidDataException("Message from Access too large");
                } while (!result.EndOfMessage);
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            var ev = AccessEventParser.Parse(text);
            if (ev is null)
            {
                _log.LogDebug(Tr.Admin("Unlesbare Nachricht von Access verworfen ({Length} Bytes)", "Unreadable message from Access discarded ({Length} bytes)"),
                    message.Length);
                continue;
            }
            await onEvent(ev, ct).ConfigureAwait(false);
        }
    }
}

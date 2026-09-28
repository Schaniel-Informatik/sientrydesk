using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SIEntryDesk.Core.Security;

namespace SIEntryDesk.Core.Video;

/// <summary>Ein RTSPS-Stream der Konsole. Host kommt aus der eigenen Konfiguration, nie aus einer API-Antwort.</summary>
public sealed record StreamSource(string Host, int Port, string Path)
{
    public string UpstreamPrefix => $"rtsps://{Host}:{Port}{Path}";
}

/// <summary>
/// Lokaler RTSP-Zugang für den Videoplayer der App. Baut zur Konsole eine TLS-Verbindung mit gepinntem Zertifikat auf
/// und reicht den Stream unter einer Einmal-Adresse (rtsp://127.0.0.1:port/token) weiter. Adressen gelten nur für
/// einen Ruf und werden mit ihm geschlossen.
/// </summary>
public sealed class StreamProxy : IAsyncDisposable
{
    private const int MaxConnectionsPerSession = 2;
    private static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    private sealed class Session(string key, StreamSource source, DateTimeOffset expires)
    {
        public string Key { get; } = key;
        public StreamSource Source { get; } = source;
        public DateTimeOffset Expires { get; } = expires;
        public CancellationTokenSource Closed { get; } = new();
        public int Connections;
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly CertificatePin _pin;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private Task? _acceptLoop;

    public StreamProxy(CertificatePin streamPin, ILogger log, TimeProvider time)
    {
        _pin = streamPin;
        _log = log;
        _time = time;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    /// <summary>Neue Einmal-Adresse für einen Ruf.</summary>
    public string Open(string key, StreamSource source, TimeSpan lifetime)
    {
        PruneExpired();
        var token = Base64Url(RandomNumberGenerator.GetBytes(24));
        _sessions[token] = new Session(key, source, _time.GetUtcNow() + lifetime);
        return $"rtsp://127.0.0.1:{Port}/{token}";
    }

    /// <summary>Schliesst alle Adressen und laufenden Verbindungen eines Rufs.</summary>
    public void Close(string key)
    {
        foreach (var (token, session) in _sessions.Where(s => s.Value.Key == key).ToList())
        {
            if (_sessions.TryRemove(token, out _))
                session.Closed.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var session in _sessions.Values)
            session.Closed.Cancel();
        _sessions.Clear();
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        Session? session = null;
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var local = client.GetStream();
                var reader = new RtspReader(local);

                RtspChunk? first;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(FirstRequestTimeout);
                    first = await reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                }
                if (first is not RtspText request || (session = FindSession(request)) is null)
                {
                    session = null;
                    await RejectAsync(local, first as RtspText, "404 Not Found", ct).ConfigureAwait(false);
                    return;
                }
                if (Interlocked.Increment(ref session.Connections) > MaxConnectionsPerSession)
                {
                    await RejectAsync(local, request, "453 Not Enough Bandwidth", ct).ConfigureAwait(false);
                    return;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Closed.Token);
                var localPrefix = $"rtsp://127.0.0.1:{Port}/{TokenOf(request)}";
                var rewriter = new RtspRewriter(localPrefix, session.Source.UpstreamPrefix);
                await using var upstream = await ConnectUpstreamAsync(session.Source, linked.Token).ConfigureAwait(false);
                await RelayAsync(request, reader, local, upstream, rewriter, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                           or InvalidDataException or AuthenticationException or ObjectDisposedException)
            {
                if (ex is AuthenticationException)
                    _log.LogError("Video: Zertifikat der Konsole passt nicht zum Pin, Stream abgelehnt");
                else if (ex is SocketException)
                    _log.LogWarning("Video: Konsole nicht erreichbar: {Message}", ex.Message);
                else if (ex is not OperationCanceledException)
                    _log.LogDebug("Video-Verbindung beendet: {Type}: {Message}", ex.GetType().Name, ex.Message);
            }
            finally
            {
                if (session is not null)
                    Interlocked.Decrement(ref session.Connections);
            }
        }
    }

    private Session? FindSession(RtspText request)
    {
        var token = TokenOf(request);
        if (token is null || !_sessions.TryGetValue(token, out var session))
            return null;
        return session.Expires > _time.GetUtcNow() ? session : null;
    }

    /// <summary>Token aus "METHODE rtsp://127.0.0.1:port/TOKEN[/...] RTSP/1.0".</summary>
    private string? TokenOf(RtspText request)
    {
        var parts = request.StartLine.Split(' ');
        var prefix = $"rtsp://127.0.0.1:{Port}/";
        if (parts.Length != 3 || !parts[1].StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var token = parts[1][prefix.Length..].Split('/')[0];
        return token.Length == 32 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? token : null;
    }

    private async Task<SslStream> ConnectUpstreamAsync(StreamSource source, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                await tcp.ConnectAsync(source.Host, source.Port, timeout.Token).ConfigureAwait(false);
            }
            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = source.Host,
                RemoteCertificateValidationCallback = _pin.Validate,
            }, ct).ConfigureAwait(false);
            return tls;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task RelayAsync(
        RtspText first, RtspReader clientReader, Stream client, Stream upstream, RtspRewriter rewriter, CancellationToken ct)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toUpstream = PumpToUpstreamAsync(first, clientReader, client, upstream, rewriter, done.Token);
        var toClient = PumpToClientAsync(new RtspReader(upstream), client, rewriter, done.Token);
        var finished = await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);
        await done.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(toUpstream, toClient).ConfigureAwait(false);
        }
        catch (Exception) when (finished.IsCompletedSuccessfully)
        {
            // Die Gegenrichtung wurde nur abgebrochen, weil eine Seite fertig war.
        }
    }

    private static async Task PumpToUpstreamAsync(
        RtspText first, RtspReader reader, Stream client, Stream upstream, RtspRewriter rewriter, CancellationToken ct)
    {
        RtspChunk? chunk = first;
        while (chunk is not null)
        {
            switch (chunk)
            {
                case RtspInterleaved frame:
                    await upstream.WriteAsync(frame.Raw, ct).ConfigureAwait(false);
                    break;
                case RtspText request:
                    if (rewriter.ToUpstream(request) is { } allowed)
                    {
                        await upstream.WriteAsync(allowed.ToBytes(), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await RejectAsync(client, request, "405 Method Not Allowed", ct).ConfigureAwait(false);
                        return;
                    }
                    break;
            }
            await upstream.FlushAsync(ct).ConfigureAwait(false);
            chunk = await reader.ReadAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task PumpToClientAsync(RtspReader reader, Stream client, RtspRewriter rewriter, CancellationToken ct)
    {
        while (await reader.ReadAsync(ct).ConfigureAwait(false) is { } chunk)
        {
            var bytes = chunk switch
            {
                RtspInterleaved frame => frame.Raw,
                RtspText response => rewriter.ToClient(response).ToBytes(),
                _ => [],
            };
            await client.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    private static async Task RejectAsync(Stream client, RtspText? request, string status, CancellationToken ct)
    {
        var cseq = request?.Header("CSeq");
        var response = $"RTSP/1.0 {status}\r\n" +
                       (cseq is not null && cseq.All(char.IsAsciiDigit) ? $"CSeq: {cseq}\r\n" : "") + "\r\n";
        await client.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
    }

    private void PruneExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var (token, session) in _sessions.Where(s => s.Value.Expires <= now).ToList())
        {
            if (_sessions.TryRemove(token, out _))
                session.Closed.Cancel();
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

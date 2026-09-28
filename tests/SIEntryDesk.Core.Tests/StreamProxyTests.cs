using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Video;

namespace SIEntryDesk.Core.Tests;

/// <summary>Ende-zu-Ende mit einem nachgebauten RTSPS-Server auf localhost.</summary>
public sealed class StreamProxyTests : IAsyncLifetime
{
    private const string StreamPath = "/StreamPfad0001";
    private readonly X509Certificate2 _certificate = CreateCertificate();
    private readonly TcpListener _server = new(IPAddress.Loopback, 0);
    private readonly List<string> _requestsSeen = [];
    private Task? _serverLoop;

    private int ServerPort => ((IPEndPoint)_server.LocalEndpoint).Port;
    private string UpstreamPrefix => $"rtsps://127.0.0.1:{ServerPort}{StreamPath}";

    public Task InitializeAsync()
    {
        _server.Start();
        _serverLoop = ServeAsync();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _server.Stop();
        if (_serverLoop is not null)
            await Task.WhenAny(_serverLoop, Task.Delay(1000));
        _certificate.Dispose();
    }

    [Fact]
    public async Task Stream_flows_through_with_the_address_rewritten()
    {
        await using var proxy = NewProxy(CertificatePin.Parse(Convert.ToHexString(SHA256.HashData(_certificate.RawData))));
        var url = proxy.Open("ruf-1", new StreamSource("127.0.0.1", ServerPort, StreamPath), TimeSpan.FromMinutes(1));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = client.GetStream();
        await Send(stream, $"DESCRIBE {url} RTSP/1.0\r\nCSeq: 2\r\n\r\n");
        var reader = new RtspReader(stream);

        var describe = Assert.IsType<RtspText>(await reader.ReadAsync(Timeout()));
        Assert.Equal(url + "/", describe.Header("Content-Base"));
        Assert.DoesNotContain(StreamPath, Encoding.Latin1.GetString(describe.ToBytes()));

        var frame = Assert.IsType<RtspInterleaved>(await reader.ReadAsync(Timeout()));
        Assert.Equal(new byte[] { (byte)'$', 0, 0, 2, 7, 7 }, frame.Raw);

        lock (_requestsSeen)
            Assert.Contains($"DESCRIBE {UpstreamPrefix} RTSP/1.0", _requestsSeen);
    }

    [Fact]
    public async Task Wrong_pin_means_no_stream()
    {
        await using var proxy = NewProxy(CertificatePin.Parse(new string('A', 64)));
        var url = proxy.Open("ruf-1", new StreamSource("127.0.0.1", ServerPort, StreamPath), TimeSpan.FromMinutes(1));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        await Send(client.GetStream(), $"DESCRIBE {url} RTSP/1.0\r\nCSeq: 2\r\n\r\n");

        Assert.Null(await new RtspReader(client.GetStream()).ReadAsync(Timeout()));
        lock (_requestsSeen)
            Assert.Empty(_requestsSeen);
    }

    [Fact]
    public async Task Unknown_or_closed_addresses_get_404()
    {
        await using var proxy = NewProxy(CertificatePin.Parse(Convert.ToHexString(SHA256.HashData(_certificate.RawData))));
        var url = proxy.Open("ruf-1", new StreamSource("127.0.0.1", ServerPort, StreamPath), TimeSpan.FromMinutes(1));
        proxy.Close("ruf-1");

        foreach (var target in new[] { url, $"rtsp://127.0.0.1:{proxy.Port}/{new string('x', 32)}" })
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
            await Send(client.GetStream(), $"OPTIONS {target} RTSP/1.0\r\nCSeq: 7\r\n\r\n");
            var response = Assert.IsType<RtspText>(await new RtspReader(client.GetStream()).ReadAsync(Timeout()));
            Assert.Equal("RTSP/1.0 404 Not Found", response.StartLine);
            Assert.Equal("7", response.Header("CSeq"));
        }
    }

    private static StreamProxy NewProxy(CertificatePin pin)
    {
        var proxy = new StreamProxy(pin, NullLogger.Instance, TimeProvider.System);
        proxy.Start();
        return proxy;
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static Task Send(Stream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    /// <summary>Antwortet auf DESCRIBE mit Content-Base und SDP und schickt danach einen RTP-Rahmen.</summary>
    private async Task ServeAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _server.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await using var tls = new SslStream(client.GetStream());
                        await tls.AuthenticateAsServerAsync(_certificate);
                        var reader = new RtspReader(tls);
                        if (await reader.ReadAsync(Timeout()) is not RtspText request)
                            return;
                        lock (_requestsSeen)
                            _requestsSeen.Add(request.StartLine);
                        var sdp = Encoding.ASCII.GetBytes("v=0\r\na=control:trackID=0\r\n");
                        await Send(tls, $"RTSP/1.0 200 OK\r\nCSeq: 2\r\nContent-Base: {UpstreamPrefix}/\r\n" +
                                        $"Content-Length: {sdp.Length}\r\n\r\n");
                        await tls.WriteAsync(sdp);
                        await tls.WriteAsync(new byte[] { (byte)'$', 0, 0, 2, 7, 7 });
                        await tls.FlushAsync();
                        await Task.Delay(500);
                    }
                    catch (Exception)
                    {
                        // Abgelehnte Handshakes sind Teil der Tests.
                    }
                }
            });
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=unifi.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Auf macOS braucht der TLS-Server ein Zertifikat mit exportierbarem Schlüssel.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12, "t"), "t");
    }
}

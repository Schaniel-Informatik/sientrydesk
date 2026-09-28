using System.Text;
using SIEntryDesk.Core.Video;

namespace SIEntryDesk.Core.Tests;

public class RtspTests
{
    private const string Local = "rtsp://127.0.0.1:5000/abcdefghijklmnopqrstuvwxyz012345";
    private const string Upstream = "rtsps://192.0.2.1:7441/StreamPfad0001";

    private static RtspText Request(string line, params string[] headers) =>
        new(string.Join("\r\n", [line, .. headers]), []);

    [Fact]
    public async Task Reader_splits_text_and_interleaved_frames()
    {
        var body = "v=0\r\na=control:trackID=0\r\n"u8.ToArray();
        var bytes = Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\nCSeq: 2\r\nContent-Length: {body.Length}\r\n\r\n")
            .Concat(body)
            .Concat(new byte[] { (byte)'$', 4, 0, 3, 1, 2, 3 })
            .ToArray();
        var reader = new RtspReader(new MemoryStream(bytes));

        var text = Assert.IsType<RtspText>(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("RTSP/1.0 200 OK", text.StartLine);
        Assert.Equal("2", text.Header("cseq"));
        Assert.Equal(body, text.Body);

        var frame = Assert.IsType<RtspInterleaved>(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(new byte[] { (byte)'$', 4, 0, 3, 1, 2, 3 }, frame.Raw);

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Content-Length: 999999")]
    [InlineData("Content-Length: -1")]
    [InlineData("Content-Length: zehn")]
    public async Task Reader_rejects_bad_lengths(string header)
    {
        var reader = new RtspReader(new MemoryStream(Encoding.ASCII.GetBytes($"RTSP/1.0 200 OK\r\n{header}\r\n\r\n")));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_rejects_endless_heads()
    {
        var reader = new RtspReader(new MemoryStream(Encoding.ASCII.GetBytes("OPTIONS " + new string('a', 20000))));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    private const string TcpTransport = "Transport: RTP/AVP/TCP;unicast;interleaved=4-5";

    [Theory]
    [InlineData("OPTIONS " + Local + " RTSP/1.0")]
    [InlineData("DESCRIBE " + Local + " RTSP/1.0")]
    [InlineData("SETUP " + Local + "/trackID=2 RTSP/1.0")]
    [InlineData("PLAY " + Local + "/ RTSP/1.0")]
    [InlineData("TEARDOWN " + Local + "/ RTSP/1.0")]
    [InlineData("GET_PARAMETER " + Local + "/ RTSP/1.0")]
    public void Playback_requests_are_rewritten(string line)
    {
        var rewriter = new RtspRewriter(Local, Upstream);
        var result = Assert.IsType<Forward>(rewriter.ToUpstream(Request(line, "CSeq: 1", TcpTransport))).Request;
        Assert.Contains(Upstream, result.StartLine);
        Assert.DoesNotContain(Local, result.Head);
    }

    [Theory]
    [InlineData("Transport: RTP/AVP;unicast;client_port=50000-50001")]
    [InlineData("Transport: RTP/AVP/UDP;unicast;client_port=50000-50001")]
    [InlineData("Transport: RTP/AVP;multicast")]
    [InlineData("Transport: RTP/AVP;unicast;client_port=50000-50001,RTP/AVP/TCP;unicast;interleaved=0-1")]
    [InlineData("CSeq: 3")]
    public void Media_outside_the_tls_connection_is_refused(string transportHeader)
    {
        var result = new RtspRewriter(Local, Upstream).ToUpstream(Request("SETUP " + Local + "/trackID=0 RTSP/1.0", transportHeader));
        var refuse = Assert.IsType<Refuse>(result);
        Assert.Equal("461 Unsupported Transport", refuse.Status);
        Assert.False(refuse.Close);
    }

    [Theory]
    [InlineData("ANNOUNCE " + Local + " RTSP/1.0")]
    [InlineData("RECORD " + Local + " RTSP/1.0")]
    [InlineData("DESCRIBE " + Local + "/../AndererPfad RTSP/1.0")]
    [InlineData("DESCRIBE rtsp://127.0.0.1:5000/fremdertoken RTSP/1.0")]
    [InlineData("DESCRIBE " + Local + "?x=1 RTSP/1.0")]
    [InlineData("DESCRIBE " + Local + " HTTP/1.1")]
    [InlineData("DESCRIBE " + Local)]
    public void Everything_else_is_refused(string line)
    {
        var refuse = Assert.IsType<Refuse>(new RtspRewriter(Local, Upstream).ToUpstream(Request(line, "CSeq: 1")));
        Assert.True(refuse.Close);
    }

    [Fact]
    public void Responses_hide_the_real_address_and_fix_the_length()
    {
        var sdp = Encoding.Latin1.GetBytes($"v=0\r\na=control:{Upstream}/trackID=0\r\n");
        var response = new RtspText(
            $"RTSP/1.0 200 OK\r\nCSeq: 2\r\nContent-Base: {Upstream}/\r\nContent-Length: {sdp.Length}", sdp);

        var result = new RtspRewriter(Local, Upstream).ToClient(response);

        Assert.Equal(Local + "/", result.Header("Content-Base"));
        Assert.DoesNotContain(Upstream, Encoding.Latin1.GetString(result.Body));
        Assert.Equal(result.Body.Length.ToString(), result.Header("Content-Length"));
        Assert.EndsWith("\r\n\r\n" + Encoding.Latin1.GetString(result.Body), Encoding.Latin1.GetString(result.ToBytes()));
    }
}

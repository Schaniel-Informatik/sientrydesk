using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SIEntryDesk.Core.Video;

/// <summary>What happens with a request from the app.</summary>
internal abstract record RewriteResult;

/// <summary>Pass on to the console, rewritten.</summary>
internal sealed record Forward(RtspText Request) : RewriteResult;

/// <summary>Reject with this status. Close: end the connection afterwards.</summary>
internal sealed record Refuse(string Status, bool Close) : RewriteResult;

/// <summary>
/// Translates between the local one-time address (rtsp://127.0.0.1:port/token) and the real stream address
/// (rtsps://console:7441/path). The app never sees the real address. Requests are limited to playback and may only
/// concern the own stream.
/// </summary>
internal sealed partial class RtspRewriter(string localPrefix, string upstreamPrefix)
{
    // ANNOUNCE and RECORD (sending) deliberately not: the Protect server would accept them.
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.Ordinal) { "OPTIONS", "DESCRIBE", "SETUP", "PLAY", "PAUSE", "TEARDOWN", "GET_PARAMETER" };

    public string LocalPrefix => localPrefix;

    /// <summary>Request from the app to the console: pass on or reject.</summary>
    public RewriteResult ToUpstream(RtspText request)
    {
        var parts = request.StartLine.Split(' ');
        if (parts.Length != 3 || !AllowedMethods.Contains(parts[0]) || parts[2] != "RTSP/1.0" || !IsOwnStream(parts[1]))
            return new Refuse("405 Method Not Allowed", Close: true);

        // Video and audio must go through the pinned TLS connection. With UDP the console would send them unencrypted
        // directly to the PC, bypassing the proxy and the certificate check.
        if (parts[0] == "SETUP" && !IsInterleavedOnly(request.Header("Transport")))
            return new Refuse("461 Unsupported Transport", Close: false);

        return new Forward(request with { Head = request.Head.Replace(localPrefix, upstreamPrefix, StringComparison.Ordinal) });
    }

    /// <summary>Every offered variant must be RTP over TCP within the RTSP connection.</summary>
    private static bool IsInterleavedOnly(string? transport) =>
        !string.IsNullOrWhiteSpace(transport) &&
        transport.Split(',').All(option =>
            option.Contains("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase) &&
            option.Contains("interleaved=", StringComparison.OrdinalIgnoreCase) &&
            !option.Contains("client_port", StringComparison.OrdinalIgnoreCase) &&
            !option.Contains("multicast", StringComparison.OrdinalIgnoreCase));

    /// <summary>Response from the console to the app, real address replaced by the local one.</summary>
    public RtspText ToClient(RtspText response)
    {
        var head = response.Head.Replace(upstreamPrefix, localPrefix, StringComparison.Ordinal);
        var body = response.Body;
        if (body.Length > 0)
        {
            var text = Encoding.Latin1.GetString(body);
            if (text.Contains(upstreamPrefix, StringComparison.Ordinal))
            {
                body = Encoding.Latin1.GetBytes(text.Replace(upstreamPrefix, localPrefix, StringComparison.Ordinal));
                head = ContentLengthLine().Replace(head, "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture));
            }
        }
        return new RtspText(head, body);
    }

    /// <summary>"*" or the local address, optionally with a track such as "/trackID=2". No "..", no special
    /// characters.</summary>
    private bool IsOwnStream(string url)
    {
        if (url == "*")
            return true;
        if (!url.StartsWith(localPrefix, StringComparison.Ordinal))
            return false;
        var rest = url[localPrefix.Length..];
        return !rest.Contains("..", StringComparison.Ordinal) && SuffixPattern().IsMatch(rest);
    }

    [GeneratedRegex(@"^(/[A-Za-z0-9=_.\-]*)*$")]
    private static partial Regex SuffixPattern();

    [GeneratedRegex(@"(?im)^Content-Length:[^\r\n]*")]
    private static partial Regex ContentLengthLine();
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SIEntryDesk.Core.Video;

/// <summary>Was mit einer Anfrage der App geschieht.</summary>
internal abstract record RewriteResult;

/// <summary>Umgeschrieben an die Konsole weitergeben.</summary>
internal sealed record Forward(RtspText Request) : RewriteResult;

/// <summary>Mit diesem Status ablehnen. Close: Verbindung danach beenden.</summary>
internal sealed record Refuse(string Status, bool Close) : RewriteResult;

/// <summary>
/// Übersetzt zwischen der lokalen Einmal-Adresse (rtsp://127.0.0.1:port/token) und der echten Stream-Adresse
/// (rtsps://konsole:7441/pfad). Die App sieht die echte Adresse nie. Anfragen sind auf Abspielen beschränkt
/// und dürfen nur den eigenen Stream betreffen.
/// </summary>
internal sealed partial class RtspRewriter(string localPrefix, string upstreamPrefix)
{
    // ANNOUNCE und RECORD (Senden) bewusst nicht: Der Protect-Server nähme sie an.
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.Ordinal) { "OPTIONS", "DESCRIBE", "SETUP", "PLAY", "PAUSE", "TEARDOWN", "GET_PARAMETER" };

    public string LocalPrefix => localPrefix;

    /// <summary>Anfrage der App an die Konsole: weitergeben oder ablehnen.</summary>
    public RewriteResult ToUpstream(RtspText request)
    {
        var parts = request.StartLine.Split(' ');
        if (parts.Length != 3 || !AllowedMethods.Contains(parts[0]) || parts[2] != "RTSP/1.0" || !IsOwnStream(parts[1]))
            return new Refuse("405 Method Not Allowed", Close: true);

        // Bild und Ton müssen durch die gepinnte TLS-Verbindung laufen. Bei UDP schickte die Konsole sie
        // unverschlüsselt direkt an den PC, am Proxy und an der Zertifikatsprüfung vorbei.
        if (parts[0] == "SETUP" && !IsInterleavedOnly(request.Header("Transport")))
            return new Refuse("461 Unsupported Transport", Close: false);

        return new Forward(request with { Head = request.Head.Replace(localPrefix, upstreamPrefix, StringComparison.Ordinal) });
    }

    /// <summary>Jede angebotene Variante muss RTP über TCP in der RTSP-Verbindung sein.</summary>
    private static bool IsInterleavedOnly(string? transport) =>
        !string.IsNullOrWhiteSpace(transport) &&
        transport.Split(',').All(option =>
            option.Contains("RTP/AVP/TCP", StringComparison.OrdinalIgnoreCase) &&
            option.Contains("interleaved=", StringComparison.OrdinalIgnoreCase) &&
            !option.Contains("client_port", StringComparison.OrdinalIgnoreCase) &&
            !option.Contains("multicast", StringComparison.OrdinalIgnoreCase));

    /// <summary>Antwort der Konsole an die App, echte Adresse durch die lokale ersetzt.</summary>
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

    /// <summary>"*" oder die lokale Adresse, optional mit Spurangabe wie "/trackID=2". Kein "..", keine Sonderzeichen.</summary>
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

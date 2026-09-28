using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SIEntryDesk.Core.Video;

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

    /// <summary>Anfrage der App an die Konsole. Null, wenn sie nicht erlaubt ist.</summary>
    public RtspText? ToUpstream(RtspText request)
    {
        var parts = request.StartLine.Split(' ');
        if (parts.Length != 3 || !AllowedMethods.Contains(parts[0]) || parts[2] != "RTSP/1.0")
            return null;
        if (!IsOwnStream(parts[1]))
            return null;
        return request with { Head = request.Head.Replace(localPrefix, upstreamPrefix, StringComparison.Ordinal) };
    }

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

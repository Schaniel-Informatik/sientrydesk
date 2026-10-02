using System.Net;
using System.Net.Sockets;

namespace SIEntryDesk.Core.Talk;

/// <summary>
/// Ziel einer Talkback-Sitzung von Protect: die Türstation selbst (rtp://&lt;IP&gt;:&lt;Port&gt;), Opus in dieser
/// Abtastrate. Die Antwort von Protect gilt als nicht vertrauenswürdig: nur eine IP-Adresse im lokalen Netz, damit
/// eine manipulierte Antwort den Ton des Mikrofons nicht ins Internet lenkt.
/// </summary>
public sealed record TalkbackTarget(IPAddress Address, int Port, int SamplingRate)
{
    public IPEndPoint EndPoint => new(Address, Port);

    /// <summary>Prüft die Antwort von POST /cameras/{id}/talkback-session. Problem ist für das Protokoll gedacht.</summary>
    public static TalkbackTarget? TryCreate(string? url, string? codec, int samplingRate, out string problem)
    {
        if (!string.Equals(codec, "opus", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"Türstation verlangt {UntrustedText.Clean(codec, 20)}, unterstützt ist nur Opus";
            return null;
        }
        if (samplingRate is not (8000 or 12000 or 16000 or 24000 or 48000))
        {
            problem = $"Abtastrate {samplingRate} passt nicht zu Opus";
            return null;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "rtp" ||
            uri.Port is < 1 or > 65535 || uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
        {
            problem = "Ziel ist keine Adresse der Form rtp://IP:Port";
            return null;
        }
        if (!IsLocal(address))
        {
            problem = "Ziel liegt nicht im lokalen Netz";
            return null;
        }
        problem = string.Empty;
        return new TalkbackTarget(address, uri.Port, samplingRate);
    }

    /// <summary>Private Bereiche nach RFC 1918, Link-Local und Unique Local (IPv6).</summary>
    internal static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (b[0] & 0xFE) == 0xFC;
        }
        return false;
    }
}

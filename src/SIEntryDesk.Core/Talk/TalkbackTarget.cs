using System.Net;
using System.Net.Sockets;

namespace SIEntryDesk.Core.Talk;

/// <summary>
/// Target of a Protect talkback session: the door station itself (rtp://&lt;IP&gt;:&lt;Port&gt;), Opus at this sample
/// rate. The response from Protect is considered untrusted: only an IP address in the local network, so that a
/// manipulated response cannot send the microphone audio to the internet.
/// </summary>
public sealed record TalkbackTarget(IPAddress Address, int Port, int SamplingRate)
{
    public IPEndPoint EndPoint => new(Address, Port);

    /// <summary>Checks the response of POST /cameras/{id}/talkback-session. Problem is meant for the log.</summary>
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

    /// <summary>Private ranges according to RFC 1918, link-local and unique local (IPv6).</summary>
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

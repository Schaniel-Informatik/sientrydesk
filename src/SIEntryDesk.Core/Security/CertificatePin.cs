using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SIEntryDesk.Core.Security;

/// <summary>
/// SHA-256 fingerprint of a server certificate. The UniFi console uses self-signed certificates, so instead of a
/// certificate authority exactly this one certificate is accepted. TLS validation is never switched off.
/// </summary>
public sealed class CertificatePin
{
    private readonly byte[] _sha256;

    private CertificatePin(byte[] sha256) => _sha256 = sha256;

    /// <summary>Accepts hex with or without separators (colon, space, hyphen).</summary>
    public static CertificatePin Parse(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var hex = new StringBuilder(64);
        foreach (var c in fingerprint.Trim())
        {
            if (char.IsAsciiHexDigit(c))
                hex.Append(c);
            else if (c is not (':' or ' ' or '-'))
                throw new FormatException("Der Fingerprint darf nur Hex-Zeichen und Trennzeichen enthalten.");
        }
        if (hex.Length != 64)
            throw new FormatException("Kein SHA-256-Fingerprint, erwartet werden 64 Hex-Zeichen.");
        return new CertificatePin(Convert.FromHexString(hex.ToString()));
    }

    public bool Matches(X509Certificate? certificate) =>
        certificate is not null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), _sha256);

    /// <summary>Callback for HttpClient and ClientWebSocket. Chain errors are normal with self-signed certificates and
    /// do not count, only the fingerprint decides.</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        Matches(certificate);

    public override string ToString() => Format(_sha256);

    /// <summary>Fingerprint of a presented certificate, for understandable error messages.</summary>
    public static string FingerprintOf(X509Certificate certificate) => Format(SHA256.HashData(certificate.GetRawCertData()));

    public static string Format(ReadOnlySpan<byte> sha256) =>
        string.Join(':', Convert.ToHexString(sha256).Chunk(2).Select(pair => new string(pair)));
}

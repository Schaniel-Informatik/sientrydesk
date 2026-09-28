using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SIEntryDesk.Core.Security;

/// <summary>
/// SHA-256-Fingerprint eines Serverzertifikats. Die UniFi-Konsole verwendet selbst ausgestellte Zertifikate,
/// deshalb wird statt einer Zertifizierungsstelle genau dieses eine Zertifikat akzeptiert.
/// Die TLS-Prüfung wird nie abgeschaltet.
/// </summary>
public sealed class CertificatePin
{
    private readonly byte[] _sha256;

    private CertificatePin(byte[] sha256) => _sha256 = sha256;

    /// <summary>Akzeptiert Hex mit oder ohne Trennzeichen (Doppelpunkt, Leerzeichen, Bindestrich).</summary>
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

    /// <summary>Callback für HttpClient und ClientWebSocket. Kettenfehler sind bei selbst ausgestellten
    /// Zertifikaten normal und zählen nicht, entscheidend ist allein der Fingerprint.</summary>
    public bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        Matches(certificate);

    public override string ToString() => Format(_sha256);

    /// <summary>Fingerprint eines vorgelegten Zertifikats, für verständliche Fehlermeldungen.</summary>
    public static string FingerprintOf(X509Certificate certificate) => Format(SHA256.HashData(certificate.GetRawCertData()));

    public static string Format(ReadOnlySpan<byte> sha256) =>
        string.Join(':', Convert.ToHexString(sha256).Chunk(2).Select(pair => new string(pair)));
}

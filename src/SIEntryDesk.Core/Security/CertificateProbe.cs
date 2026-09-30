using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace SIEntryDesk.Core.Security;

/// <summary>
/// Liest den Fingerabdruck des Zertifikats, das ein Server vorlegt, ohne ihm zu vertrauen. Nur zum Anzeigen,
/// damit ein Admin den Pin prüfen und übernehmen kann. Über diese Verbindung wird nichts gesendet.
/// </summary>
public static class CertificateProbe
{
    public static async Task<string> FetchFingerprintAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        X509Certificate? presented = null;
        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                presented = certificate;
                return true;
            },
        }, cts.Token).ConfigureAwait(false);
        return presented is null
            ? throw new InvalidOperationException("Kein Zertifikat erhalten")
            : CertificatePin.FingerprintOf(presented);
    }
}

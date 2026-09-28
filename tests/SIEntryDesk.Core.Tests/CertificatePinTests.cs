using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SIEntryDesk.Core.Security;

namespace SIEntryDesk.Core.Tests;

public class CertificatePinTests
{
    private static X509Certificate2 CreateSelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void Matches_only_the_pinned_certificate()
    {
        using var pinned = CreateSelfSigned("unifi.local");
        using var other = CreateSelfSigned("unifi.local");
        var pin = CertificatePin.Parse(Convert.ToHexString(SHA256.HashData(pinned.RawData)));

        Assert.True(pin.Matches(pinned));
        Assert.False(pin.Matches(other));
        Assert.False(pin.Matches(null));
    }

    [Theory]
    [InlineData("a1:b2:c3:d4:e5:f6:07:18:29:3a:4b:5c:6d:7e:8f:90:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff:00")]
    [InlineData("A1B2C3D4E5F60718293A4B5C6D7E8F90112233445566778899AABBCCDDEEFF00")]
    [InlineData(" A1 B2 C3 D4 E5 F6 07 18 29 3A 4B 5C 6D 7E 8F 90 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF 00 ")]
    public void Parse_accepts_common_notations(string value)
    {
        Assert.Equal(
            "A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00",
            CertificatePin.Parse(value).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("A1B2C3D4")]
    [InlineData("sha256 A1B2C3D4E5F60718293A4B5C6D7E8F90112233445566778899AABBCCDDEEFF00")]
    [InlineData("A1B2C3D4E5F60718293A4B5C6D7E8F90112233445566778899AABBCCDDEEFF0000")]
    public void Parse_rejects_anything_else(string value)
    {
        Assert.Throws<FormatException>(() => CertificatePin.Parse(value));
    }
}

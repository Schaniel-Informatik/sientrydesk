using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SIEntryDesk.Core.Peers;

/// <summary>Someone answered a doorbell call by talking: call (Access request_id), user and PC.</summary>
public sealed record PeerAnnouncement(string CallId, string User, string Pc, DateTimeOffset At)
{
    /// <summary>Millisecond precision, as on the wire, so that a local announcement equals the received one.</summary>
    public static PeerAnnouncement Create(string callId, string user, string pc, DateTimeOffset now) =>
        new(callId, user, pc, DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()));
}

public enum PeerDecodeResult
{
    Ok,
    /// <summary>Wrong format, size or signature. Not from an SI EntryDesk with the same Access token.</summary>
    Invalid,
    /// <summary>Signature valid, but the time differs by more than <see cref="PeerCodec.MaxClockSkew"/>.</summary>
    Stale,
}

/// <summary>
/// UDP message between the services of the PCs in the same network segment (decision 2026-11-03, replaces "no
/// coordination between PCs"). Format: "SIED1", HMAC-SHA256 over "SIED1" and the JSON, JSON. The key is derived from
/// the Access token, which all PCs share. It does not reveal the token, and without the token nobody can forge a message.
/// </summary>
public static class PeerCodec
{
    public const int Port = 47801;
    public const int MaxDatagramBytes = 1024;
    public const int MaxNameLength = 64;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(60);

    private static readonly byte[] Magic = "SIED1"u8.ToArray();
    private const int TagBytes = 32;

    private sealed record Wire(string Call, string User, string Pc, long At);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] DeriveKey(string accessToken) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(accessToken), 32,
            "SIEntryDesk"u8.ToArray(), "peer announcement v1"u8.ToArray());

    public static byte[] Encode(PeerAnnouncement announcement, byte[] key)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new Wire(announcement.CallId, announcement.User, announcement.Pc, announcement.At.ToUnixTimeMilliseconds()), Options);
        var datagram = new byte[Magic.Length + TagBytes + json.Length];
        Magic.CopyTo(datagram, 0);
        json.CopyTo(datagram, Magic.Length + TagBytes);
        Sign(key, datagram, datagram.AsSpan(Magic.Length, TagBytes));
        return datagram;
    }

    /// <summary>Checks size, signature and time. Names are cleaned like all untrusted text.</summary>
    public static PeerDecodeResult TryDecode(ReadOnlySpan<byte> datagram, byte[] key, DateTimeOffset now,
        out PeerAnnouncement? announcement)
    {
        announcement = null;
        if (datagram.Length > MaxDatagramBytes || datagram.Length < Magic.Length + TagBytes + 2 ||
            !datagram[..Magic.Length].SequenceEqual(Magic))
            return PeerDecodeResult.Invalid;

        Span<byte> expected = stackalloc byte[TagBytes];
        Sign(key, datagram, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, datagram.Slice(Magic.Length, TagBytes)))
            return PeerDecodeResult.Invalid;

        Wire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(datagram[(Magic.Length + TagBytes)..], Options);
        }
        catch (JsonException)
        {
            return PeerDecodeResult.Invalid;
        }
        var user = UntrustedText.Clean(wire?.User, MaxNameLength);
        var pc = UntrustedText.Clean(wire?.Pc, MaxNameLength);
        if (wire is null || !UntrustedText.IsSafeId(wire.Call) || user.Length == 0 || pc.Length == 0)
            return PeerDecodeResult.Invalid;

        DateTimeOffset at;
        try
        {
            at = DateTimeOffset.FromUnixTimeMilliseconds(wire.At);
        }
        catch (ArgumentOutOfRangeException)
        {
            return PeerDecodeResult.Invalid;
        }
        if ((now - at).Duration() > MaxClockSkew)
            return PeerDecodeResult.Stale;

        announcement = new PeerAnnouncement(wire.Call, user, pc, at);
        return PeerDecodeResult.Ok;
    }

    /// <summary>HMAC over the magic and the JSON, the tag in between is skipped.</summary>
    private static void Sign(byte[] key, ReadOnlySpan<byte> datagram, Span<byte> tag)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(datagram[..Magic.Length]);
        hmac.AppendData(datagram[(Magic.Length + TagBytes)..]);
        hmac.GetHashAndReset(tag);
    }
}

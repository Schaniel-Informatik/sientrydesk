using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SIEntryDesk.Core.Talk;

/// <summary>
/// RTP-Pakete (RFC 3550) für Opus (RFC 7587): ein Opus-Paket pro RTP-Paket, Zeitstempel immer im Takt 48 kHz,
/// auch wenn mit 24 kHz kodiert wird. Payload-Typ 97 wie ffmpeg, mit dem die Türstation im Machbarkeitstest Ton
/// wiedergegeben hat.
/// </summary>
public sealed class RtpPacketizer
{
    public const int HeaderBytes = 12;
    public const byte OpusPayloadType = 97;
    private const int ClockRate = 48000;

    private readonly uint _ssrc;
    private ushort _sequence;
    private uint _timestamp;
    private bool _first = true;

    public RtpPacketizer() : this(RandomUInt(), (ushort)RandomUInt(), RandomUInt())
    {
    }

    internal RtpPacketizer(uint ssrc, ushort firstSequence, uint firstTimestamp)
    {
        _ssrc = ssrc;
        _sequence = firstSequence;
        _timestamp = firstTimestamp;
    }

    /// <summary>Baut das nächste Paket für ein Opus-Paket, das <paramref name="durationMs"/> Ton enthält.</summary>
    public byte[] Next(ReadOnlySpan<byte> opus, int durationMs = 20)
    {
        var packet = new byte[HeaderBytes + opus.Length];
        packet[0] = 0x80; // Version 2, kein Padding, keine Erweiterung, keine CSRC
        packet[1] = (byte)((_first ? 0x80 : 0x00) | OpusPayloadType); // Marker am Anfang eines Sprechstosses
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), _timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), _ssrc);
        opus.CopyTo(packet.AsSpan(HeaderBytes));

        _first = false;
        _sequence++;
        _timestamp += (uint)(ClockRate / 1000 * durationMs);
        return packet;
    }

    private static uint RandomUInt() => BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
}

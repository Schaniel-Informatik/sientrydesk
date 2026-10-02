using Concentus;
using Concentus.Enums;

namespace SIEntryDesk.Audio;

/// <summary>Kodiert Mono-Rahmen von 20 ms mit Opus für Sprache, wie es die Talkback-Sitzung von Protect verlangt.</summary>
public sealed class OpusVoiceEncoder
{
    /// <summary>Grösstes Opus-Paket laut RFC 6716.</summary>
    public const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;

    static OpusVoiceEncoder()
    {
        // Immer die verwaltete Umsetzung, damit das Verhalten nicht davon abhängt, ob eine libopus installiert ist.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    public OpusVoiceEncoder(int sampleRate, int bitrate = 32000)
    {
        if (!IsSupportedRate(sampleRate))
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Opus kennt nur 8, 12, 16, 24 und 48 kHz");
        SampleRate = sampleRate;
        FrameSamples = sampleRate * VoiceFramer.FrameMilliseconds / 1000;
        _encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP, null);
        _encoder.Bitrate = bitrate;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        // Breitband (Sprache bis 8 kHz, reiner SILK-Modus). Bei 24 kHz wählt Opus sonst den Hybridmodus, der mit
        // Concentus 2.2.2 im Test nicht sauber hin und zurück kam. Für Sprache an der Tür genügt Breitband.
        _encoder.MaxBandwidth = OpusBandwidth.OPUS_BANDWIDTH_WIDEBAND;
    }

    public int SampleRate { get; }

    public int FrameSamples { get; }

    public static bool IsSupportedRate(int rate) => rate is 8000 or 12000 or 16000 or 24000 or 48000;

    /// <summary>Ein Rahmen von genau <see cref="FrameSamples"/> Samples im Bereich -1 bis 1 ergibt ein Opus-Paket.</summary>
    public byte[] Encode(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples)
            throw new ArgumentException($"Ein Rahmen hat {FrameSamples} Samples, nicht {frame.Length}", nameof(frame));
        Span<byte> packet = stackalloc byte[MaxPacketBytes];
        var length = _encoder.Encode(frame, FrameSamples, packet, packet.Length);
        return packet[..length].ToArray();
    }
}

using Concentus;
using Concentus.Enums;

namespace SIEntryDesk.Audio;

/// <summary>Encodes mono frames of 20 ms with Opus for speech, as the Protect talkback session requires.</summary>
public sealed class OpusVoiceEncoder
{
    /// <summary>Largest Opus packet according to RFC 6716.</summary>
    public const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;

    static OpusVoiceEncoder()
    {
        // Always the managed implementation, so that the behavior does not depend on whether a libopus is installed.
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
        // Wideband (speech up to 8 kHz, pure SILK mode). At 24 kHz Opus otherwise chooses hybrid mode, which did not
        // make a clean round trip with Concentus 2.2.2 in the test. Wideband is enough for speech at the door.
        _encoder.MaxBandwidth = OpusBandwidth.OPUS_BANDWIDTH_WIDEBAND;
    }

    public int SampleRate { get; }

    public int FrameSamples { get; }

    public static bool IsSupportedRate(int rate) => rate is 8000 or 12000 or 16000 or 24000 or 48000;

    /// <summary>A frame of exactly <see cref="FrameSamples"/> samples in the range -1 to 1 yields one Opus
    /// packet.</summary>
    public byte[] Encode(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSamples)
            throw new ArgumentException($"Ein Rahmen hat {FrameSamples} Samples, nicht {frame.Length}", nameof(frame));
        Span<byte> packet = stackalloc byte[MaxPacketBytes];
        var length = _encoder.Encode(frame, FrameSamples, packet, packet.Length);
        return packet[..length].ToArray();
    }
}

using System.Buffers;
using Concentus;

namespace SIEntryDesk.Audio;

/// <summary>
/// Macht aus Mikrofondaten (beliebige Abtastrate, ein oder mehrere Kanäle, interleavte float-Samples) Mono-Rahmen von
/// 20 ms in der Abtastrate der Türstation. Ein Rahmen ist das, was Opus pro Paket kodiert.
/// </summary>
public sealed class VoiceFramer
{
    public const int FrameMilliseconds = 20;

    private readonly int _inputRate;
    private readonly int _channels;
    private readonly IResampler? _resampler;
    private readonly float[] _frame;
    private int _filled;

    public VoiceFramer(int inputRate, int channels, int outputRate)
    {
        if (inputRate is < 8000 or > 384000)
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (channels is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (!OpusVoiceEncoder.IsSupportedRate(outputRate))
            throw new ArgumentOutOfRangeException(nameof(outputRate), "Opus kennt nur 8, 12, 16, 24 und 48 kHz");
        _inputRate = inputRate;
        _channels = channels;
        OutputRate = outputRate;
        _frame = new float[FrameSamples];
        if (inputRate != outputRate)
            _resampler = ResamplerFactory.CreateResampler(1, inputRate, outputRate, 5, null);
    }

    public int OutputRate { get; }

    public int FrameSamples => OutputRate * FrameMilliseconds / 1000;

    /// <summary>Nimmt interleavte Samples entgegen und ruft <paramref name="onFrame"/> für jeden vollen Rahmen.</summary>
    public void Push(ReadOnlySpan<float> interleaved, Action<ReadOnlySpan<float>> onFrame)
    {
        var count = interleaved.Length / _channels;
        if (count == 0)
            return;

        var mono = ArrayPool<float>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var sum = 0f;
                for (var c = 0; c < _channels; c++)
                    sum += interleaved[i * _channels + c];
                mono[i] = sum / _channels;
            }

            if (_resampler is null)
            {
                Append(mono.AsSpan(0, count), onFrame);
                return;
            }

            var resampled = ArrayPool<float>.Shared.Rent((int)((long)count * OutputRate / _inputRate) + 64);
            try
            {
                var offset = 0;
                while (offset < count)
                {
                    var inLength = count - offset;
                    var outLength = resampled.Length;
                    _resampler.Process(0, mono.AsSpan(offset, inLength), ref inLength, resampled.AsSpan(), ref outLength);
                    Append(resampled.AsSpan(0, outLength), onFrame);
                    if (inLength == 0 && outLength == 0)
                        break;
                    offset += inLength;
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(resampled);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mono);
        }
    }

    /// <summary>Verwirft einen angefangenen Rahmen, z. B. beim Loslassen der Sprechtaste.</summary>
    public void Reset()
    {
        _filled = 0;
        _resampler?.ResetMem();
    }

    private void Append(ReadOnlySpan<float> samples, Action<ReadOnlySpan<float>> onFrame)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(_frame.Length - _filled, samples.Length);
            samples[..take].CopyTo(_frame.AsSpan(_filled));
            _filled += take;
            samples = samples[take..];
            if (_filled == _frame.Length)
            {
                onFrame(_frame);
                _filled = 0;
            }
        }
    }
}

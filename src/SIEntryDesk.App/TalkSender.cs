using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SIEntryDesk.Audio;

namespace SIEntryDesk.App;

/// <summary>
/// Nimmt das Standardmikrofon für Kommunikation auf (Entscheid 2026-10-02: keine Auswahl in der App), macht daraus
/// Opus-Pakete von 20 ms in der Abtastrate der Türstation und gibt sie der Reihe nach weiter, solange gesprochen wird.
/// </summary>
internal sealed class TalkSender : IDisposable
{
    private readonly WasapiRecorder _recorder;
    private readonly VoiceFramer _framer;
    private readonly OpusVoiceEncoder _encoder;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(25) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _pump;
    private readonly Action<string> _onError;
    private int _disposed;

    private TalkSender(WasapiRecorder recorder, int sampleRate, Func<byte[], Task> send, Action<string> onError)
    {
        _recorder = recorder;
        _onError = onError;
        var format = recorder.WaveFormat;
        _framer = new VoiceFramer(format.SampleRate, format.Channels, sampleRate);
        _encoder = new OpusVoiceEncoder(sampleRate);
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null && Volatile.Read(ref _disposed) == 0)
                _onError("Mikrofon unterbrochen");
        };
        _pump = Task.Run(async () =>
        {
            await foreach (var packet in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                await send(packet).ConfigureAwait(false);
        });
    }

    /// <summary>Startet die Aufnahme. Bei fehlendem Mikrofon oder gesperrtem Zugriff null und eine Meldung.</summary>
    public static TalkSender? Start(int sampleRate, Func<byte[], Task> send, Action<string> onError, out string problem)
    {
        WasapiRecorder? recorder = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var device))
            {
                problem = "Kein Mikrofon gefunden";
                return null;
            }
            // Ereignisgesteuert mit kurzem Puffer, damit der Ton ohne merkliche Verzögerung an die Tür geht. Bewusst
            // ohne Kommunikationsmodus: der würde andere Töne am PC leiser stellen.
            recorder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(20)
                .Build();
            if (!IsSupported(recorder.WaveFormat))
            {
                problem = $"Mikrofonformat {recorder.WaveFormat.Encoding} wird nicht unterstützt";
                recorder.Dispose();
                return null;
            }
            var sender = new TalkSender(recorder, sampleRate, send, onError);
            recorder.StartRecording();
            problem = string.Empty;
            return sender;
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005)
        {
            recorder?.Dispose();
            problem = "Kein Zugriff aufs Mikrofon. Windows: Einstellungen → Datenschutz → Mikrofon → Desktop-Apps zulassen";
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            recorder?.Dispose();
            problem = "Mikrofon lässt sich nicht öffnen";
            return null;
        }
    }

    private static bool IsSupported(WaveFormat format) =>
        Encoding(format) switch
        {
            WaveFormatEncoding.IeeeFloat => format.BitsPerSample == 32,
            WaveFormatEncoding.Pcm => format.BitsPerSample is 16 or 24 or 32,
            _ => false,
        };

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.</summary>
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Bei WaveFormatExtensible steht das eigentliche Format im Unterformat.</summary>
    private static WaveFormatEncoding Encoding(WaveFormat format) =>
        format is WaveFormatExtensible extensible
            ? extensible.SubFormat == IeeeFloatSubFormat ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm
            : format.Encoding;

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (Volatile.Read(ref _disposed) != 0 || data.IsEmpty)
            return;
        var samples = ToFloat(_recorder.WaveFormat, data);
        // Windows meldet Stille (z. B. stummgeschaltetes Mikrofon) per Flag, der Inhalt ist dann ungültig.
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
            Array.Clear(samples);
        _framer.Push(samples, frame => _queue.Writer.TryWrite(_encoder.Encode(frame)));
    }

    private static float[] ToFloat(WaveFormat format, ReadOnlySpan<byte> data)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var result = new float[data.Length / bytesPerSample];
        if (Encoding(format) == WaveFormatEncoding.IeeeFloat)
        {
            MemoryMarshal.Cast<byte, float>(data[..(result.Length * 4)]).CopyTo(result);
            return result;
        }
        for (var i = 0; i < result.Length; i++)
        {
            var sample = data.Slice(i * bytesPerSample, bytesPerSample);
            result[i] = bytesPerSample switch
            {
                2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                3 => ((sample[2] << 24) | (sample[1] << 16) | (sample[0] << 8)) / 2147483648f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
            };
        }
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _recorder.DataAvailable -= OnData;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }
        _recorder.Dispose();
        _queue.Writer.TryComplete();
    }
}

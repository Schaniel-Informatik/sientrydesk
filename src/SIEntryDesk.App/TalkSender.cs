using System.Buffers.Binary;
using System.Diagnostics;
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
/// <remarks>
/// Ablauf (0.7.0, „Sprechen flüssiger“): <see cref="Open"/> beim Drücken, damit kein Wort verloren geht, bis der Dienst
/// die Talkback-Sitzung hat. Bis dahin wird höchstens <see cref="PreRollLimit"/> zwischengespeichert. <see cref="Begin"/>
/// mit der Freigabe sendet den Vorlauf und danach laufend. <see cref="Finish"/> beim Loslassen nimmt noch den Nachlauf
/// auf und meldet das Ende erst nach dem letzten Paket, damit der Dienst die Sitzung nicht vor dem letzten Wort schliesst.
/// Gesendet wird im Takt von 20 ms pro Paket, auch der Vorlauf, damit die Türstation keinen Schwall auf einmal bekommt.
/// </remarks>
internal sealed class TalkSender : IDisposable
{
    public static readonly TimeSpan PreRollLimit = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(600);

    private readonly object _gate = new();
    private readonly WasapiRecorder _recorder;
    private readonly WaveFormat _format;
    private readonly Action<string> _onError;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<float[]> _preRoll = new();
    private int _preRollSamples;
    private VoiceFramer? _framer;
    private OpusVoiceEncoder? _encoder;
    private System.Threading.Timer? _tailTimer;
    private bool _recording = true;
    private Func<Task>? _afterLast;
    private Action? _finished;
    private int _disposed;

    private TalkSender(WasapiRecorder recorder, Action<string> onError)
    {
        _recorder = recorder;
        _format = recorder.WaveFormat;
        _onError = onError;
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null && Volatile.Read(ref _disposed) == 0)
                _onError("Mikrofon unterbrochen");
        };
    }

    /// <summary>Öffnet das Mikrofon und speichert zwischen, bis <see cref="Begin"/> kommt. Bei fehlendem Mikrofon oder
    /// gesperrtem Zugriff null und eine Meldung.</summary>
    public static TalkSender? Open(Action<string> onError, out string problem)
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
            var sender = new TalkSender(recorder, onError);
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

    /// <summary>Freigabe vom Dienst: Vorlauf kodieren und senden, danach laufend. War die Taste schon losgelassen und der
    /// Nachlauf vorbei, wird nur der Vorlauf gesendet und danach das Ende gemeldet.</summary>
    public void Begin(int sampleRate, Func<byte[], Task> send)
    {
        lock (_gate)
        {
            if (_framer is not null || Volatile.Read(ref _disposed) != 0)
                return;
            _framer = new VoiceFramer(_format.SampleRate, _format.Channels, sampleRate);
            _encoder = new OpusVoiceEncoder(sampleRate);
            while (_preRoll.TryDequeue(out var samples))
                Encode(samples);
            _preRollSamples = 0;
            if (!_recording)
                Complete();
        }
        _ = PumpAsync(send, _stop.Token);
    }

    /// <summary>Taste losgelassen: noch <see cref="Tail"/> aufnehmen, dann stoppen. <paramref name="afterLast"/> läuft nach
    /// dem letzten gesendeten Paket (nur nach <see cref="Begin"/>), <paramref name="finished"/> danach bzw. sofort, wenn
    /// nichts zu senden ist.</summary>
    public void Finish(Func<Task> afterLast, Action finished)
    {
        lock (_gate)
        {
            if (!_recording || _tailTimer is not null)
                return;
            _afterLast = afterLast;
            _finished = finished;
            _tailTimer = new System.Threading.Timer(_ => EndRecording(), null, Tail, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Taste im Nachlauf wieder gedrückt: weiter aufnehmen, als wäre nie losgelassen worden. False, wenn die
    /// Aufnahme schon zu Ende ist.</summary>
    public bool Resume()
    {
        lock (_gate)
        {
            if (!_recording || _tailTimer is null)
                return false;
            _tailTimer.Dispose();
            _tailTimer = null;
            _afterLast = null;
            _finished = null;
            return true;
        }
    }

    private void EndRecording()
    {
        lock (_gate)
        {
            if (!_recording || _tailTimer is null)
                return;
            _tailTimer.Dispose();
            _tailTimer = null;
            _recording = false;
            StopRecorder();
            if (_framer is not null)
                Complete();
        }
    }

    /// <summary>Nach dem letzten Paket: Ende melden, dann fertig. Ohne Freigabe gibt es nichts zu senden.</summary>
    private void Complete()
    {
        if (_afterLast is { } afterLast)
            _queue.Writer.TryWrite(afterLast);
        if (_finished is { } finished)
            _queue.Writer.TryWrite(finished);
        _queue.Writer.TryComplete();
    }

    /// <summary>Sendet im Takt von 20 ms pro Paket. Liegt mehr bereit (Vorlauf), wartet jedes Paket auf seinen Takt.</summary>
    private async Task PumpAsync(Func<byte[], Task> send, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long sent = 0;
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (item)
                {
                    case byte[] packet:
                        var wait = TimeSpan.FromMilliseconds(sent * VoiceFramer.FrameMilliseconds) - clock.Elapsed;
                        if (wait > TimeSpan.Zero)
                            await Task.Delay(wait, ct).ConfigureAwait(false);
                        await send(packet).ConfigureAwait(false);
                        sent++;
                        break;
                    case Func<Task> afterLast:
                        await afterLast().ConfigureAwait(false);
                        break;
                    case Action finished:
                        finished();
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
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
        var samples = ToFloat(_format, data);
        // Windows meldet Stille (z. B. stummgeschaltetes Mikrofon) per Flag, der Inhalt ist dann ungültig.
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
            Array.Clear(samples);
        lock (_gate)
        {
            if (!_recording)
                return;
            if (_framer is not null)
            {
                Encode(samples);
                return;
            }
            // Vorlauf: bis zur Freigabe sammeln, das Älteste verwerfen, wenn sie zu lange dauert.
            _preRoll.Enqueue(samples);
            _preRollSamples += samples.Length;
            var limit = (int)(PreRollLimit.TotalSeconds * _format.SampleRate * _format.Channels);
            while (_preRollSamples > limit && _preRoll.TryDequeue(out var dropped))
                _preRollSamples -= dropped.Length;
        }
    }

    private void Encode(float[] samples) =>
        _framer!.Push(samples, frame => _queue.Writer.TryWrite(_encoder!.Encode(frame)));

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

    private void StopRecorder()
    {
        _recorder.DataAvailable -= OnData;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }
    }

    /// <summary>Sofort beenden, ohne Nachlauf und ohne Ende zu melden (Höchstdauer, Fenster zu, Fehler).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_gate)
        {
            _tailTimer?.Dispose();
            _tailTimer = null;
            if (_recording)
                StopRecorder();
            _recording = false;
        }
        _recorder.Dispose();
        _stop.Cancel();
        _queue.Writer.TryComplete();
    }
}

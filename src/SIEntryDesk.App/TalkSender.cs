using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SIEntryDesk.Audio;

namespace SIEntryDesk.App;

/// <summary>
/// Records the default communications microphone (decision 2026-10-02: no selection in the app), turns it into Opus
/// packets of 20 ms at the door station's sample rate and passes them on in order while talking.
/// </summary>
/// <remarks>
/// Flow (0.7.0, "smoother talking"): <see cref="Open"/> on press, so that no word gets lost until the service has the
/// talkback session. Until then at most <see cref="PreRollLimit"/> is buffered. <see cref="Begin"/> with the grant
/// sends the pre-roll and then continuously. <see cref="Finish"/> on release records the tail and reports the end only
/// after the last packet, so that the service does not close the session before the last word. Sending is paced at 20
/// ms per packet, the pre-roll too, so that the door station does not get a burst at once.
/// </remarks>
internal sealed class TalkSender : IDisposable
{
    public static readonly TimeSpan PreRollLimit = TimeSpan.FromSeconds(2);
    /// <summary>Tail after release: 0.6 s in 0.7.x, 0.3 s from 0.8.0, because with speakers the door audio stays muted
    /// that long and the visitor's answer would otherwise get lost (feedback 2026-10-27).</summary>
    public static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(300);

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
    private Action? _recordingEnded;
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

    /// <summary>Opens the microphone and buffers until <see cref="Begin"/> comes. Without a microphone or with blocked
    /// access, null and a message.</summary>
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
            // Event-driven with a short buffer, so that the audio reaches the door without noticeable delay.
            // Deliberately without communications mode: it would lower other sounds on the PC.
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

    /// <summary>Grant from the service: encode and send the pre-roll, then continuously. If the button was already
    /// released and the tail is over, only the pre-roll is sent and then the end is reported.</summary>
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

    /// <summary>Button released: record <see cref="Tail"/> more, then stop. <paramref name="recordingEnded"/> runs as
    /// soon as the microphone is off (from then on the door audio may play again), <paramref name="afterLast"/> after
    /// the last sent packet (only after <see cref="Begin"/>), <paramref name="finished"/> after that.</summary>
    public void Finish(Action recordingEnded, Func<Task> afterLast, Action finished)
    {
        lock (_gate)
        {
            if (!_recording || _tailTimer is not null)
                return;
            _recordingEnded = recordingEnded;
            _afterLast = afterLast;
            _finished = finished;
            _tailTimer = new System.Threading.Timer(_ => EndRecording(), null, Tail, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Button pressed again during the tail: keep recording as if it had never been released. False if the
    /// recording has already ended.</summary>
    public bool Resume()
    {
        lock (_gate)
        {
            if (!_recording || _tailTimer is null)
                return false;
            _tailTimer.Dispose();
            _tailTimer = null;
            _recordingEnded = null;
            _afterLast = null;
            _finished = null;
            return true;
        }
    }

    private void EndRecording()
    {
        Action? recordingEnded;
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
            recordingEnded = _recordingEnded;
        }
        recordingEnded?.Invoke();
    }

    /// <summary>After the last packet: report the end, then done. Without a grant there is nothing to send.</summary>
    private void Complete()
    {
        if (_afterLast is { } afterLast)
            _queue.Writer.TryWrite(afterLast);
        if (_finished is { } finished)
            _queue.Writer.TryWrite(finished);
        _queue.Writer.TryComplete();
    }

    /// <summary>Sends paced at 20 ms per packet. If more is ready (pre-roll), each packet waits for its slot.</summary>
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

    /// <summary>With WaveFormatExtensible the actual format is in the sub format.</summary>
    private static WaveFormatEncoding Encoding(WaveFormat format) =>
        format is WaveFormatExtensible extensible
            ? extensible.SubFormat == IeeeFloatSubFormat ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm
            : format.Encoding;

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (Volatile.Read(ref _disposed) != 0 || data.IsEmpty)
            return;
        var samples = ToFloat(_format, data);
        // Windows reports silence (e.g. muted microphone) by flag, the content is invalid then.
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
            // Pre-roll: collect until the grant, discard the oldest if it takes too long.
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

    /// <summary>Stop immediately, without tail and without reporting the end (maximum duration, window closed,
    /// error).</summary>
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

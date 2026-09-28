using System.IO;
using System.Media;

namespace SIEntryDesk.App;

/// <summary>Klingelton in Schleife. Wird zur Laufzeit erzeugt (zwei Töne, dann Pause), damit keine fremde Audiodatei nötig ist.</summary>
internal sealed class Ringtone : IDisposable
{
    private const int SampleRate = 22050;
    private readonly MemoryStream _wave = CreateWave();
    private readonly SoundPlayer _player;
    private bool _playing;

    public Ringtone() => _player = new SoundPlayer(_wave);

    public void Play()
    {
        if (_playing)
            return;
        _wave.Position = 0;
        _player.PlayLooping();
        _playing = true;
    }

    public void Stop()
    {
        if (!_playing)
            return;
        _player.Stop();
        _playing = false;
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
        _wave.Dispose();
    }

    private static MemoryStream CreateWave()
    {
        var samples = new List<short>();
        void Tone(double frequency, double seconds)
        {
            var count = (int)(SampleRate * seconds);
            for (var i = 0; i < count; i++)
            {
                // Kurzes Ein- und Ausblenden gegen Knacksen, leichter Oberton für einen Glockenklang.
                var t = (double)i / SampleRate;
                var envelope = Math.Min(1.0, Math.Min(i, count - i) / (SampleRate * 0.01)) * Math.Exp(-2.5 * t);
                var value = Math.Sin(2 * Math.PI * frequency * t) + 0.3 * Math.Sin(4 * Math.PI * frequency * t);
                samples.Add((short)(value / 1.3 * envelope * short.MaxValue * 0.6));
            }
        }
        void Silence(double seconds) => samples.AddRange(Enumerable.Repeat((short)0, (int)(SampleRate * seconds)));

        Tone(988, 0.45);
        Tone(784, 0.7);
        Silence(1.0);

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = samples.Count * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (var sample in samples)
                writer.Write(sample);
        }
        stream.Position = 0;
        return stream;
    }
}

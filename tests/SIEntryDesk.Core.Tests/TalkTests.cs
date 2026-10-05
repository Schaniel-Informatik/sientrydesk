using System.Buffers.Binary;
using System.Net;
using Concentus;
using SIEntryDesk.Audio;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;
using SIEntryDesk.Core.Talk;

namespace SIEntryDesk.Core.Tests;

public class TalkTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Talkback_target_from_protect_is_accepted()
    {
        var target = TalkbackTarget.TryCreate("rtp://192.168.1.123:7004", "opus", 24000, out var problem);
        Assert.Equal("", problem);
        Assert.Equal(IPAddress.Parse("192.168.1.123"), target!.Address);
        Assert.Equal(7004, target.Port);
        Assert.Equal(24000, target.SamplingRate);
    }

    [Theory]
    [InlineData("rtp://8.8.8.8:7004", "opus", 24000, "nicht im lokalen Netz")]
    [InlineData("rtp://door.example.com:7004", "opus", 24000, "rtp://IP:Port")]
    [InlineData("http://192.168.1.123:7004", "opus", 24000, "rtp://IP:Port")]
    [InlineData("rtp://192.168.1.123:7004/x", "opus", 24000, "rtp://IP:Port")]
    [InlineData("rtp://192.168.1.123:7004", "aac", 22050, "unterstützt ist nur Opus")]
    [InlineData("rtp://192.168.1.123:7004", "opus", 22050, "passt nicht zu Opus")]
    [InlineData(null, "opus", 24000, "rtp://IP:Port")]
    public void Unusable_talkback_targets_are_refused(string? url, string codec, int rate, string expected)
    {
        Assert.Null(TalkbackTarget.TryCreate(url, codec, rate, out var problem));
        Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:db8::1", false)]
    public void Only_local_addresses_count(string address, bool local) =>
        Assert.Equal(local, TalkbackTarget.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void Rtp_header_follows_rfc_3550_and_7587()
    {
        var rtp = new RtpPacketizer(ssrc: 0xCAFEBABE, firstSequence: 65535, firstTimestamp: 1000);
        var first = rtp.Next([1, 2, 3]);
        var second = rtp.Next([4]);

        Assert.Equal(15, first.Length);
        Assert.Equal(0x80, first[0]);
        Assert.Equal(0x80 | 97, first[1]);
        Assert.Equal(97, second[1]);
        Assert.Equal(65535, BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(second.AsSpan(2)));
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(4)));
        Assert.Equal(1000u + 960u, BinaryPrimitives.ReadUInt32BigEndian(second.AsSpan(4)));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(second.AsSpan(8)));
        Assert.Equal(new byte[] { 1, 2, 3 }, first[12..]);
    }

    [Fact]
    public void Only_one_app_per_door_talks_at_a_time()
    {
        var time = new ManualTime();
        var floor = new TalkFloor(time);

        Assert.True(floor.TryTake("door", "call", clientId: 1, "anna", out _));
        Assert.False(floor.TryTake("door", "call", clientId: 2, "beat", out var busy));
        Assert.Equal("anna", busy.Speaker);
        Assert.True(floor.TryTake("other-door", "call-2", clientId: 2, "beat", out _));
        Assert.True(floor.TryTake("door", "call", clientId: 1, "anna", out _));

        Assert.NotNull(floor.Release("call", 1));
        Assert.True(floor.TryTake("door", "call", clientId: 2, "beat", out _));
    }

    [Fact]
    public void Talking_ends_after_sixty_seconds()
    {
        var time = new ManualTime();
        var floor = new TalkFloor(time);
        floor.TryTake("door", "call", 1, "anna", out _);

        time.Now += TimeSpan.FromSeconds(59);
        Assert.NotNull(floor.Current("call", 1));
        Assert.Empty(floor.Expire());

        time.Now += TimeSpan.FromSeconds(1);
        Assert.Null(floor.Current("call", 1));
        Assert.True(floor.TryTake("door", "call", 2, "beat", out _));
    }

    [Fact]
    public void Disconnect_and_call_end_release_the_floor()
    {
        var floor = new TalkFloor(new ManualTime());
        floor.TryTake("door-1", "call-1", 1, "anna", out _);
        floor.TryTake("door-2", "call-2", 1, "anna", out _);
        floor.TryTake("door-3", "call-3", 2, "beat", out _);

        Assert.Equal(2, floor.ReleaseClient(1).Count);
        Assert.Single(floor.ReleaseCall("call-3"));
        Assert.Null(floor.Current("call-3", 2));
    }

    [Fact]
    public void Talk_messages_survive_the_pipe()
    {
        IpcMessage[] messages =
        [
            new TalkRequest("call", true),
            new TalkResultMessage("call", true, "Sprechen", 24000, 60),
            new TalkAudioMessage("call", Convert.ToBase64String([1, 2, 3])),
            new TalkEndedMessage("call", "60 s erreicht"),
            new CallStartedMessage("call", "Tür", DateTimeOffset.UnixEpoch, true, true, TalkAvailable: true),
        ];
        foreach (var message in messages)
        {
            var bytes = IpcProtocol.Serialize(message);
            Assert.Equal(message, IpcProtocol.Deserialize(bytes.AsSpan(0, bytes.Length - 1)));
        }
    }

    [Fact]
    public void Microphone_audio_becomes_twenty_millisecond_frames_at_the_door_rate()
    {
        // 48 kHz Stereo wie ein typisches Windows-Mikrofon, 1 s Ton bei 440 Hz.
        var framer = new VoiceFramer(48000, 2, 24000);
        var input = new float[48000 * 2];
        for (var i = 0; i < 48000; i++)
            input[2 * i] = input[2 * i + 1] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 48000);

        var frames = new List<float[]>();
        foreach (var chunk in input.Chunk(960)) // 10 ms pro Aufruf, wie WASAPI liefert
            framer.Push(chunk, f => frames.Add(f.ToArray()));

        Assert.Equal(480, framer.FrameSamples);
        Assert.InRange(frames.Count, 48, 50);
        Assert.All(frames, f => Assert.Equal(480, f.Length));
        var peak = frames.Skip(5).SelectMany(f => f).Max(MathF.Abs);
        Assert.InRange(peak, 0.4f, 0.6f);
    }

    [Fact]
    public void Opus_frames_decode_back_to_the_spoken_tone()
    {
        var encoder = new OpusVoiceEncoder(24000);
        var decoder = OpusCodecFactory.CreateDecoder(24000, 1, null);
        var decoded = new List<float>();
        var frame = new float[480];
        for (var n = 0; n < 50; n++)
        {
            for (var i = 0; i < frame.Length; i++)
                frame[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * (n * 480 + i) / 24000f);
            var packet = encoder.Encode(frame);
            Assert.InRange(packet.Length, 1, OpusVoiceEncoder.MaxPacketBytes);
            var output = new float[480];
            Assert.Equal(480, decoder.Decode(packet, output, 480, false));
            decoded.AddRange(output);
        }

        // Nach dem Einschwingen: Pegel und Frequenz (Nulldurchgänge) wie beim Original.
        var steady = decoded.Skip(4800).ToArray();
        var rms = MathF.Sqrt(steady.Sum(s => s * s) / steady.Length);
        Assert.InRange(rms, 0.25f, 0.45f);
        var crossings = steady.Zip(steady.Skip(1)).Count(p => p.First < 0 && p.Second >= 0);
        var hz = crossings * 24000f / steady.Length;
        Assert.InRange(hz, 420f, 460f);
    }

    [Fact]
    public void Encoder_rejects_wrong_frame_sizes()
    {
        var encoder = new OpusVoiceEncoder(24000);
        Assert.Throws<ArgumentException>(() => encoder.Encode(new float[479]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusVoiceEncoder(22050));
    }
}

public class CheckReportTests
{
    [Fact]
    public void Report_lists_areas_with_marks_and_overall_result()
    {
        Diagnostics.CheckItem[] items =
        [
            new(Diagnostics.CheckReport.ThisPc, "Dienst", Diagnostics.CheckLevel.Ok, "läuft"),
            new(Diagnostics.CheckReport.Installation, "Access-Token", Diagnostics.CheckLevel.Fail, "abgelehnt"),
            new(Diagnostics.CheckReport.Installation, "Ablaufdaten", Diagnostics.CheckLevel.Warn, "bald"),
        ];
        var text = Diagnostics.CheckReport.ToText(items, "SI EntryDesk 0.6.0, PC-1", DateTimeOffset.UnixEpoch);
        Assert.Contains("Ergebnis: Fehler", text);
        Assert.Contains("Dieser PC\n  ✓ Dienst: läuft", text.Replace("\r", ""));
        Assert.Contains("  ✗ Access-Token: abgelehnt", text);
        Assert.Equal(Diagnostics.CheckLevel.Ok, Diagnostics.CheckReport.Overall([items[0], items[0] with { Level = Diagnostics.CheckLevel.Info }]));
    }

    [Fact]
    public void Check_result_survives_the_pipe()
    {
        var message = new Ipc.CheckResultMessage([new("Anlage", "Konfiguration", Diagnostics.CheckLevel.Ok, "Konsole x")]);
        var bytes = Ipc.IpcProtocol.Serialize(message);
        var back = Assert.IsType<Ipc.CheckResultMessage>(Ipc.IpcProtocol.Deserialize(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Equal(message.Items, back.Items);
        Assert.IsType<Ipc.CheckRequest>(Ipc.IpcProtocol.Deserialize(Ipc.IpcProtocol.Serialize(new Ipc.CheckRequest()).AsSpan()[..^1]));
    }
}

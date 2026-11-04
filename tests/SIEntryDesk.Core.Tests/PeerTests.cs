using System.Text;
using SIEntryDesk.Core.Peers;

namespace SIEntryDesk.Core.Tests;

public class PeerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = PeerCodec.DeriveKey("test-token-1");

    private static PeerAnnouncement Answer(string user = "amuster", string pc = "PC-01", double seconds = 0, string call = "req-1") =>
        PeerAnnouncement.Create(call, user, pc, Now.AddSeconds(seconds));

    [Fact]
    public void Announcement_round_trips()
    {
        var answer = Answer();
        var result = PeerCodec.TryDecode(PeerCodec.Encode(answer, Key), Key, Now.AddSeconds(2), out var decoded);
        Assert.Equal(PeerDecodeResult.Ok, result);
        Assert.Equal(answer, decoded);
    }

    [Fact]
    public void Key_depends_on_the_access_token_and_does_not_contain_it()
    {
        Assert.Equal(Key, PeerCodec.DeriveKey("test-token-1"));
        Assert.NotEqual(Key, PeerCodec.DeriveKey("test-token-2"));
        Assert.DoesNotContain("test-token-1", Encoding.UTF8.GetString(PeerCodec.Encode(Answer(), Key)));
    }

    [Fact]
    public void Other_token_is_rejected()
    {
        var datagram = PeerCodec.Encode(Answer(), PeerCodec.DeriveKey("other-token"));
        Assert.Equal(PeerDecodeResult.Invalid, PeerCodec.TryDecode(datagram, Key, Now, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void Changed_content_is_rejected()
    {
        var datagram = PeerCodec.Encode(Answer(), Key);
        var text = Encoding.UTF8.GetString(datagram).Replace("amuster", "bmuster");
        Assert.Equal(PeerDecodeResult.Invalid, PeerCodec.TryDecode(Encoding.UTF8.GetBytes(text), Key, Now, out _));
    }

    [Theory]
    [InlineData(61)]
    [InlineData(-61)]
    public void Message_too_far_from_the_own_clock_is_stale(double seconds)
    {
        var datagram = PeerCodec.Encode(Answer(seconds: seconds), Key);
        Assert.Equal(PeerDecodeResult.Stale, PeerCodec.TryDecode(datagram, Key, Now, out _));
    }

    [Fact]
    public void Garbage_and_oversized_datagrams_are_rejected()
    {
        Assert.Equal(PeerDecodeResult.Invalid, PeerCodec.TryDecode([], Key, Now, out _));
        Assert.Equal(PeerDecodeResult.Invalid, PeerCodec.TryDecode("hello"u8, Key, Now, out _));
        var big = PeerCodec.Encode(Answer(user: new string('a', 2000)), Key);
        Assert.Equal(PeerDecodeResult.Invalid, PeerCodec.TryDecode(big, Key, Now, out _));
    }

    [Fact]
    public void Unsafe_call_id_is_rejected_even_when_signed()
    {
        var datagram = PeerCodec.Encode(Answer(call: "../doors/x"), Key);
        Assert.Equal(PeerDecodeResult.Invalid, PeerCodec.TryDecode(datagram, Key, Now, out _));
    }

    [Fact]
    public void Names_are_cleaned_and_shortened()
    {
        var datagram = PeerCodec.Encode(Answer(user: "a‮muster\n" + new string('x', 100)), Key);
        Assert.Equal(PeerDecodeResult.Ok, PeerCodec.TryDecode(datagram, Key, Now, out var decoded));
        Assert.DoesNotContain('‮', decoded!.User);
        Assert.DoesNotContain('\n', decoded.User);
        Assert.True(decoded.User.Length <= PeerCodec.MaxNameLength);
    }

    [Fact]
    public void First_answer_wins_regardless_of_arrival_order()
    {
        var answers = new CallAnswers();
        var late = Answer(user: "bmuster", pc: "PC-02", seconds: 2);
        var early = Answer(user: "amuster", pc: "PC-01", seconds: 1);

        Assert.True(answers.Offer(late));
        Assert.True(answers.Offer(early));
        Assert.False(answers.Offer(late));
        Assert.False(answers.Offer(early));
        Assert.Equal(early, answers.Find("req-1"));
    }

    [Fact]
    public void Same_time_is_decided_by_pc_then_user()
    {
        var answers = new CallAnswers();
        Assert.True(answers.Offer(Answer(pc: "PC-02")));
        Assert.True(answers.Offer(Answer(pc: "PC-01")));
        Assert.False(answers.Offer(Answer(pc: "PC-03")));
        Assert.Equal("PC-01", answers.Find("req-1")!.Pc);
    }

    [Fact]
    public void Ended_calls_are_forgotten()
    {
        var answers = new CallAnswers();
        answers.Offer(Answer(call: "req-1"));
        answers.Offer(Answer(call: "req-2"));
        answers.RemoveWhere(callId => callId == "req-1");
        Assert.Null(answers.Find("req-1"));
        Assert.NotNull(answers.Find("req-2"));
    }
}

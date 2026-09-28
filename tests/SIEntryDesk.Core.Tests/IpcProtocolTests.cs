using System.Text;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.Core.Tests;

public class IpcProtocolTests
{
    public static TheoryData<IpcMessage> Messages() =>
    [
        new StatusMessage(true, "", "0.1.0"),
        new CallStartedMessage("req-1", "Tür 1", new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero), true),
        new CallEndedMessage("req-1", CallEndReason.AnsweredElsewhere),
        new DoorOpenedMessage("req-1", "A. Muster"),
        new UnlockRequest("req-1"),
        new UnlockResultMessage("req-1", false, "Ruf bereits beendet"),
    ];

    [Theory]
    [MemberData(nameof(Messages))]
    public async Task Messages_survive_a_round_trip(IpcMessage message)
    {
        using var stream = new MemoryStream();
        await IpcProtocol.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;

        var read = await new IpcReader(stream).ReadAsync(CancellationToken.None);
        Assert.Equal(message, read);
    }

    [Fact]
    public async Task Unknown_and_broken_lines_are_skipped()
    {
        var text = "{\"type\":\"unbekannt\"}\nkein json\n" +
                   Encoding.UTF8.GetString(IpcProtocol.Serialize(new UnlockRequest("req-2")));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var reader = new IpcReader(stream);
        Assert.Equal(new UnlockRequest("req-2"), await reader.ReadAsync(CancellationToken.None));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Overlong_lines_end_the_connection()
    {
        using var stream = new MemoryStream(new byte[IpcProtocol.MaxMessageBytes + 10]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new IpcReader(stream).ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Several_messages_in_one_read()
    {
        var bytes = IpcProtocol.Serialize(new UnlockRequest("a")).Concat(IpcProtocol.Serialize(new UnlockRequest("b"))).ToArray();
        using var stream = new MemoryStream(bytes);
        var reader = new IpcReader(stream);
        Assert.Equal(new UnlockRequest("a"), await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(new UnlockRequest("b"), await reader.ReadAsync(CancellationToken.None));
    }
}

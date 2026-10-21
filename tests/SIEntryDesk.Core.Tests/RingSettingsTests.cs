using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.Core.Tests;

public class RingSettingsTests
{
    [Theory]
    [InlineData(RingMode.Immediate)]
    [InlineData(RingMode.Delayed)]
    [InlineData(RingMode.Off)]
    public void Stored_values_round_trip(RingMode mode) =>
        Assert.Equal(mode, RingSettings.Parse(RingSettings.ToStored(mode)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OFF")]
    [InlineData("irgendwas")]
    public void Unknown_values_ring_immediately(string? stored) =>
        Assert.Equal(RingMode.Immediate, RingSettings.Parse(stored));

    [Fact]
    public void Summary_names_only_doors_that_do_not_ring_immediately()
    {
        var summary = RingSettings.Summary([("Tür B", RingMode.Off), ("Tür A", RingMode.Immediate), ("Tür C", RingMode.Delayed)]);
        Assert.Equal("Tür B: aus, Tür C: nach 10 s", summary);
    }

    [Fact]
    public void Summary_is_empty_when_all_ring_immediately() =>
        Assert.Null(RingSettings.Summary([("Tür A", RingMode.Immediate)]));
}

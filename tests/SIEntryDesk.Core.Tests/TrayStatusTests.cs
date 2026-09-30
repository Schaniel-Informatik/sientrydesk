using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.Core.Tests;

public class TrayStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private static StatusMessage Status(LinkHealth health, string problem = "", string warning = "") =>
        new(health == LinkHealth.Ready, problem, "0.3.0", health, warning);

    [Fact]
    public void Ready_is_green()
    {
        var state = TrayStatus.Evaluate(true, Status(LinkHealth.Ready), null, Now);
        Assert.Equal(TrayColor.Green, state.Color);
        Assert.False(state.Alarm);
    }

    [Fact]
    public void Missing_service_is_red_with_alarm()
    {
        var state = TrayStatus.Evaluate(false, null, null, Now);
        Assert.Equal(TrayColor.Red, state.Color);
        Assert.True(state.Alarm);
    }

    [Fact]
    public void Errors_are_red_with_alarm_and_explained()
    {
        var state = TrayStatus.Evaluate(true, Status(LinkHealth.Error, "Access lehnt den Token ab"), null, Now);
        Assert.Equal(TrayColor.Red, state.Color);
        Assert.True(state.Alarm);
        Assert.Equal("Access lehnt den Token ab", state.Text);
    }

    [Fact]
    public void Outside_the_office_is_grey_without_alarm()
    {
        var state = TrayStatus.Evaluate(true, Status(LinkHealth.Unreachable), null, Now);
        Assert.Equal(TrayColor.Grey, state.Color);
        Assert.False(state.Alarm);
    }

    [Fact]
    public void Warnings_are_orange()
    {
        var state = TrayStatus.Evaluate(true, Status(LinkHealth.Ready, warning: "Livebild gestört"), null, Now);
        Assert.Equal(TrayColor.Orange, state.Color);
        Assert.Contains("Livebild gestört", state.Text);
    }

    [Fact]
    public void Pause_is_blue_but_red_wins()
    {
        var until = Now.AddMinutes(30);
        Assert.Equal(TrayColor.Blue, TrayStatus.Evaluate(true, Status(LinkHealth.Ready), until, Now).Color);
        Assert.Equal(TrayColor.Blue, TrayStatus.Evaluate(true, Status(LinkHealth.Unreachable), until, Now).Color);
        Assert.Equal(TrayColor.Red, TrayStatus.Evaluate(true, Status(LinkHealth.Error), until, Now).Color);
        Assert.Equal(TrayColor.Red, TrayStatus.Evaluate(false, null, until, Now).Color);
    }

    [Fact]
    public void Expired_pause_counts_as_no_pause()
    {
        Assert.Equal(TrayColor.Green, TrayStatus.Evaluate(true, Status(LinkHealth.Ready), Now.AddSeconds(-1), Now).Color);
    }
}

public class ExpiryWarningTests
{
    private static readonly DateTime Today = new(2026, 9, 30);

    [Fact]
    public void No_dates_no_warning()
    {
        Assert.Null(new EntryDeskOptions().ExpiryWarning(Today));
    }

    [Fact]
    public void Fifteen_days_ahead_no_warning()
    {
        Assert.Null(new EntryDeskOptions { TokenExpires = Today.AddDays(15) }.ExpiryWarning(Today));
    }

    [Fact]
    public void Fourteen_days_ahead_warns()
    {
        Assert.Equal("Die Tokens von SI EntryDesk müssen bis am 14.10.2026 erneuert werden",
            new EntryDeskOptions { TokenExpires = Today.AddDays(14) }.ExpiryWarning(Today));
    }

    [Fact]
    public void Earlier_of_both_dates_counts()
    {
        Assert.Equal("Die Tokens von SI EntryDesk müssen bis am 05.10.2026 erneuert werden",
            new EntryDeskOptions { TokenExpires = Today.AddDays(200), ProtectKeyExpires = Today.AddDays(5) }.ExpiryWarning(Today));
    }

    [Fact]
    public void Expired()
    {
        Assert.Equal("Die Tokens von SI EntryDesk sind am 29.09.2026 abgelaufen",
            new EntryDeskOptions { TokenExpires = Today.AddDays(-1) }.ExpiryWarning(Today));
    }

    [Fact]
    public void Default_live_view_is_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new EntryDeskOptions().LiveViewLifetime);
    }
}

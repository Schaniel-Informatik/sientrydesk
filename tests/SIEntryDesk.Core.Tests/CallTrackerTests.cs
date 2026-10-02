using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.Core.Tests;

public class CallTrackerTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string DoorId = "11111111-2222-4333-8444-555555555555";
    private const string HubId = "0123456789ab";

    private static AccessRingStarted Ring(string id = "req-1", bool unlockingNotAllowed = false, string doorName = "Tür 1") =>
        new(id, DoorId, doorName, HubId, "cam-1", "UVC G6 Pro Entry", IsCamera: true, unlockingNotAllowed, CreatedAt: null);

    [Fact]
    public void Ring_starts_and_ends_a_call()
    {
        var tracker = new CallTracker(new ManualTime());

        var started = Assert.IsType<CallStarted>(Assert.Single(tracker.Apply(Ring())));
        Assert.Equal("cam-1", started.Call.CameraId);
        Assert.Single(tracker.ActiveCalls());

        var ended = Assert.IsType<CallEnded>(Assert.Single(tracker.Apply(new AccessRingEnded("req-1", 400))));
        Assert.Equal(CallEndReason.AnsweredElsewhere, ended.Reason);
        Assert.Empty(tracker.ActiveCalls());
    }

    [Fact]
    public void Duplicate_ring_is_ignored()
    {
        var tracker = new CallTracker(new ManualTime());
        tracker.Apply(Ring());
        Assert.Empty(tracker.Apply(Ring()));
    }

    [Fact]
    public void End_without_request_id_is_ignored()
    {
        var tracker = new CallTracker(new ManualTime());
        tracker.Apply(Ring());
        Assert.Empty(tracker.Apply(new AccessRingEnded(null, 0)));
        Assert.Single(tracker.ActiveCalls());
    }

    [Fact]
    public void Door_filter_drops_other_doors()
    {
        var tracker = new CallTracker(new ManualTime(), r => r.DoorName == "Tür 2");
        Assert.Empty(tracker.Apply(Ring()));
        Assert.Equal(UnlockDecision.UnknownCall, tracker.TryBeginUnlock("req-1", out _));
    }

    [Fact]
    public void Unlock_only_once_while_the_call_runs()
    {
        var tracker = new CallTracker(new ManualTime());
        tracker.Apply(Ring());

        Assert.Equal(UnlockDecision.Allowed, tracker.TryBeginUnlock("req-1", out var call));
        Assert.Equal(DoorId, call!.DoorId);
        Assert.Equal(UnlockDecision.AlreadyRequested, tracker.TryBeginUnlock("req-1", out _));

        tracker.CompleteUnlock("req-1", success: true);
        Assert.Equal(UnlockDecision.AlreadyRequested, tracker.TryBeginUnlock("req-1", out _));
    }

    [Fact]
    public void Failed_unlock_may_be_retried()
    {
        var tracker = new CallTracker(new ManualTime());
        tracker.Apply(Ring());
        tracker.TryBeginUnlock("req-1", out _);
        tracker.CompleteUnlock("req-1", success: false);
        Assert.Equal(UnlockDecision.Allowed, tracker.TryBeginUnlock("req-1", out _));
    }

    [Theory]
    [InlineData(400)] // anderswo angenommen: jemand kümmert sich
    [InlineData(106)] // abgelehnt: bewusst entschieden
    [InlineData(107)]
    public void No_unlock_after_the_call_was_handled(int reason)
    {
        var tracker = new CallTracker(new ManualTime());
        tracker.Apply(Ring());
        tracker.Apply(new AccessRingEnded("req-1", reason));
        Assert.Equal(UnlockDecision.CallEnded, tracker.TryBeginUnlock("req-1", out var call));
        Assert.Null(call);
        Assert.Equal(UnlockDecision.UnknownCall, tracker.TryBeginUnlock("req-unbekannt", out _));
    }

    [Theory]
    [InlineData(108)] // Besucher hat abgebrochen, z. B. zweimal gedrückt
    [InlineData(105)] // niemand hat abgenommen
    public void Unlock_still_possible_shortly_after_cancel_or_timeout(int reason)
    {
        var time = new ManualTime();
        var tracker = new CallTracker(time);
        tracker.Apply(Ring());
        tracker.Apply(new AccessRingEnded("req-1", reason));

        time.Now += TimeSpan.FromSeconds(9);
        Assert.Equal(UnlockDecision.Allowed, tracker.TryBeginUnlock("req-1", out var call));
        Assert.Equal(DoorId, call!.DoorId);
        Assert.Equal(UnlockDecision.AlreadyRequested, tracker.TryBeginUnlock("req-1", out _));
    }

    [Fact]
    public void Grace_period_ends_after_ten_seconds()
    {
        var time = new ManualTime();
        var tracker = new CallTracker(time);
        tracker.Apply(Ring());
        tracker.Apply(new AccessRingEnded("req-1", 108));
        time.Now += TimeSpan.FromSeconds(11);
        Assert.Equal(UnlockDecision.CallEnded, tracker.TryBeginUnlock("req-1", out _));
    }

    [Fact]
    public void No_unlock_in_grace_period_when_already_opened()
    {
        var time = new ManualTime();
        var tracker = new CallTracker(time);
        tracker.Apply(Ring());
        tracker.Apply(new AccessRingEnded("req-1", 108));
        tracker.Apply(new AccessDoorUnlocked(DoorId, HubId, "Tür 1"));
        Assert.Equal(UnlockDecision.AlreadyOpened, tracker.TryBeginUnlock("req-1", out _));
    }

    [Fact]
    public void Unlock_in_progress_survives_the_end_of_the_call()
    {
        // Nach dem Öffnen über die API endet der Ruf mit 108. Das darf kein zweites Öffnen freigeben.
        var tracker = new CallTracker(new ManualTime());
        tracker.Apply(Ring());
        Assert.Equal(UnlockDecision.Allowed, tracker.TryBeginUnlock("req-1", out _));
        tracker.Apply(new AccessRingEnded("req-1", 108));
        Assert.Equal(UnlockDecision.AlreadyRequested, tracker.TryBeginUnlock("req-1", out _));
        tracker.CompleteUnlock("req-1", success: true);
        Assert.Equal(UnlockDecision.AlreadyRequested, tracker.TryBeginUnlock("req-1", out _));
    }

    [Fact]
    public void No_unlock_when_access_forbids_it()
    {
        var tracker = new CallTracker(new ManualTime());
        tracker.Apply(Ring(unlockingNotAllowed: true));
        Assert.Equal(UnlockDecision.NotAllowedByAccess, tracker.TryBeginUnlock("req-1", out _));
    }

    [Fact]
    public void Calls_without_end_expire()
    {
        var time = new ManualTime();
        var tracker = new CallTracker(time);
        tracker.Apply(Ring());

        time.Now += TimeSpan.FromSeconds(60);
        Assert.Empty(tracker.Expire());

        time.Now += TimeSpan.FromSeconds(31);
        var ended = Assert.IsType<CallEnded>(Assert.Single(tracker.Expire()));
        Assert.Equal(CallEndReason.Expired, ended.Reason);
        Assert.Equal(UnlockDecision.UnknownCall, tracker.TryBeginUnlock("req-1", out _));
    }

    [Fact]
    public void Opening_after_the_end_is_reported_with_the_person()
    {
        // Reihenfolge wie im Test beobachtet: Ende 400, dann remote_unlock, dann Protokoll mit Namen.
        var time = new ManualTime();
        var tracker = new CallTracker(time);
        tracker.Apply(Ring());
        tracker.Apply(new AccessRingEnded("req-1", 400));

        time.Now += TimeSpan.FromSeconds(1);
        var opened = Assert.IsType<CallDoorOpened>(Assert.Single(tracker.Apply(new AccessDoorUnlocked(DoorId, HubId, "Tür 1"))));
        Assert.Equal("req-1", opened.Call.CallId);
        Assert.Null(opened.OpenedBy);

        time.Now += TimeSpan.FromSeconds(1);
        var named = Assert.IsType<CallDoorOpened>(Assert.Single(tracker.Apply(new AccessUnlockLogged(HubId, "A. Muster", "CALL"))));
        Assert.Equal("A. Muster", named.OpenedBy);

        Assert.Empty(tracker.Apply(new AccessDoorUnlocked(DoorId, HubId, "Tür 1")));
    }

    [Fact]
    public void Old_openings_are_not_attributed()
    {
        var time = new ManualTime();
        var tracker = new CallTracker(time);
        tracker.Apply(Ring());
        tracker.Apply(new AccessRingEnded("req-1", 105));

        time.Now += TimeSpan.FromSeconds(16);
        Assert.Empty(tracker.Apply(new AccessDoorUnlocked(DoorId, HubId, "Tür 1")));
    }

    [Fact]
    public void Talking_is_allowed_during_the_call_and_briefly_after()
    {
        var time = new ManualTime();
        var tracker = new CallTracker(time);
        Assert.Equal(TalkDecision.UnknownCall, tracker.CanTalk("req-1", out _));

        tracker.Apply(Ring());
        Assert.Equal(TalkDecision.Allowed, tracker.CanTalk("req-1", out var call));
        Assert.Equal(DoorId, call!.DoorId);

        tracker.Apply(new AccessRingEnded("req-1", 107));
        time.Now += TimeSpan.FromSeconds(9);
        Assert.Equal(TalkDecision.Allowed, tracker.CanTalk("req-1", out _));
        time.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(TalkDecision.CallEnded, tracker.CanTalk("req-1", out _));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(106)]
    public void No_talking_after_answered_elsewhere_or_declined(int reason)
    {
        var tracker = new CallTracker(new ManualTime());
        tracker.Apply(Ring());
        tracker.Apply(new AccessRingEnded("req-1", reason));
        Assert.Equal(TalkDecision.CallEnded, tracker.CanTalk("req-1", out var call));
        Assert.Null(call);
    }
}

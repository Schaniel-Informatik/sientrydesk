using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.Core.Tests;

public class AccessEventParserTests
{
    // Aufbau wie im Test beobachtet, alle Werte erfunden.
    internal const string RingJson = """
        {"event":"access.remote_view","receiver_id":"","event_object_id":"00000000-0000-4000-8000-000000000001",
         "save_to_history":false,
         "data":{"channel":"c1","token":"geheimer-ruf-token","device_id":"aaaaaaaaaaaaaaaaaaaaaaaa",
                 "device_type":"UVC G6 Pro Entry","device_name":"Test Entry",
                 "door_id":"11111111-2222-4333-8444-555555555555","door_name":"Tür 1","floor_name":"1F",
                 "request_id":"req-0001","clear_request_id":"","in_or_out":"in","create_time":1694771479,
                 "reason_code":0,"connected_uah_id":"0123456789ab","is_camera":true,"unlocking_not_allowed":false}}
        """;

    [Theory]
    [InlineData("Hello")]
    [InlineData("\"Hello\"\n")]
    public void Hello_is_a_heartbeat(string text)
    {
        Assert.IsType<AccessHeartbeat>(AccessEventParser.Parse(text));
    }

    [Fact]
    public void Ring_is_parsed_without_the_call_token()
    {
        var ring = Assert.IsType<AccessRingStarted>(AccessEventParser.Parse(RingJson));
        Assert.Equal("req-0001", ring.RequestId);
        Assert.Equal("11111111-2222-4333-8444-555555555555", ring.DoorId);
        Assert.Equal("Tür 1", ring.DoorName);
        Assert.Equal("0123456789ab", ring.HubId);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaa", ring.DeviceId);
        Assert.True(ring.IsCamera);
        Assert.False(ring.UnlockingNotAllowed);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1694771479), ring.CreatedAt);
        Assert.DoesNotContain("geheimer-ruf-token", ring.ToString());
    }

    [Fact]
    public void Ring_with_unsafe_door_id_is_rejected()
    {
        var json = RingJson.Replace("11111111-2222-4333-8444-555555555555", "../../users");
        Assert.Null(AccessEventParser.Parse(json));
    }

    [Fact]
    public void Control_characters_in_names_are_removed()
    {
        var json = RingJson.Replace("Tür 1", "T\\u001b[31mür\\u202e 1");
        var ring = Assert.IsType<AccessRingStarted>(AccessEventParser.Parse(json));
        Assert.DoesNotContain(ring.DoorName, c => char.IsControl(c) || c == '‮');
    }

    [Theory]
    [InlineData(105, CallEndReason.Timeout)]
    [InlineData(106, CallEndReason.Declined)]
    [InlineData(107, CallEndReason.Opened)]
    [InlineData(108, CallEndReason.Cancelled)]
    [InlineData(400, CallEndReason.AnsweredElsewhere)]
    [InlineData(0, CallEndReason.Unknown)]
    [InlineData(999, CallEndReason.Unknown)]
    public void Ring_end_reasons(int code, CallEndReason expected)
    {
        var json = $$$"""{"event":"access.remote_view.change","data":{"reason_code":{{{code}}},"remote_call_request_id":"req-0001"}}""";
        var end = Assert.IsType<AccessRingEnded>(AccessEventParser.Parse(json));
        Assert.Equal("req-0001", end.RequestId);
        Assert.Equal(expected, end.Reason);
    }

    [Fact]
    public void Ring_end_without_request_id_has_null_id()
    {
        var end = Assert.IsType<AccessRingEnded>(AccessEventParser.Parse(
            """{"event":"access.remote_view.change","data":{"reason_code":0}}"""));
        Assert.Null(end.RequestId);
    }

    [Fact]
    public void Remote_unlock_carries_door_and_hub()
    {
        var unlocked = Assert.IsType<AccessDoorUnlocked>(AccessEventParser.Parse("""
            {"event":"access.data.device.remote_unlock","event_object_id":"0123456789ab",
             "data":{"unique_id":"11111111-2222-4333-8444-555555555555","name":"Tür 1","location_type":"door"}}
            """));
        Assert.Equal("11111111-2222-4333-8444-555555555555", unlocked.DoorId);
        Assert.Equal("0123456789ab", unlocked.HubId);
    }

    [Fact]
    public void Unlock_log_names_the_person()
    {
        var log = Assert.IsType<AccessUnlockLogged>(AccessEventParser.Parse("""
            {"event":"access.logs.add","data":{"_source":{
              "actor":{"type":"user","display_name":"A. Muster"},
              "event":{"type":"access.door.unlock","result":"ACCESS"},
              "authentication":{"credential_provider":"CALL"},
              "target":[{"type":"UAH-DOOR","id":"0123456789ab"},{"type":"door","id":"0123456789ab","display_name":"Tür 1"}]}}}
            """));
        Assert.Equal("0123456789ab", log.HubId);
        Assert.Equal("A. Muster", log.ActorName);
        Assert.Equal("CALL", log.CredentialProvider);
    }

    [Fact]
    public void Unlock_without_a_person_has_no_name()
    {
        var log = Assert.IsType<AccessUnlockLogged>(AccessEventParser.Parse("""
            {"event":"access.logs.add","data":{"_source":{
              "actor":{"type":"user","display_name":"N/A"},
              "event":{"type":"access.door.unlock","result":"ACCESS"},
              "target":[{"type":"door","id":"0123456789ab","display_name":"Tür 1"}]}}}
            """));
        Assert.Equal(string.Empty, log.ActorName);
    }

    [Fact]
    public void Other_log_entries_are_just_named()
    {
        var other = Assert.IsType<AccessOtherEvent>(AccessEventParser.Parse("""
            {"event":"access.logs.add","data":{"_source":{"event":{"type":"access.door.lock","result":"ACCESS"}}}}
            """));
        Assert.Equal("access.logs.add", other.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"event\":\"access.remote_view\",\"data\":\"x\"}")]
    public void Garbage_is_ignored(string text)
    {
        Assert.Null(AccessEventParser.Parse(text));
    }
}

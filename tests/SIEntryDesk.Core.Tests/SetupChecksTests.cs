using Microsoft.Extensions.Configuration;
using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Setup;

namespace SIEntryDesk.Core.Tests;

public class SetupChecksTests
{
    private static readonly Dictionary<string, string> Cameras = new()
    {
        ["cam-nord-00001"] = "Eingang Nord",
        ["cam-sued-00001"] = "Lieferung Süd",
        ["cam-hof-000001"] = "Hof",
        ["cam-hof-000002"] = "Hof West",
    };

    [Fact]
    public void Door_names_are_matched_to_camera_names()
    {
        var doors = new[]
        {
            new AccessDoor("door-nord", "Eingang Nord HUB", "UDM - 1F - Eingang Nord HUB"),
            new AccessDoor("door-sued", "Lieferung Süd", ""),
            new AccessDoor("door-keller", "Keller", ""),
        };
        var suggestions = SetupChecks.SuggestDoorCameras(doors, Cameras);
        Assert.Equal("cam-nord-00001", suggestions["door-nord"]);
        Assert.Equal("cam-sued-00001", suggestions["door-sued"]);
        Assert.False(suggestions.ContainsKey("door-keller"));
    }

    [Fact]
    public void Ambiguous_names_get_no_suggestion()
    {
        // "Hof Tür" passt exakt auf "Hof" und wird zugeordnet, "Westflügel" auf nichts.
        var suggestions = SetupChecks.SuggestDoorCameras(
            [new AccessDoor("door-hof", "Hof Tür", ""), new AccessDoor("door-w", "Westflügel", "")], Cameras);
        Assert.Equal("cam-hof-000001", suggestions["door-hof"]);
        Assert.False(suggestions.ContainsKey("door-w"));

        // "Hof Westtor" enthält sowohl "hof" als auch "hofwest": zwei Treffer, keine Zuordnung.
        var partial = SetupChecks.SuggestDoorCameras([new AccessDoor("door-hw", "Hof Westtor", "")], Cameras);
        Assert.False(partial.ContainsKey("door-hw"));
    }

    [Fact]
    public void Generated_configuration_is_read_back_by_the_service()
    {
        var options = new EntryDeskOptions
        {
            Host = "unifi.example.local",
            AccessPin = new string('A', 64),
            ProtectPin = new string('B', 64),
            DoorCameras = new() { ["door-nord"] = "cam-nord-00001" },
            LiveViewSeconds = 45,
            TokenExpires = new DateTime(2027, 9, 30),
            ProtectKeyExpires = new DateTime(2027, 9, 29),
        };
        var json = SetupChecks.ToConfigJson(options, new DateTime(2026, 9, 30, 15, 0, 0));
        Assert.StartsWith("// SI EntryDesk", json);

        var path = Path.Combine(Path.GetTempPath(), $"sied-setup-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var read = new EntryDeskOptions();
            new ConfigurationBuilder().AddJsonFile(path).Build().Bind(read);
            Assert.Null(read.Validate());
            Assert.Equal("unifi.example.local", read.Host);
            Assert.Equal("cam-nord-00001", read.DoorCameras["door-nord"]);
            Assert.Equal(45, read.LiveViewSeconds);
            Assert.Equal(new DateTime(2027, 9, 29), read.ProtectKeyExpires);
            Assert.DoesNotContain("StreamPin", json);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

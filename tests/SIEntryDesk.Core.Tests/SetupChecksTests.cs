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

    private static readonly string PinA = string.Join(":", Enumerable.Repeat("AA", 32));
    private static readonly string PinB = string.Join(":", Enumerable.Repeat("BB", 32));

    [Fact]
    public void Existing_configuration_with_comments_is_loaded()
    {
        var options = new EntryDeskOptions
        {
            Host = "unifi.example.local",
            AccessPin = PinA,
            ProtectPin = PinB,
            Doors = ["Eingang Nord HUB"],
            DoorCameras = new() { ["door-nord"] = "cam-nord-00001" },
            LiveView = false,
            LiveViewSeconds = 90,
            TokenExpires = new DateTime(2027, 9, 30),
        };
        var loaded = SetupChecks.ParseConfigJson(SetupChecks.ToConfigJson(options, new DateTime(2026, 10, 1)) + "// Schluss\n");
        Assert.Equal("unifi.example.local", loaded.Host);
        Assert.Equal(PinB, loaded.EffectiveStreamPin);
        Assert.Equal(["Eingang Nord HUB"], loaded.Doors);
        Assert.Equal("cam-nord-00001", loaded.DoorCameras["door-nord"]);
        Assert.False(loaded.LiveView);
        Assert.Equal(90, loaded.LiveViewSeconds);
        Assert.Equal(new DateTime(2027, 9, 30), loaded.TokenExpires);
        Assert.Null(loaded.ProtectKeyExpires);
    }

    [Fact]
    public void Handwritten_configuration_is_loaded_like_the_service_does()
    {
        var loaded = SetupChecks.ParseConfigJson("""
            {
              // von Hand
              "host": "10.0.0.1",
              "AccessPin": "aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa aa",
              "Doors": null,
              "LiveViewSeconds": "45",
              "Unbekannt": 1,
            }
            """);
        Assert.Equal("10.0.0.1", loaded.Host);
        Assert.Null(loaded.Validate());
        Assert.Empty(loaded.Doors);
        Assert.Empty(loaded.DoorCameras);
        Assert.Equal(45, loaded.LiveViewSeconds);
        Assert.False(loaded.VideoConfigured);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{ \"Host\": ")]
    public void Unreadable_configuration_is_rejected(string json) =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SetupChecks.ParseConfigJson(json));

    private static PortCheck Port(int port, string? fingerprint) =>
        new("Test", port, fingerprint is not null, fingerprint, fingerprint is null ? "keine Antwort" : null);

    [Fact]
    public void Pins_are_compared_regardless_of_notation()
    {
        Assert.Equal(PinComparison.Match, SetupChecks.ComparePin(Port(443, PinA), PinA.Replace(":", "").ToLowerInvariant()));
        Assert.Equal(PinComparison.Mismatch, SetupChecks.ComparePin(Port(443, PinA), PinB));
        Assert.Equal(PinComparison.Mismatch, SetupChecks.ComparePin(Port(443, PinA), "kein Pin"));
        Assert.Equal(PinComparison.NotConfigured, SetupChecks.ComparePin(Port(443, PinA), ""));
        Assert.Equal(PinComparison.Unreachable, SetupChecks.ComparePin(Port(443, null), PinA));
    }

    [Fact]
    public void Unchanged_pins_need_no_new_confirmation()
    {
        var options = new EntryDeskOptions { Host = "h", AccessPin = PinA, ProtectPin = PinB };
        Assert.True(SetupChecks.PinsUnchanged(options, [Port(12445, PinA), Port(443, PinB), Port(7441, PinB)]));
        // Nicht erreichbare Ports ändern nichts, solange Access passt.
        Assert.True(SetupChecks.PinsUnchanged(options, [Port(12445, PinA), Port(443, null), Port(7441, null)]));
        // Access nicht erreichbar oder anders: neu bestätigen.
        Assert.False(SetupChecks.PinsUnchanged(options, [Port(12445, null), Port(443, PinB), Port(7441, PinB)]));
        Assert.False(SetupChecks.PinsUnchanged(options, [Port(12445, PinB), Port(443, PinB), Port(7441, PinB)]));
        // Neues Zertifikat auf 7441.
        Assert.False(SetupChecks.PinsUnchanged(options, [Port(12445, PinA), Port(443, PinB), Port(7441, PinA)]));
        // Bisher ohne Livebild: Pins für 443 und 7441 wären neu.
        var noVideo = new EntryDeskOptions { Host = "h", AccessPin = PinA };
        Assert.False(SetupChecks.PinsUnchanged(noVideo, [Port(12445, PinA), Port(443, PinB), Port(7441, PinB)]));
    }

    [Fact]
    public void Door_configuration_is_compared_with_the_installation()
    {
        AccessDoor[] doors = [new("door-nord", "Eingang Nord HUB", ""), new("door-sued", "Lieferung Süd", "")];
        var options = new EntryDeskOptions
        {
            Doors = ["eingang nord hub", "door-sued", "Alte Tür"],
            DoorCameras = new() { ["door-nord"] = "cam-nord-00001", ["door-weg"] = "cam-hof-000001" },
        };

        var withoutCameras = SetupChecks.CompareDoors(options, doors, null);
        Assert.Contains(withoutCameras, f => f.Level == FindingLevel.Warn && f.Text.Contains("„Alte Tür“"));
        Assert.Contains(withoutCameras, f => f.Level == FindingLevel.Warn && f.Text.Contains("door-weg"));
        Assert.Equal(2, withoutCameras.Count);

        var findings = SetupChecks.CompareDoors(options, doors, Cameras);
        Assert.Contains(findings, f => f.Level == FindingLevel.Warn && f.Text.StartsWith("Lieferung Süd: keine Kamera") && f.Text.Contains("Vorschlag: Lieferung Süd"));
        Assert.Equal(3, findings.Count);

        options.DoorCameras["door-nord"] = "cam-gibt-es-nicht";
        Assert.Contains(SetupChecks.CompareDoors(options, doors, Cameras), f => f.Level == FindingLevel.Fail && f.Text.StartsWith("Eingang Nord HUB"));
    }

    [Fact]
    public void Matching_door_configuration_is_reported_as_ok()
    {
        AccessDoor[] doors = [new("door-nord", "Eingang Nord HUB", ""), new("door-sued", "Lieferung Süd", "")];
        var options = new EntryDeskOptions
        {
            DoorCameras = new() { ["door-nord"] = "cam-nord-00001", ["door-sued"] = "cam-sued-00001" },
        };
        var finding = Assert.Single(SetupChecks.CompareDoors(options, doors, Cameras));
        Assert.Equal(FindingLevel.Ok, finding.Level);

        // Eine Tür, die dieser PC nicht anzeigt, braucht keine Kamera.
        var onlyNorth = new EntryDeskOptions { Doors = ["door-nord"], DoorCameras = new() { ["door-nord"] = "cam-nord-00001" } };
        Assert.Equal(FindingLevel.Ok, Assert.Single(SetupChecks.CompareDoors(onlyNorth, doors, Cameras)).Level);
    }
}

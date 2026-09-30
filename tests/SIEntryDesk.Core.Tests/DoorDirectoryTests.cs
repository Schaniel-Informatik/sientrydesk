using SIEntryDesk.Core.Calls;

namespace SIEntryDesk.Core.Tests;

public sealed class DoorDirectoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sied-doors-{Guid.NewGuid():N}.json");

    private static CallInfo Call(string doorId = "11111111-2222-4333-8444-555555555555", string camera = "cam0000000001", string name = "Tür 1") =>
        new("req-1", doorId, name, "0123456789ab", camera, DateTimeOffset.UnixEpoch, UnlockAllowed: true);

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Learns_from_calls_and_survives_a_restart()
    {
        var doors = new DoorDirectory(_path);
        Assert.True(doors.Learn(Call()));
        Assert.False(doors.Learn(Call()));
        Assert.True(doors.Learn(Call(camera: "cam0000000002")));

        var reloaded = new DoorDirectory(_path);
        var door = Assert.Single(reloaded.All());
        Assert.Equal("cam0000000002", door.CameraId);
        Assert.Equal("Tür 1", door.DoorName);
    }

    [Fact]
    public void Configured_cameras_win_over_learned_ones()
    {
        var doors = new DoorDirectory(null);
        doors.Learn(Call(camera: "cam-gelernt-01"));
        doors.Seed("11111111-2222-4333-8444-555555555555", "cam-konfig-001");
        var door = Assert.Single(doors.All());
        Assert.Equal("cam-konfig-001", door.CameraId);
        Assert.Equal("Tür 1", door.DoorName);

        doors.Seed("../boese", "cam-konfig-002");
        Assert.Single(doors.All());
    }

    [Fact]
    public void Calls_without_camera_teach_nothing()
    {
        var doors = new DoorDirectory(null);
        Assert.False(doors.Learn(Call(camera: "")));
        Assert.Empty(doors.All());
    }

    [Fact]
    public void Tampered_entries_in_the_file_are_ignored()
    {
        File.WriteAllText(_path, """
            [{"DoorId":"../x","DoorName":"Böse","CameraId":"cam1"},
             {"DoorId":"door-ok","DoorName":"Gut\u001b[31m","CameraId":"cam-ok-000001"}]
            """);
        var door = Assert.Single(new DoorDirectory(_path).All());
        Assert.Equal("door-ok", door.DoorId);
        Assert.DoesNotContain(door.DoorName, char.IsControl);
    }

    [Fact]
    public void Broken_file_means_empty_directory()
    {
        File.WriteAllText(_path, "kein json");
        Assert.Empty(new DoorDirectory(_path).All());
    }
}

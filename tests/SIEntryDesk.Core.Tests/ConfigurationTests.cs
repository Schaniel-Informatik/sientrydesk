using Microsoft.Extensions.Configuration;

namespace SIEntryDesk.Core.Tests;

public class ConfigurationTests
{
    private static string ExampleFile([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "deploy", "programm", "sientrydesk.example.json"));

    [Fact]
    public void Example_configuration_binds_as_the_service_reads_it()
    {
        var options = new EntryDeskOptions();
        new ConfigurationBuilder().AddJsonFile(ExampleFile(), optional: false).Build().Bind(options);

        Assert.Null(options.Validate());
        Assert.Null(options.ValidateVideo());
        Assert.Equal("unifi.example.local", options.Host);
        Assert.Empty(options.Doors);
        Assert.Equal("000000000000000000000001", Assert.Single(options.DoorCameras).Value);
        Assert.True(options.LiveView);
        Assert.Equal(TimeSpan.FromSeconds(60), options.LiveViewLifetime);
        Assert.Equal(new DateTime(2027, 9, 30), options.TokenExpires);
    }

    [Fact]
    public void Local_file_overrides_single_values()
    {
        var local = Path.Combine(Path.GetTempPath(), $"sied-local-{Guid.NewGuid():N}.json");
        File.WriteAllText(local, "{ \"LiveView\": false }");
        try
        {
            var options = new EntryDeskOptions();
            new ConfigurationBuilder().AddJsonFile(ExampleFile()).AddJsonFile(local).Build().Bind(options);
            Assert.False(options.LiveView);
            Assert.Equal("unifi.example.local", options.Host);
        }
        finally
        {
            File.Delete(local);
        }
    }
}

namespace SIEntryDesk.Service;

/// <summary>
/// Storage under %ProgramData%\SIEntryDesk. The install script sets the permissions: only SYSTEM, administrators and
/// the service account, standard users have no access.
/// </summary>
internal static class ServicePaths
{
    public const string ServiceName = "SIEntryDesk";

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIEntryDesk");

    /// <summary>Installation configuration, the same for all PCs of an installation (from the package).</summary>
    public static string ConfigFile => Path.Combine(DataDirectory, "sientrydesk.json");

    /// <summary>Optional deviations for this PC only, override values from <see cref="ConfigFile"/>.</summary>
    public static string LocalConfigFile => Path.Combine(DataDirectory, "sientrydesk.local.json");

    /// <summary>Configuration up to 0.2.x, still read so that an update without a new file keeps working.</summary>
    public static string LegacyConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Writable by the service, e.g. for the learned door → camera mapping.</summary>
    public static string StateDirectory => Path.Combine(DataDirectory, "state");
    public static string DoorsFile => Path.Combine(StateDirectory, "doors.json");
}

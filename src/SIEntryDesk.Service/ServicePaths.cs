namespace SIEntryDesk.Service;

/// <summary>
/// Ablage unter %ProgramData%\SIEntryDesk. Die Rechte setzt das Installationsskript:
/// nur SYSTEM, Administratoren und das Dienstkonto, Standardbenutzer haben keinen Zugriff.
/// </summary>
internal static class ServicePaths
{
    public const string ServiceName = "SIEntryDesk";

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SIEntryDesk");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Vom Dienst beschreibbar, z. B. für die gelernte Zuordnung Tür → Kamera.</summary>
    public static string StateDirectory => Path.Combine(DataDirectory, "state");
    public static string DoorsFile => Path.Combine(StateDirectory, "doors.json");
}

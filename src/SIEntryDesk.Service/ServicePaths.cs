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

    /// <summary>Anlagen-Konfiguration, für alle PCs einer Anlage gleich (aus dem Paket).</summary>
    public static string ConfigFile => Path.Combine(DataDirectory, "sientrydesk.json");

    /// <summary>Optionale Abweichungen nur für diesen PC, überschreiben Werte aus <see cref="ConfigFile"/>.</summary>
    public static string LocalConfigFile => Path.Combine(DataDirectory, "sientrydesk.local.json");

    /// <summary>Konfiguration bis 0.2.x, wird noch gelesen, damit ein Update ohne neue Datei weiterläuft.</summary>
    public static string LegacyConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string SecretsFile => Path.Combine(DataDirectory, "secrets.dat");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Vom Dienst beschreibbar, z. B. für die gelernte Zuordnung Tür → Kamera.</summary>
    public static string StateDirectory => Path.Combine(DataDirectory, "state");
    public static string DoorsFile => Path.Combine(StateDirectory, "doors.json");
}

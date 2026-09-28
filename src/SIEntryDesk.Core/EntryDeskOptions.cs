using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Security;

namespace SIEntryDesk.Core;

/// <summary>Nicht geheime Einstellungen. Tokens gehören nicht hierher.</summary>
public sealed class EntryDeskOptions
{
    /// <summary>Adresse der UniFi-Konsole (IP oder Name).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SHA-256-Fingerprint des Zertifikats der Access-API auf Port 12445.</summary>
    public string AccessPin { get; set; } = string.Empty;

    /// <summary>Türen, die dieser PC anzeigt und öffnen darf, als Tür-ID oder Türname. Leer = alle.</summary>
    public List<string> Doors { get; set; } = [];

    /// <summary>Beschreibung des ersten Problems, oder null wenn die Einstellungen brauchbar sind.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host.Trim()) == UriHostNameType.Unknown)
            return "Host fehlt oder ist ungültig";
        try
        {
            CertificatePin.Parse(AccessPin);
        }
        catch (FormatException)
        {
            return "AccessPin fehlt oder ist kein SHA-256-Fingerprint";
        }
        return null;
    }

    public bool AcceptsDoor(AccessRingStarted ring) =>
        Doors.Count == 0 ||
        Doors.Any(d => string.Equals(d.Trim(), ring.DoorId, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(d.Trim(), ring.DoorName, StringComparison.OrdinalIgnoreCase));
}

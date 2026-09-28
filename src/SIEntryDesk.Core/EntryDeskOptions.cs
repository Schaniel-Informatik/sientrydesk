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

    /// <summary>SHA-256-Fingerprint des UniFi-OS-Zertifikats auf Port 443 (Protect-API). Leer = kein Livebild.</summary>
    public string ProtectPin { get; set; } = string.Empty;

    /// <summary>Fingerprint auf Port 7441 (Videostream). Leer = derselbe wie ProtectPin.</summary>
    public string StreamPin { get; set; } = string.Empty;

    /// <summary>Bevorzugte Stream-Qualitäten in dieser Reihenfolge. Das Fenster ist klein, deshalb zuerst niedrig.</summary>
    public List<string> StreamQualities { get; set; } = ["low", "medium", "high"];

    /// <summary>Livebild ohne Klingeln über das Tray-Menü. Pro PC abschaltbar, jeder Abruf wird protokolliert.</summary>
    public bool LiveView { get; set; } = true;

    /// <summary>So lange bleibt das Livebild ohne Klingeln offen, begrenzt auf 15–600 s.</summary>
    public int LiveViewSeconds { get; set; } = 90;

    public TimeSpan LiveViewLifetime => TimeSpan.FromSeconds(Math.Clamp(LiveViewSeconds, 15, 600));

    public bool VideoConfigured => !string.IsNullOrWhiteSpace(ProtectPin);

    /// <summary>Problem der Video-Einstellungen, oder null. Nur relevant, wenn ProtectPin gesetzt ist.</summary>
    public string? ValidateVideo()
    {
        try
        {
            CertificatePin.Parse(ProtectPin);
            if (!string.IsNullOrWhiteSpace(StreamPin))
                CertificatePin.Parse(StreamPin);
        }
        catch (FormatException)
        {
            return "ProtectPin oder StreamPin ist kein SHA-256-Fingerprint";
        }
        return null;
    }

    public string EffectiveStreamPin => string.IsNullOrWhiteSpace(StreamPin) ? ProtectPin : StreamPin;

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

    public bool AcceptsDoor(AccessRingStarted ring) => AcceptsDoor(ring.DoorId, ring.DoorName);

    public bool AcceptsDoor(string doorId, string doorName) =>
        Doors.Count == 0 ||
        Doors.Any(d => string.Equals(d.Trim(), doorId, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(d.Trim(), doorName, StringComparison.OrdinalIgnoreCase));
}

using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Security;

namespace SIEntryDesk.Core;

/// <summary>Non-secret settings. Tokens do not belong here.</summary>
public sealed class EntryDeskOptions
{
    /// <summary>Address of the UniFi console (IP or name).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SHA-256 fingerprint of the certificate of the Access API on port 12445.</summary>
    public string AccessPin { get; set; } = string.Empty;

    /// <summary>Doors this PC shows and may unlock, as door ID or door name. Empty = all.</summary>
    public List<string> Doors { get; set; } = [];

    /// <summary>SHA-256 fingerprint of the UniFi OS certificate on port 443 (Protect API). Empty = no live
    /// view.</summary>
    public string ProtectPin { get; set; } = string.Empty;

    /// <summary>Fingerprint on port 7441 (video stream). Empty = same as ProtectPin.</summary>
    public string StreamPin { get; set; } = string.Empty;

    /// <summary>Preferred stream qualities in this order. The window is small, so low first.</summary>
    public List<string> StreamQualities { get; set; } = ["low", "medium", "high"];

    /// <summary>Live view without ringing via the tray menu. Can be switched off per PC, every request is
    /// logged.</summary>
    public bool LiveView { get; set; } = true;

    /// <summary>Talkback during a call. Can be switched off per PC (decision 2026-10-02), needs the Protect
    /// key.</summary>
    public bool Talkback { get; set; } = true;

    /// <summary>How long the live view without ringing stays open, limited to 15–600 s.</summary>
    public int LiveViewSeconds { get; set; } = 60;

    public TimeSpan LiveViewLifetime => TimeSpan.FromSeconds(Math.Clamp(LiveViewSeconds, 15, 600));

    /// <summary>Door ID → Protect camera ID. From the configuration, so that the live view is available without a first
    /// ring. Mappings learned from ring events complement it.</summary>
    public Dictionary<string, string> DoorCameras { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Expiry date of the Access token, for the warning beforehand. Empty = unknown.</summary>
    public DateTime? TokenExpires { get; set; }

    /// <summary>Expiry date of the Protect key, for the warning beforehand. Empty = unknown.</summary>
    public DateTime? ProtectKeyExpires { get; set; }

    /// <summary>Warn this many days before expiry (decision 2026-09-30).</summary>
    public const int ExpiryWarningDays = 14;

    /// <summary>Notice that the keys expire soon or have expired, or null. The earlier of the two expiry dates counts,
    /// because both are renewed together.</summary>
    public string? ExpiryWarning(DateTime today)
    {
        var dates = new[] { TokenExpires, ProtectKeyExpires }.Where(d => d.HasValue).Select(d => d!.Value.Date).ToList();
        if (dates.Count == 0)
            return null;
        var next = dates.Min();
        if (next < today.Date)
            return $"Die Tokens von SI EntryDesk sind am {next:dd.MM.yyyy} abgelaufen";
        return (next - today.Date).TotalDays <= ExpiryWarningDays
            ? $"Die Tokens von SI EntryDesk müssen bis am {next:dd.MM.yyyy} erneuert werden"
            : null;
    }

    public bool VideoConfigured => !string.IsNullOrWhiteSpace(ProtectPin);

    /// <summary>Problem with the video settings, or null. Only relevant if ProtectPin is set.</summary>
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

    /// <summary>Description of the first problem, or null if the settings are usable.</summary>
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

using SIEntryDesk.Core.Access;
using SIEntryDesk.Core.Security;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Core;

/// <summary>Non-secret settings. Tokens do not belong here.</summary>
public sealed class EntryDeskOptions
{
    /// <summary>Language for the users: "de" (default), "en" or "pl". Check and log are German for "de", otherwise
    /// English (from 0.10.0).</summary>
    public string Language { get; set; } = "de";

    /// <summary>The language to use. Unknown values count as German, <see cref="Validate"/> reports them.</summary>
    public UiLanguage UiLanguage => Tr.TryParse(Language, out var language) ? language : UiLanguage.De;

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
        var date = Tr.Date(next);
        if (next < today.Date)
            return Tr.User($"Die Tokens von SI EntryDesk sind am {date} abgelaufen", $"The SI EntryDesk tokens expired on {date}",
                $"Tokeny SI EntryDesk wygasły {date}");
        return (next - today.Date).TotalDays <= ExpiryWarningDays
            ? Tr.User($"Die Tokens von SI EntryDesk müssen bis am {date} erneuert werden",
                $"The SI EntryDesk tokens must be renewed by {date}", $"Tokeny SI EntryDesk trzeba odnowić do {date}")
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
            return Tr.Admin("ProtectPin oder StreamPin ist kein SHA-256-Fingerprint", "ProtectPin or StreamPin is not a SHA-256 fingerprint");
        }
        return null;
    }

    public string EffectiveStreamPin => string.IsNullOrWhiteSpace(StreamPin) ? ProtectPin : StreamPin;

    /// <summary>Description of the first problem, or null if the settings are usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host.Trim()) == UriHostNameType.Unknown)
            return Tr.Admin("Host fehlt oder ist ungültig", "Host is missing or invalid");
        try
        {
            CertificatePin.Parse(AccessPin);
        }
        catch (FormatException)
        {
            return Tr.Admin("AccessPin fehlt oder ist kein SHA-256-Fingerprint", "AccessPin is missing or not a SHA-256 fingerprint");
        }
        return null;
    }

    /// <summary>Unknown language, or null. Not part of <see cref="Validate"/>: with a typo the PC should still ring, in
    /// German.</summary>
    public string? ValidateLanguage() => Tr.TryParse(Language, out _)
        ? null
        : Tr.Admin($"Language „{UntrustedText.Clean(Language, 20)}“ ist unbekannt, möglich sind de, en und pl. Es gilt Deutsch",
            $"Language \"{UntrustedText.Clean(Language, 20)}\" is unknown, possible are de, en and pl. German applies");

    public bool AcceptsDoor(AccessRingStarted ring) => AcceptsDoor(ring.DoorId, ring.DoorName);

    public bool AcceptsDoor(string doorId, string doorName) =>
        Doors.Count == 0 ||
        Doors.Any(d => string.Equals(d.Trim(), doorId, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(d.Trim(), doorName, StringComparison.OrdinalIgnoreCase));
}

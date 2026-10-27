using System.Text;

namespace SIEntryDesk.Core;

/// <summary>Texts from events and API responses are untrusted: remove control characters, limit the length.</summary>
public static class UntrustedText
{
    public const int DefaultMaxLength = 120;

    public static string Clean(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (sb.Length >= maxLength)
                break;
            // Control characters, including Unicode format characters such as direction changes, become spaces.
            sb.Append(char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>IDs from events end up in URL paths. Only letters, digits and hyphen are allowed.</summary>
    public static bool IsSafeId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

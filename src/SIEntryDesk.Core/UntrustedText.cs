using System.Text;

namespace SIEntryDesk.Core;

/// <summary>Texte aus Ereignissen und API-Antworten sind nicht vertrauenswürdig: Steuerzeichen entfernen, Länge begrenzen.</summary>
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
            // Steuerzeichen, auch Unicode-Formatzeichen wie Richtungswechsel, werden zu Leerzeichen.
            sb.Append(char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>IDs aus Ereignissen landen in URL-Pfaden. Erlaubt sind nur Buchstaben, Ziffern und Bindestrich.</summary>
    public static bool IsSafeId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

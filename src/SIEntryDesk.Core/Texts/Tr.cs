using System.Globalization;

namespace SIEntryDesk.Core.Texts;

/// <summary>Language of an installation ("Language" in sientrydesk.json).</summary>
public enum UiLanguage
{
    De,
    En,
    Pl,
}

/// <summary>
/// Texts in the language of the installation (decision 2026-11-11). Every text stays at the place where it is used,
/// with all its translations side by side, always in the order German, English, Polish:
/// <list type="bullet">
/// <item><see cref="User"/> for what users see in normal use: tray menu, ring window, notifications. German, English
/// or Polish.</item>
/// <item><see cref="Admin"/> for check, log, setup wizard and command line. German for German installations,
/// otherwise English.</item>
/// </list>
/// tools/texts.py collects all calls into a table for proofreading. Arguments must therefore be string literals,
/// interpolated or joined with +, never variables.
/// </summary>
public static class Tr
{
    private static UiLanguage _language = UiLanguage.De;
    private static readonly AsyncLocal<UiLanguage?> Override = new();

    /// <summary>Language for users. Without a setting German, as before 0.10.0.</summary>
    public static UiLanguage Language => Override.Value ?? _language;

    /// <summary>Language for admins: German or English.</summary>
    public static UiLanguage AdminLanguage => Language == UiLanguage.De ? UiLanguage.De : UiLanguage.En;

    public static void Use(UiLanguage language) => _language = language;

    /// <summary>Only for this async flow, for tests that run in parallel.</summary>
    public static IDisposable Scope(UiLanguage language)
    {
        var previous = Override.Value;
        Override.Value = language;
        return new Restore(() => Override.Value = previous);
    }

    public static string User(string de, string en, string pl) => Language switch
    {
        UiLanguage.En => en,
        UiLanguage.Pl => pl,
        _ => de,
    };

    public static string Admin(string de, string en) => AdminLanguage == UiLanguage.De ? de : en;

    /// <summary>"de", "en", "pl", case-insensitive. Empty means German. Unknown values yield false.</summary>
    public static bool TryParse(string? value, out UiLanguage language)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "de":
                language = UiLanguage.De;
                return true;
            case "en":
                language = UiLanguage.En;
                return true;
            case "pl":
                language = UiLanguage.Pl;
                return true;
            default:
                language = UiLanguage.De;
                return false;
        }
    }

    public static string Code(UiLanguage language) => language switch
    {
        UiLanguage.En => "en",
        UiLanguage.Pl => "pl",
        _ => "de",
    };

    /// <summary>Language of Windows for the setup wizard and the command line, before a configuration is known: German
    /// for German, Polish for Polish, otherwise English.</summary>
    public static UiLanguage FromCulture(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "de" => UiLanguage.De,
        "pl" => UiLanguage.Pl,
        _ => UiLanguage.En,
    };

    /// <summary>Date for users and admins: 05.11.2026 in German and Polish, 2026-11-05 in English.</summary>
    public static string Date(DateTime date) =>
        date.ToString(Language == UiLanguage.En ? "yyyy-MM-dd" : "dd.MM.yyyy", CultureInfo.InvariantCulture);

    public static string AdminDate(DateTime date) =>
        date.ToString(AdminLanguage == UiLanguage.En ? "yyyy-MM-dd" : "dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>Polish plural: 1 → one, 2–4 (not 12–14) → few, otherwise many. "1 minuta, 2 minuty, 5 minut".</summary>
    public static string PlPlural(long n, string one, string few, string many)
    {
        if (n == 1)
            return one;
        var lastTwo = n % 100;
        var last = n % 10;
        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

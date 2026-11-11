using System.Globalization;
using Microsoft.Win32;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.App;

/// <summary>
/// Language of the app. The app cannot read sientrydesk.json (only admins and the service can), so the service reports
/// the language of the installation with its status. The app keeps the last one per user, so that it starts in the right
/// language before it is connected. Without a stored one the language of Windows applies until then.
/// </summary>
internal static class AppLanguage
{
    private const string SettingsKey = @"Software\SIEntryDesk";
    private const string ValueName = "Language";

    public static void Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        var stored = key?.GetValue(ValueName) as string;
        Tr.Use(stored is not null && Tr.TryParse(stored, out var language) ? language : Tr.FromCulture(CultureInfo.CurrentUICulture));
    }

    /// <summary>Language from the status of the service. True if it changed.</summary>
    public static bool Apply(string code)
    {
        if (!Tr.TryParse(code, out var language) || language == Tr.Language)
            return false;
        Tr.Use(language);
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(ValueName, Tr.Code(language));
        return true;
    }
}

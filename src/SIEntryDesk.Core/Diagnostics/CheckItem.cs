using System.Globalization;
using System.Text;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Core.Diagnostics;

public enum CheckLevel
{
    Ok,
    Info,
    Warn,
    Fail,
}

/// <summary>One item of the check of SIEntryDesk.exe --check. Detail says what is going on and what to do. Never
/// tokens, keys or stream addresses, the result is copied and passed on. Area is <see cref="CheckReport.ThisPc"/> or
/// <see cref="CheckReport.Installation"/>, Name and Detail are in the admin language.</summary>
public sealed record CheckItem(string Area, string Name, CheckLevel Level, string Detail);

public static class CheckReport
{
    /// <summary>Identifiers, not texts: the service sends them to the app. Before 0.10.0 they were German texts.</summary>
    public const string ThisPc = "pc";
    public const string Installation = "installation";

    public static string AreaName(string area) => area switch
    {
        ThisPc or "Dieser PC" => Tr.Admin("Dieser PC", "This PC"),
        Installation or "Anlage" => Tr.Admin("Anlage", "Installation"),
        _ => area,
    };

    public static string Mark(CheckLevel level) => level switch
    {
        CheckLevel.Ok => "✓",
        CheckLevel.Warn => "⚠",
        CheckLevel.Fail => "✗",
        _ => "·",
    };

    /// <summary>Overall result: red as soon as one item is red, otherwise orange if there is a warning.</summary>
    public static CheckLevel Overall(IEnumerable<CheckItem> items) =>
        items.Select(i => i.Level).DefaultIfEmpty(CheckLevel.Ok).Max() switch
        {
            CheckLevel.Info => CheckLevel.Ok,
            var level => level,
        };

    /// <summary>Text to copy into a ticket or an email.</summary>
    public static string ToText(IEnumerable<CheckItem> items, string header, DateTimeOffset at)
    {
        var list = items.ToList();
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{header}, {Tr.AdminDate(at.ToLocalTime().DateTime)} {at.ToLocalTime():HH:mm}");
        var result = Overall(list) switch
        {
            CheckLevel.Fail => Tr.Admin("Fehler", "errors"),
            CheckLevel.Warn => Tr.Admin("Hinweise", "notes"),
            _ => Tr.Admin("alles in Ordnung", "all fine"),
        };
        text.AppendLine(CultureInfo.InvariantCulture, $"{Tr.Admin("Ergebnis", "Result")}: {result}");
        foreach (var area in list.Select(i => i.Area).Distinct())
        {
            text.AppendLine();
            text.AppendLine(AreaName(area));
            foreach (var item in list.Where(i => i.Area == area))
                text.AppendLine(CultureInfo.InvariantCulture, $"  {Mark(item.Level)} {item.Name}: {item.Detail}");
        }
        return text.ToString();
    }
}

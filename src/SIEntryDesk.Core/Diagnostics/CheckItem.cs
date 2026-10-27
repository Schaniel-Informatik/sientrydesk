using System.Globalization;
using System.Text;

namespace SIEntryDesk.Core.Diagnostics;

public enum CheckLevel
{
    Ok,
    Info,
    Warn,
    Fail,
}

/// <summary>One item of the check of SIEntryDesk.exe --check. Detail says what is going on and what to do. Never
/// tokens, keys or stream addresses, the result is copied and passed on.</summary>
public sealed record CheckItem(string Area, string Name, CheckLevel Level, string Detail);

public static class CheckReport
{
    public const string ThisPc = "Dieser PC";
    public const string Installation = "Anlage";

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
        text.AppendLine(CultureInfo.InvariantCulture, $"{header}, {at.ToLocalTime():dd.MM.yyyy HH:mm}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Ergebnis: {Overall(list) switch { CheckLevel.Fail => "Fehler", CheckLevel.Warn => "Hinweise", _ => "alles in Ordnung" }}");
        foreach (var area in list.Select(i => i.Area).Distinct())
        {
            text.AppendLine();
            text.AppendLine(area);
            foreach (var item in list.Where(i => i.Area == area))
                text.AppendLine(CultureInfo.InvariantCulture, $"  {Mark(item.Level)} {item.Name}: {item.Detail}");
        }
        return text.ToString();
    }
}

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

/// <summary>Ein Punkt der Prüfung von SIEntryDesk.exe --check. Detail sagt, was los ist und was zu tun ist.
/// Nie Tokens, Schlüssel oder Stream-Adressen, das Ergebnis wird kopiert und weitergegeben.</summary>
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

    /// <summary>Gesamturteil: rot, sobald ein Punkt rot ist, sonst orange bei einer Warnung.</summary>
    public static CheckLevel Overall(IEnumerable<CheckItem> items) =>
        items.Select(i => i.Level).DefaultIfEmpty(CheckLevel.Ok).Max() switch
        {
            CheckLevel.Info => CheckLevel.Ok,
            var level => level,
        };

    /// <summary>Text zum Kopieren in ein Ticket oder eine Mail.</summary>
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

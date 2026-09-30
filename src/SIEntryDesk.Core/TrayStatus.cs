using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.Core;

public enum TrayColor
{
    Green,
    Orange,
    Red,
    Grey,
    Blue,
}

/// <param name="Alarm">Dieser PC klingelt nicht, obwohl er es sollte. Die App meldet das nach einer Wartezeit.</param>
public sealed record TrayState(TrayColor Color, string Text, bool Alarm);

/// <summary>
/// Welche Farbe und welcher Text das Tray-Symbol zeigt. Reihenfolge: Rot (klingelt nicht) vor Blau (pausiert)
/// vor Grau (nicht im Firmennetz, kein Alarm) vor Orange (Nebensache gestört) vor Grün.
/// </summary>
public static class TrayStatus
{
    public static TrayState Evaluate(bool serviceConnected, StatusMessage? status, DateTimeOffset? pausedUntil, DateTimeOffset now)
    {
        if (!serviceConnected)
            return new TrayState(TrayColor.Red, "Dienst nicht erreichbar, dieser PC klingelt nicht", Alarm: true);
        if (status is { Health: LinkHealth.Error })
            return new TrayState(TrayColor.Red, Explain(status.Problem, "Störung, dieser PC klingelt nicht"), Alarm: true);
        if (pausedUntil is { } until && until > now)
            return new TrayState(TrayColor.Blue, $"Klingel pausiert bis {until.ToLocalTime():HH:mm}", Alarm: false);
        return status switch
        {
            null or { Health: LinkHealth.Starting } => new TrayState(TrayColor.Grey, "Verbinde …", false),
            { Health: LinkHealth.Unreachable } => new TrayState(TrayColor.Grey,
                Explain(status.Problem, "Konsole nicht erreichbar (nicht im Firmennetz?)"), false),
            { Warning.Length: > 0 } => new TrayState(TrayColor.Orange, $"Bereit, aber: {status.Warning}", false),
            _ => new TrayState(TrayColor.Green, "Bereit", false),
        };
    }

    private static string Explain(string problem, string fallback) => string.IsNullOrWhiteSpace(problem) ? fallback : problem;
}

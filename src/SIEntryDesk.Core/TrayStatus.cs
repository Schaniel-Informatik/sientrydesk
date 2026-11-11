using SIEntryDesk.Core.Ipc;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Core;

public enum TrayColor
{
    Green,
    Orange,
    Red,
    Grey,
    Blue,
}

/// <param name="Alarm">This PC does not ring although it should. The app reports this after a waiting time.</param>
public sealed record TrayState(TrayColor Color, string Text, bool Alarm);

/// <summary>
/// Which color and text the tray icon shows. Order: red (does not ring) before blue (paused) before grey (not in the
/// company network, no alarm) before orange (minor issue) before green.
/// </summary>
public static class TrayStatus
{
    public static TrayState Evaluate(bool serviceConnected, StatusMessage? status, DateTimeOffset? pausedUntil, DateTimeOffset now)
    {
        if (!serviceConnected)
            return new TrayState(TrayColor.Red, Tr.User("Dienst nicht erreichbar, dieser PC klingelt nicht",
                "Service not reachable, this PC does not ring", "Usługa nieosiągalna, ten komputer nie dzwoni"), Alarm: true);
        if (status is { Health: LinkHealth.Error })
            return new TrayState(TrayColor.Red, Explain(status.Problem, Tr.User("Störung, dieser PC klingelt nicht",
                "Fault, this PC does not ring", "Usterka, ten komputer nie dzwoni")), Alarm: true);
        if (pausedUntil is { } until && until > now)
            return new TrayState(TrayColor.Blue, Tr.User($"Klingel pausiert bis {until.ToLocalTime():HH:mm}",
                $"Doorbell paused until {until.ToLocalTime():HH:mm}", $"Dzwonek wstrzymany do {until.ToLocalTime():HH:mm}"), Alarm: false);
        return status switch
        {
            null or { Health: LinkHealth.Starting } => new TrayState(TrayColor.Grey, Connecting, false),
            { Health: LinkHealth.Unreachable } => new TrayState(TrayColor.Grey,
                Explain(status.Problem, Tr.User("Konsole nicht erreichbar (nicht im Firmennetz?)",
                    "Console not reachable (outside the company network?)", "Konsola nieosiągalna (poza siecią firmową?)")), false),
            { Warning.Length: > 0 } => new TrayState(TrayColor.Orange, Tr.User($"Bereit, aber: {status.Warning}",
                $"Ready, but: {status.Warning}", $"Gotowe, ale: {status.Warning}"), false),
            _ => new TrayState(TrayColor.Green, Tr.User("Bereit", "Ready", "Gotowe"), false),
        };
    }

    public static string Connecting => Tr.User("Verbinde …", "Connecting …", "Łączenie …");

    private static string Explain(string problem, string fallback) => string.IsNullOrWhiteSpace(problem) ? fallback : problem;
}

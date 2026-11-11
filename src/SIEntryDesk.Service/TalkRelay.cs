using System.Collections.Concurrent;
using System.Net.Sockets;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Talk;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Service;

/// <summary>
/// Talkback: grants talking at a door (one app per door, at most 60 s in one go), gets a talkback session from Protect
/// for each talk turn and sends the app's Opus packets as RTP to the door station. The app never learns the target, and
/// the audio stops here as soon as talking is no longer allowed.
/// </summary>
internal sealed class TalkRelay(TimeProvider time, ILogger log, Action<int, IpcMessage> sendToClient) : IDisposable
{
    private sealed class Session(TalkTurn turn, string doorName, TalkbackTarget target, UdpClient udp, DateTimeOffset started)
        : IDisposable
    {
        private readonly RtpPacketizer _rtp = new();

        public TalkTurn Turn => turn;
        public string DoorName => doorName;
        public TalkbackTarget Target => target;
        public DateTimeOffset Started => started;
        public int Frames { get; private set; }

        public void Send(ReadOnlySpan<byte> opus)
        {
            udp.Send(_rtp.Next(opus));
            Frames++;
        }

        public void Dispose() => udp.Dispose();
    }

    /// <summary>Opus packets of 20 ms, that is 50 per second. The service accepts no more.</summary>
    private const int MaxFramesPerSecond = 60;

    private readonly TalkFloor _floor = new(time);
    private readonly ConcurrentDictionary<(string CallId, int ClientId), Session> _sessions = new();

    public int MaxSeconds => (int)_floor.MaxTalk.TotalSeconds;

    public async Task<TalkResultMessage> StartAsync(
        CallInfo call, string cameraId, string doorName, int clientId, string user, ProtectApiClient protect, CancellationToken ct)
    {
        if (!_floor.TryTake(call.DoorId, call.CallId, clientId, user, out var holder))
        {
            log.LogInformation(Tr.Admin("Sprechen abgelehnt für {User}: {Holder} spricht an {Door}", "Talking refused for {User}: {Holder} talks at {Door}"),
                user, holder.Speaker, doorName);
            var speaker = ShortName(holder.Speaker);
            return new TalkResultMessage(call.CallId, false, Tr.User($"{speaker} spricht gerade an diesem PC",
                $"{speaker} is talking on this PC right now", $"{speaker} rozmawia teraz na tym komputerze"));
        }

        TalkbackTarget? target;
        var problem = string.Empty;
        try
        {
            target = await protect.CreateTalkbackSessionAsync(cameraId, p => problem = p, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or System.Security.Authentication.AuthenticationException or System.Text.Json.JsonException)
        {
            _floor.Release(call.CallId, clientId);
            log.LogError(Tr.Admin("Sprechen nicht möglich, Talkback-Sitzung: {Type}: {Message}", "Talking not possible, talkback session: {Type}: {Message}"),
                ex.GetType().Name, ex.Message);
            return new TalkResultMessage(call.CallId, false, ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? CallCoordinator.ProtectRejected
                : CallCoordinator.ProtectUnreachable);
        }
        if (target is null)
        {
            _floor.Release(call.CallId, clientId);
            log.LogError(Tr.Admin("Sprechen nicht möglich, Antwort von Protect unbrauchbar: {Problem}", "Talking not possible, unusable answer from Protect: {Problem}"),
                problem);
            return new TalkResultMessage(call.CallId, false, Tr.User("Die Türstation bietet kein passendes Gegensprechen",
                "The door station offers no suitable talkback", "Stacja drzwiowa nie oferuje odpowiedniej rozmowy"));
        }

        UdpClient udp;
        try
        {
            udp = new UdpClient(target.Address.AddressFamily);
            udp.Connect(target.EndPoint);
        }
        catch (SocketException ex)
        {
            _floor.Release(call.CallId, clientId);
            log.LogError(Tr.Admin("Sprechen nicht möglich, Türstation {Address}: {Message}", "Talking not possible, door station {Address}: {Message}"),
                target.Address, ex.Message);
            return new TalkResultMessage(call.CallId, false, StationUnreachable);
        }

        var session = new Session(holder, doorName, target, udp, time.GetUtcNow());
        if (_sessions.TryRemove((call.CallId, clientId), out var previous))
            previous.Dispose();
        _sessions[(call.CallId, clientId)] = session;
        log.LogInformation(Tr.Admin("Sprechen: {Door} von {User}, Türstation {Address}", "Talking: {Door} by {User}, door station {Address}"),
            doorName, user, target.Address);
        return new TalkResultMessage(call.CallId, true, Tr.User("Sprechen", "Talking", "Rozmowa"), target.SamplingRate, MaxSeconds);
    }

    /// <summary>Button released.</summary>
    public void Stop(string callId, int clientId)
    {
        _floor.Release(callId, clientId);
        End((callId, clientId), reason: null, notify: false);
    }

    /// <summary>An Opus packet from the app. Discarded without a valid grant.</summary>
    public void Audio(string callId, int clientId, string base64)
    {
        if (!_sessions.TryGetValue((callId, clientId), out var session))
            return;
        if (_floor.Current(callId, clientId) is null)
        {
            End((callId, clientId), MaxReached, notify: true);
            return;
        }
        var elapsed = time.GetUtcNow() - session.Started;
        if (session.Frames > (elapsed.TotalSeconds + 1) * MaxFramesPerSecond || base64.Length > 2000)
            return;

        Span<byte> opus = stackalloc byte[1500];
        if (!Convert.TryFromBase64String(base64, opus, out var length) || length is 0 or > 1275)
            return;
        try
        {
            session.Send(opus[..length]);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            log.LogWarning(Tr.Admin("Ton an Türstation {Address} nicht gesendet: {Message}", "Audio to door station {Address} not sent: {Message}"),
                session.Target.Address, ex.Message);
            End((callId, clientId), StationUnreachable, notify: true);
        }
    }

    /// <summary>Call over (after the grace period, or immediately after "answered elsewhere").</summary>
    public void EndCall(string callId, string reason)
    {
        _floor.ReleaseCall(callId);
        foreach (var key in _sessions.Keys.Where(k => k.CallId == callId).ToList())
            End(key, reason, notify: true);
    }

    /// <summary>App disconnected.</summary>
    public void EndClient(int clientId)
    {
        _floor.ReleaseClient(clientId);
        foreach (var key in _sessions.Keys.Where(k => k.ClientId == clientId).ToList())
            End(key, Tr.Admin("App getrennt", "app disconnected"), notify: false);
    }

    /// <summary>Called regularly: ends talking after the maximum duration, even if no more packets arrive.</summary>
    public void Expire()
    {
        foreach (var turn in _floor.Expire())
            End((turn.CallId, turn.ClientId), MaxReached, notify: true);
    }

    private void End((string CallId, int ClientId) key, string? reason, bool notify)
    {
        if (!_sessions.TryRemove(key, out var session))
            return;
        session.Dispose();
        var seconds = (time.GetUtcNow() - session.Started).TotalSeconds;
        log.LogInformation(Tr.Admin("Sprechen beendet: {Door} von {User} nach {Seconds:F1} s, {Frames} Pakete{Reason}",
                "Talking ended: {Door} by {User} after {Seconds:F1} s, {Frames} packets{Reason}"),
            session.DoorName, session.Turn.Speaker, seconds, session.Frames, reason is null ? string.Empty : $" ({reason})");
        if (notify && reason is not null)
            sendToClient(key.ClientId, new TalkEndedMessage(key.CallId, reason));
    }

    private string MaxReached => Tr.User($"Höchstdauer von {MaxSeconds} s erreicht", $"Maximum of {MaxSeconds} s reached",
        $"Osiągnięto maksymalny czas {MaxSeconds} s");

    private static string StationUnreachable => Tr.User("Türstation nicht erreichbar", "Door station not reachable", "Stacja drzwiowa nieosiągalna");

    /// <summary>"DOMAIN\anna" → "anna" for display.</summary>
    internal static string ShortName(string user) => user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}

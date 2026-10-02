using System.Collections.Concurrent;
using System.Net.Sockets;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Ipc;
using SIEntryDesk.Core.Protect;
using SIEntryDesk.Core.Talk;

namespace SIEntryDesk.Service;

/// <summary>
/// Gegensprechen: gibt das Sprechen an einer Tür frei (eine App pro Tür, höchstens 60 s am Stück), holt pro Sprechstoss
/// eine Talkback-Sitzung bei Protect und schickt die Opus-Pakete der App als RTP an die Türstation. Die App erfährt das
/// Ziel nicht, und der Ton endet hier, sobald das Sprechen nicht mehr erlaubt ist.
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

    /// <summary>Opus-Pakete von 20 ms, also 50 pro Sekunde. Mehr nimmt der Dienst nicht an.</summary>
    private const int MaxFramesPerSecond = 60;

    private readonly TalkFloor _floor = new(time);
    private readonly ConcurrentDictionary<(string CallId, int ClientId), Session> _sessions = new();

    public int MaxSeconds => (int)_floor.MaxTalk.TotalSeconds;

    public async Task<TalkResultMessage> StartAsync(
        CallInfo call, string cameraId, string doorName, int clientId, string user, ProtectApiClient protect, CancellationToken ct)
    {
        if (!_floor.TryTake(call.DoorId, call.CallId, clientId, user, out var holder))
        {
            log.LogInformation("Sprechen abgelehnt für {User}: {Holder} spricht an {Door}", user, holder.Speaker, doorName);
            return new TalkResultMessage(call.CallId, false, $"{ShortName(holder.Speaker)} spricht gerade an diesem PC");
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
            log.LogError("Sprechen nicht möglich, Talkback-Sitzung: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return new TalkResultMessage(call.CallId, false, ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? "Protect-Schlüssel abgelehnt"
                : "Protect nicht erreichbar");
        }
        if (target is null)
        {
            _floor.Release(call.CallId, clientId);
            log.LogError("Sprechen nicht möglich, Antwort von Protect unbrauchbar: {Problem}", problem);
            return new TalkResultMessage(call.CallId, false, "Die Türstation bietet kein passendes Gegensprechen");
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
            log.LogError("Sprechen nicht möglich, Türstation {Address}: {Message}", target.Address, ex.Message);
            return new TalkResultMessage(call.CallId, false, "Türstation nicht erreichbar");
        }

        var session = new Session(holder, doorName, target, udp, time.GetUtcNow());
        if (_sessions.TryRemove((call.CallId, clientId), out var previous))
            previous.Dispose();
        _sessions[(call.CallId, clientId)] = session;
        log.LogInformation("Sprechen: {Door} von {User}, Türstation {Address}", doorName, user, target.Address);
        return new TalkResultMessage(call.CallId, true, "Sprechen", target.SamplingRate, MaxSeconds);
    }

    /// <summary>Taste losgelassen.</summary>
    public void Stop(string callId, int clientId)
    {
        _floor.Release(callId, clientId);
        End((callId, clientId), reason: null, notify: false);
    }

    /// <summary>Ein Opus-Paket der App. Ohne gültige Freigabe wird es verworfen.</summary>
    public void Audio(string callId, int clientId, string base64)
    {
        if (!_sessions.TryGetValue((callId, clientId), out var session))
            return;
        if (_floor.Current(callId, clientId) is null)
        {
            End((callId, clientId), $"Höchstdauer von {MaxSeconds} s erreicht", notify: true);
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
            log.LogWarning("Ton an Türstation {Address} nicht gesendet: {Message}", session.Target.Address, ex.Message);
            End((callId, clientId), "Türstation nicht erreichbar", notify: true);
        }
    }

    /// <summary>Ruf zu Ende (nach der Nachfrist bzw. sofort nach „anderswo angenommen“).</summary>
    public void EndCall(string callId, string reason)
    {
        _floor.ReleaseCall(callId);
        foreach (var key in _sessions.Keys.Where(k => k.CallId == callId).ToList())
            End(key, reason, notify: true);
    }

    /// <summary>App getrennt.</summary>
    public void EndClient(int clientId)
    {
        _floor.ReleaseClient(clientId);
        foreach (var key in _sessions.Keys.Where(k => k.ClientId == clientId).ToList())
            End(key, "App getrennt", notify: false);
    }

    /// <summary>Regelmässig aufgerufen: beendet Sprechen nach der Höchstdauer, auch wenn keine Pakete mehr kommen.</summary>
    public void Expire()
    {
        foreach (var turn in _floor.Expire())
            End((turn.CallId, turn.ClientId), $"Höchstdauer von {MaxSeconds} s erreicht", notify: true);
    }

    private void End((string CallId, int ClientId) key, string? reason, bool notify)
    {
        if (!_sessions.TryRemove(key, out var session))
            return;
        session.Dispose();
        var seconds = (time.GetUtcNow() - session.Started).TotalSeconds;
        log.LogInformation("Sprechen beendet: {Door} von {User} nach {Seconds:F1} s, {Frames} Pakete{Reason}",
            session.DoorName, session.Turn.Speaker, seconds, session.Frames, reason is null ? string.Empty : $" ({reason})");
        if (notify && reason is not null)
            sendToClient(key.ClientId, new TalkEndedMessage(key.CallId, reason));
    }

    /// <summary>„DOMAIN\anna“ → „anna“ für die Anzeige.</summary>
    private static string ShortName(string user) => user.Contains('\\') ? user[(user.LastIndexOf('\\') + 1)..] : user;

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }
}

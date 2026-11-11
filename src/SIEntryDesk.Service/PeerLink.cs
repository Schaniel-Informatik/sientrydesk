using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SIEntryDesk.Core.Peers;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Service;

/// <summary>
/// Signed UDP broadcast to the other PCs in the same network segment: who answered a call (decision 2026-11-03).
/// Routers do not forward broadcasts, so PCs in another segment neither send to nor hear from this one. install.ps1
/// opens the port for the local subnet only.
/// </summary>
internal sealed class PeerLink(byte[] key, TimeProvider time, ILogger log) : IDisposable
{
    /// <summary>UDP may lose a datagram, and a PC may get the ring from Access a little later: repeat after 1 and
    /// 3 s. Duplicates do not matter, the receivers know the announcement already.</summary>
    private static readonly TimeSpan[] Repeats = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];
    private static readonly TimeSpan RejectLogInterval = TimeSpan.FromMinutes(10);

    private UdpClient? _udp;
    private DateTimeOffset _lastRejectLog = DateTimeOffset.MinValue;

    /// <summary>Why this PC does not receive, for the check. Null if the port is open.</summary>
    public string? Problem { get; private set; }

    public async Task RunAsync(Action<PeerAnnouncement> received, CancellationToken ct)
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            _udp.Client.ExclusiveAddressUse = true;
            // Windows reports an ICMP "port unreachable" as an error on the next receive. Irrelevant here.
            _udp.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, PeerCodec.Port));
        }
        catch (SocketException ex)
        {
            Problem = Tr.Admin($"Dienst empfängt nicht, UDP {PeerCodec.Port} belegt ({ex.SocketErrorCode})",
                $"Service does not receive, UDP {PeerCodec.Port} in use ({ex.SocketErrorCode})");
            log.LogError(Tr.Admin("Abstimmung mit anderen PCs aus: UDP {Port} nicht verfügbar: {Message}",
                "Coordination with other PCs off: UDP {Port} not available: {Message}"), PeerCodec.Port, ex.Message);
            _udp?.Dispose();
            _udp = null;
            return;
        }
        log.LogInformation(Tr.Admin("Abstimmung mit anderen PCs: UDP {Port}", "Coordination with other PCs: UDP {Port}"), PeerCodec.Port);

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                log.LogWarning(Tr.Admin("Abstimmung mit anderen PCs: Empfang gestört: {Message}", "Coordination with other PCs: receiving disturbed: {Message}"),
                    ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
                continue;
            }

            var result = PeerCodec.TryDecode(datagram.Buffer, key, time.GetUtcNow(), out var announcement);
            if (result == PeerDecodeResult.Ok)
                received(announcement!);
            else
                LogRejected(datagram.RemoteEndPoint.Address, result);
        }
    }

    /// <summary>Sends to the broadcast address of every network of this PC, repeated in the background.</summary>
    public void Announce(PeerAnnouncement announcement, CancellationToken ct)
    {
        if (_udp is not { } udp)
            return;
        var datagram = PeerCodec.Encode(announcement, key);
        _ = Task.Run(async () =>
        {
            var waited = TimeSpan.Zero;
            foreach (var at in Repeats)
            {
                try
                {
                    await Task.Delay(at - waited, time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                waited = at;
                foreach (var target in BroadcastAddresses())
                {
                    try
                    {
                        await udp.SendAsync(datagram, new IPEndPoint(target, PeerCodec.Port), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        if (ex is not SocketException)
                            return;
                        log.LogWarning(Tr.Admin("Abstimmung mit anderen PCs: an {Target} nicht gesendet: {Message}",
                            "Coordination with other PCs: not sent to {Target}: {Message}"), target, ex.Message);
                    }
                }
            }
        });
    }

    /// <summary>Directed broadcast (e.g. x.x.x.255) per IPv4 network. 255.255.255.255 would leave through one network
    /// only.</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var targets = new HashSet<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.PrefixLength is < 8 or > 30)
                    continue;
                var address = unicast.Address.GetAddressBytes();
                if (address[0] == 169 && address[1] == 254)
                    continue;
                var host = uint.MaxValue >> unicast.PrefixLength;
                var value = (uint)address[0] << 24 | (uint)address[1] << 16 | (uint)address[2] << 8 | address[3];
                var broadcast = value | host;
                targets.Add(new IPAddress([(byte)(broadcast >> 24), (byte)(broadcast >> 16), (byte)(broadcast >> 8), (byte)broadcast]));
            }
        }
        return targets;
    }

    /// <summary>At most one line per <see cref="RejectLogInterval"/>, so that nobody can fill the log.</summary>
    private void LogRejected(IPAddress from, PeerDecodeResult result)
    {
        var now = time.GetUtcNow();
        if (now - _lastRejectLog < RejectLogInterval)
            return;
        _lastRejectLog = now;
        if (result == PeerDecodeResult.Stale)
            log.LogWarning(Tr.Admin("Abstimmung mit anderen PCs: Meldung von {From} verworfen, Uhrzeit weicht mehr als {Seconds} s ab",
                    "Coordination with other PCs: message from {From} discarded, clock differs by more than {Seconds} s"),
                from, PeerCodec.MaxClockSkew.TotalSeconds);
        else
            log.LogWarning(Tr.Admin("Abstimmung mit anderen PCs: ungültige Meldung von {From} verworfen (anderer Access-Token?)",
                "Coordination with other PCs: invalid message from {From} discarded (different Access token?)"), from);
    }

    public void Dispose()
    {
        _udp?.Dispose();
        _udp = null;
    }
}

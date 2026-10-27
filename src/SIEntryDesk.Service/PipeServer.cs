using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.Service;

/// <summary>A connected app: connection number and Windows user.</summary>
internal sealed record PipeClient(int Id, string User);

/// <summary>
/// Local named pipe for the tray apps of the logged-on users. Access only for interactively logged-on users
/// (read/write), SYSTEM, administrators and the service itself. Windows provides the user name, it ends up as the
/// initiator in the Access log.
/// </summary>
internal sealed class PipeServer(ServiceState state, ILogger<PipeServer> log) : BackgroundService
{
    private const int MaxClients = 20;
    private readonly ConcurrentDictionary<int, ClientConnection> _clients = new();
    private int _nextId;

    /// <summary>(Request of the app, app) → answer to this app. Set by the coordinator.</summary>
    public Func<IpcMessage, PipeClient, CancellationToken, Task<IpcMessage?>>? RequestHandler { get; set; }

    /// <summary>An app has disconnected (number as in <see cref="PipeClient.Id"/>).</summary>
    public event Action<int>? ClientDisconnected;

    public void Broadcast(IpcMessage message)
    {
        foreach (var client in _clients.Values)
            client.Send(message);
    }

    /// <summary>Message to one app only, if it is still connected.</summary>
    public void Send(int clientId, IpcMessage message)
    {
        if (_clients.TryGetValue(clientId, out var client))
            client.Send(message);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // The first instance must be ours. If another process already holds the name, this is reported.
        var firstInstance = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(firstInstance);
                firstInstance = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (firstInstance)
                    log.LogError("Pipe-Name {Name} ist bereits belegt, möglicherweise von einem fremden Prozess", IpcProtocol.PipeName);
                else
                    log.LogWarning("Keine freie Pipe-Instanz: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException ex)
            {
                log.LogWarning("Verbindungsaufbau der App fehlgeschlagen: {Message}", ex.Message);
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var client = new ClientConnection(Interlocked.Increment(ref _nextId), pipe, ClientUser(pipe));
            _clients[client.Id] = client;
            _ = ServeAsync(client, ct);
        }
    }

    private async Task ServeAsync(ClientConnection client, CancellationToken ct)
    {
        log.LogInformation("App verbunden: {User}", client.User);
        try
        {
            foreach (var message in state.Snapshot())
                client.Send(message);
            var writer = client.RunWriterAsync(ct);
            var reader = new IpcReader(client.Pipe);
            var who = new PipeClient(client.Id, client.User);
            while (await reader.ReadAsync(ct).ConfigureAwait(false) is { } message)
            {
                if (RequestHandler is { } handler &&
                    await handler(message, who, ct).ConfigureAwait(false) is { } reply)
                    client.Send(reply);
            }
            client.Dispose();
            await writer.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Dispose();
            ClientDisconnected?.Invoke(client.Id);
            log.LogInformation("App getrennt: {User}", client.User);
        }
    }

    private static string ClientUser(NamedPipeServerStream pipe)
    {
        try
        {
            return pipe.GetImpersonationUserName();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "unbekannt";
        }
    }

    private static NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using (var self = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        // Logged-on users may connect, but not create their own instances of this pipe.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, options, 4096, 4096, security);
    }

    private sealed class ClientConnection(int id, NamedPipeServerStream pipe, string user) : IDisposable
    {
        private readonly Channel<IpcMessage> _outbox = Channel.CreateBounded<IpcMessage>(
            new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public int Id => id;
        public string User => user;
        public NamedPipeServerStream Pipe => pipe;

        public void Send(IpcMessage message) => _outbox.Writer.TryWrite(message);

        public async Task RunWriterAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var message in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await IpcProtocol.WriteAsync(pipe, message, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _outbox.Writer.TryComplete();
            pipe.Dispose();
        }
    }
}

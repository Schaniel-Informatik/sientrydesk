using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows.Threading;
using SIEntryDesk.Core.Ipc;

namespace SIEntryDesk.App;

public sealed record ServiceClientStatus(bool Connected, StatusMessage? Service);

/// <summary>Verbindung zum lokalen Dienst über die Named Pipe, mit automatischem Neuaufbau.
/// Ereignisse werden auf dem UI-Thread ausgelöst.</summary>
internal sealed class ServiceClient(Dispatcher dispatcher)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public event Action<ServiceClientStatus>? StatusChanged;
    public event Action<IpcMessage>? MessageReceived;

    public Task RunAsync(CancellationToken ct) => Task.Run(() => LoopAsync(ct), ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Identification genügt dem Dienst, um den Windows-Benutzer festzustellen.
                using var pipe = new NamedPipeClientStream(
                    ".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
                _pipe = pipe;
                var reader = new IpcReader(pipe);
                while (await reader.ReadAsync(ct).ConfigureAwait(false) is { } message)
                {
                    if (message is StatusMessage status)
                        Post(() => StatusChanged?.Invoke(new ServiceClientStatus(true, status)));
                    Post(() => MessageReceived?.Invoke(message));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException
                                           or UnauthorizedAccessException or OperationCanceledException)
            {
            }
            finally
            {
                _pipe = null;
            }

            Post(() => StatusChanged?.Invoke(new ServiceClientStatus(false, null)));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<bool> SendAsync(IpcMessage message)
    {
        var pipe = _pipe;
        if (pipe is null)
            return false;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await IpcProtocol.WriteAsync(pipe, message, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Post(Action action) => dispatcher.InvokeAsync(action);
}

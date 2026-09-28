using System.Text.Json;
using System.Text.Json.Serialization;

namespace SIEntryDesk.Core.Ipc;

/// <summary>Eine JSON-Nachricht pro Zeile (UTF-8, "\n"), höchstens <see cref="MaxMessageBytes"/> lang.</summary>
public static class IpcProtocol
{
    public const string PipeName = "SIEntryDesk";
    public const int MaxMessageBytes = 64 * 1024;

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Serialize(IpcMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        var framed = new byte[json.Length + 1];
        json.CopyTo(framed, 0);
        framed[^1] = (byte)'\n';
        return framed;
    }

    public static async Task WriteAsync(Stream stream, IpcMessage message, CancellationToken ct)
    {
        await stream.WriteAsync(Serialize(message), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Unbekannte oder fehlerhafte Nachrichten ergeben null und werden übersprungen.</summary>
    public static IpcMessage? Deserialize(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize<IpcMessage>(line, Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Liest Zeilen aus einem Stream. Zu lange Zeilen beenden die Verbindung.</summary>
public sealed class IpcReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[IpcProtocol.MaxMessageBytes + 1];
    private int _start;
    private int _end;

    /// <summary>Nächste gültige Nachricht, oder null wenn der Stream endet.</summary>
    public async Task<IpcMessage?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var line = _buffer.AsSpan(_start, newline - _start);
                _start = newline + 1;
                var message = IpcProtocol.Deserialize(line);
                if (message is not null)
                    return message;
                continue;
            }

            if (_end - _start >= IpcProtocol.MaxMessageBytes)
                throw new InvalidDataException("IPC-Nachricht zu lang");
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            var read = await stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct).ConfigureAwait(false);
            if (read == 0)
                return null;
            _end += read;
        }
    }
}

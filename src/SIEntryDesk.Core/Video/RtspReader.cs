using System.Globalization;
using System.Text;

namespace SIEntryDesk.Core.Video;

/// <summary>Ein Stück aus einem RTSP-Datenstrom über TCP.</summary>
internal abstract record RtspChunk;

/// <summary>Eingebetteter Binärrahmen: '$', Kanal, Länge (2 Byte), RTP/RTCP-Daten. Raw enthält alles.</summary>
internal sealed record RtspInterleaved(byte[] Raw) : RtspChunk;

/// <summary>Anfrage oder Antwort. Head ist Startzeile plus Kopfzeilen ohne die abschliessende Leerzeile.</summary>
internal sealed record RtspText(string Head, byte[] Body) : RtspChunk
{
    public string StartLine => Head.Split("\r\n", 2)[0];

    public byte[] ToBytes()
    {
        var head = Encoding.Latin1.GetBytes(Head + "\r\n\r\n");
        var bytes = new byte[head.Length + Body.Length];
        head.CopyTo(bytes, 0);
        Body.CopyTo(bytes, head.Length);
        return bytes;
    }

    public string? Header(string name)
    {
        foreach (var line in Head.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }
}

/// <summary>Zerlegt einen RTSP-Datenstrom in Textnachrichten und Binärrahmen, mit festen Grössengrenzen.</summary>
internal sealed class RtspReader(Stream stream)
{
    public const int MaxHeadBytes = 16 * 1024;
    public const int MaxBodyBytes = 64 * 1024;

    private readonly byte[] _buffer = new byte[4 + ushort.MaxValue + MaxHeadBytes];
    private int _start;
    private int _end;

    /// <summary>Nächstes Stück, oder null wenn der Strom endet.</summary>
    public async Task<RtspChunk?> ReadAsync(CancellationToken ct)
    {
        if (!await EnsureAsync(1, ct).ConfigureAwait(false))
            return null;

        if (_buffer[_start] == (byte)'$')
        {
            if (!await EnsureAsync(4, ct).ConfigureAwait(false))
                return null;
            var length = 4 + ((_buffer[_start + 2] << 8) | _buffer[_start + 3]);
            if (!await EnsureAsync(length, ct).ConfigureAwait(false))
                return null;
            return new RtspInterleaved(Take(length));
        }

        int headEnd;
        while ((headEnd = IndexOfHeadEnd()) < 0)
        {
            if (_end - _start >= MaxHeadBytes)
                throw new InvalidDataException("RTSP-Kopf zu lang");
            if (!await FillAsync(ct).ConfigureAwait(false))
                return null;
        }

        var head = Encoding.Latin1.GetString(_buffer, _start, headEnd - _start);
        _start = headEnd + 4;
        var message = new RtspText(head, []);
        var lengthHeader = message.Header("Content-Length");
        var bodyLength = 0;
        if (lengthHeader is not null &&
            (!int.TryParse(lengthHeader, NumberStyles.None, CultureInfo.InvariantCulture, out bodyLength) || bodyLength > MaxBodyBytes))
            throw new InvalidDataException("Ungültige Content-Length");
        if (!await EnsureAsync(bodyLength, ct).ConfigureAwait(false))
            return null;
        return message with { Body = Take(bodyLength) };
    }

    private byte[] Take(int count)
    {
        var bytes = _buffer.AsSpan(_start, count).ToArray();
        _start += count;
        return bytes;
    }

    private int IndexOfHeadEnd()
    {
        var index = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8);
        return index < 0 ? -1 : _start + index;
    }

    private async Task<bool> EnsureAsync(int count, CancellationToken ct)
    {
        while (_end - _start < count)
        {
            if (!await FillAsync(ct).ConfigureAwait(false))
                return false;
        }
        return true;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length)
            throw new InvalidDataException("RTSP-Puffer voll");
        var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (read == 0)
            return false;
        _end += read;
        return true;
    }
}

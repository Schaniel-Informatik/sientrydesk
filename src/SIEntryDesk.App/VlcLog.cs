using System.IO;
using LibVLCSharp.Shared;

namespace SIEntryDesk.App;

/// <summary>
/// Protokoll des Videoplayers unter %LOCALAPPDATA%\SIEntryDesk\vlc.log: welche Module geladen wurden, Warnungen und
/// Fehler. Hilft, wenn das Livebild schwarz oder stumm bleibt (fehlt ein Plugin, steht dort „no … module matched“).
/// Wird bei jedem Start neu angelegt und ist auf 512 KB begrenzt. Enthält nur Einmal-Adressen auf 127.0.0.1.
/// </summary>
internal static class VlcLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static void Attach(LibVLC libVlc)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SIEntryDesk");
            Directory.CreateDirectory(dir);
            _writer = new StreamWriter(Path.Combine(dir, "vlc.log"), append: false) { AutoFlush = true };
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} SI EntryDesk {TrayIcon.AppVersion}, LibVLC {libVlc.Version}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        libVlc.Log += (_, e) =>
        {
            var useful = e.Level >= LogLevel.Warning || e.Message.StartsWith("using ", StringComparison.Ordinal) ||
                         e.Message.Contains("module matched", StringComparison.Ordinal);
            if (!useful)
                return;
            lock (Gate)
            {
                if (_writer is null || _writer.BaseStream.Length > MaxBytes)
                    return;
                try
                {
                    _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {e.Level,-7} {e.Module}: {e.Message}");
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _writer = null;
                }
            }
        };
    }
}

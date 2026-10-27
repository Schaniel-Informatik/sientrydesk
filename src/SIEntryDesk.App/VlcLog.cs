using System.IO;
using LibVLCSharp.Shared;

namespace SIEntryDesk.App;

/// <summary>
/// Log of the video player under %LOCALAPPDATA%\SIEntryDesk\vlc.log: which modules were loaded, warnings and errors.
/// Helps when the live view stays black or silent (if a plugin is missing, it says "no … module matched"). Created anew
/// at every start and limited to 512 KB. Contains only one-time addresses on 127.0.0.1.
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

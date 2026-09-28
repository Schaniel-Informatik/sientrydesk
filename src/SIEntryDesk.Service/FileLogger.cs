using System.Text;
using Microsoft.Extensions.Logging;

namespace SIEntryDesk.Service;

/// <summary>Einfaches Tagesprotokoll unter %ProgramData%\SIEntryDesk\logs, 14 Tage aufbewahrt. Keine Tokens loggen.</summary>
internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private const int KeepDays = 14;
    private readonly object _gate = new();
    private DateOnly _currentDay;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var now = DateTimeOffset.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(level switch
            {
                LogLevel.Critical => "KRIT",
                LogLevel.Error => "FEHL",
                LogLevel.Warning => "WARN",
                LogLevel.Information => "INFO",
                _ => "DBG ",
            })
            .Append(' ').Append(category.Split('.')[^1]).Append(": ").Append(message);
        if (ex is not null)
            line.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        line.AppendLine();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var today = DateOnly.FromDateTime(now.DateTime);
                if (today != _currentDay)
                {
                    _currentDay = today;
                    Cleanup(today);
                }
                File.AppendAllText(Path.Combine(directory, $"service-{today:yyyyMMdd}.log"), line.ToString());
            }
            catch (IOException)
            {
                // Protokollieren darf den Dienst nicht stören.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Cleanup(DateOnly today)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "service-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file).Replace("service-", "", StringComparison.Ordinal);
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", out var day) && day < today.AddDays(-KeepDays))
                File.Delete(file);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}

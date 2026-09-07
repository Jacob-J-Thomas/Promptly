using Microsoft.Extensions.Logging;

namespace Promptly.IntegrationTests;

internal sealed class IntegrationFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _writeLock = new();

    public IntegrationFileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"Promptly integration server log started {DateTimeOffset.UtcNow:O}{Environment.NewLine}");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (_writeLock)
        {
            using var writer = File.AppendText(_path);
            writer.Write(DateTimeOffset.UtcNow.ToString("O"));
            writer.Write(" [");
            writer.Write(level);
            writer.Write("] ");
            writer.Write(category);
            writer.Write('[');
            writer.Write(eventId.Id);
            writer.Write("]: ");
            writer.WriteLine(message);
            if (exception is not null)
            {
                writer.WriteLine(exception);
            }
        }
    }

    private sealed class FileLogger(
        IntegrationFileLoggerProvider provider,
        string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(categoryName, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}

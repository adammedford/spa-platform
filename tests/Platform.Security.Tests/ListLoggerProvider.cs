using Microsoft.Extensions.Logging;

namespace Platform.Security.Tests;

/// <summary>
/// Collects every formatted log line in a list, so a test can assert a refusal's rule id and search
/// for values that must never be logged. Shared by <see cref="SecurityHost"/> and <see cref="GateHost"/>.
/// Readers lock the list, because Kestrel writes from its own threads.
/// </summary>
internal sealed class ListLoggerProvider(List<string> lines) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ListLogger(lines);

    public void Dispose()
    {
    }

    private sealed class ListLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}

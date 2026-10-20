using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Platform.Hosting.Tests;

/// <summary>
/// Every log line the host writes, rendered, with its category. The asset endpoint's refusals are
/// logged under a rule id and never with the path (docs/plan/02-contracts.md, Errors: "Tests assert
/// on both the code and the rule id"), and a test can only hold it to that by reading the lines.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> lines = new();

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Lines => [.. lines];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue((category, logLevel, formatter(state, exception)));
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Platform.Proxy.Tests;

/// <summary>
/// Every log line a <see cref="ProxyHost"/> writes, with its category and event id, so a test can hold
/// the unknown-api endpoint's Warning line to its template: a <c>LoggerMessage</c> method carries an
/// event id named after it, and an ad hoc <c>LogWarning</c> call carries none (#162).
///
/// <see cref="Platform.Testing.CapturedLog"/> is the host's capture, but its
/// <see cref="Platform.Testing.CapturedLine"/> has no event id (change request issue-145, section 4),
/// so this recorder keeps it for that one test; change request issue-148-captured-line-event-id asks
/// for it.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogLine> lines = new();

    public IReadOnlyList<LogLine> Lines => [.. lines];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<LogLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(new LogLine(category, logLevel, eventId, formatter(state, exception)));
    }
}

/// <summary>One line a <see cref="RecordingLoggerProvider"/> recorded, rendered.</summary>
internal sealed record LogLine(string Category, LogLevel Level, EventId EventId, string Message);

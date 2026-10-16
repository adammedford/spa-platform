using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Platform.Identity.Tests;

/// <summary>Every log line the gateway's services write, formatted, with its category.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, string Message)> lines = new();

    public IReadOnlyList<(string Category, string Message)> Lines => [.. lines];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, string)> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue((category, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}

using Microsoft.Extensions.Logging;

namespace Platform.Registry.Tests;

/// <summary>
/// Collects every formatted log line, with any exception attached to it, so a test can search them
/// for values that must never be logged (N-16). The same shape as Platform.Security.Tests' provider,
/// copied rather than referenced because that project belongs to another workstream.
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<string> lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (lines)
            {
                return [.. lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new ListLogger(lines);

    public ILogger<T> CreateLogger<T>() => new Logger<T>(new SingleProviderFactory(this));

    public void Dispose()
    {
    }

    private sealed class SingleProviderFactory(ILoggerProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
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
                lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
            }
        }
    }
}

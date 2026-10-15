using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Gateway.TestHarness;

/// <summary>
/// Every line the gateway logged, as stdout would carry it. N-16 searches these for canary values, and
/// F-11 reads the request line from them.
///
/// Each entry is rendered by the formatter the gateway's console logger is configured with (task 6.4's
/// JSON formatter), so a test reads the same bytes the deployed log stream holds, not the structured
/// state behind them. The console logger stays registered, so the lines also reach the test output.
/// </summary>
public sealed class CapturedLog
{
    private readonly List<string> lines = [];

    /// <summary>Every line so far, oldest first.</summary>
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

    /// <summary>The lines that are JSON objects, parsed. A line the formatter did not write as JSON is skipped.</summary>
    public IReadOnlyList<JsonElement> Json
    {
        get
        {
            List<JsonElement> parsed = [];
            foreach (string line in Lines)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        parsed.Add(document.RootElement.Clone());
                    }
                }
                catch (JsonException)
                {
                }
            }

            return parsed;
        }
    }

    internal void Add(string line)
    {
        lock (lines)
        {
            lines.Add(line);
        }
    }

    /// <summary>
    /// A logger provider that renders each entry with the named console formatter into lines of its own.
    /// The formatter is resolved on first use, because it is registered by the gateway's own composition.
    /// </summary>
    internal sealed class Provider(CapturedLog log, Func<ConsoleFormatter?> formatter) : ILoggerProvider, ISupportExternalScope
    {
        private readonly Lazy<ConsoleFormatter?> resolved = new(formatter);
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

        // The factory hands over the scopes the request pipeline pushed, as it does to the console
        // logger, so the formatter sees the same scope values on both.
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(Provider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider.scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> render)
            {
                ConsoleFormatter? formatter = provider.resolved.Value;
                if (formatter is null)
                {
                    // No platform formatter: keep everything the entry could put on a line, so a canary
                    // search errs on the side of finding something.
                    provider.Add(render(state, exception) + (exception is null ? string.Empty : " " + exception));
                    return;
                }

                using StringWriter writer = new();
                formatter.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, render),
                    provider.scopes, writer);
                foreach (string line in writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    provider.Add(line.TrimEnd('\r'));
                }
            }
        }

        private void Add(string line) => log.Add(line);
    }
}

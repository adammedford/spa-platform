using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Platform.Testing;

/// <summary>
/// One line a host logged, as a record. <see cref="Text"/> is what a never-logged search (N-16) reads:
/// the message and, when there is one, the exception with its inner exceptions, because a credential
/// in an exception message is something the search must find.
/// </summary>
/// <param name="State">
/// The structured state the line was logged with, as name and value pairs; for a message template
/// that is each placeholder's value and <c>{OriginalFormat}</c>. Empty for a state that is not such a
/// list.
/// </param>
/// <param name="EventId">
/// The event id the line was logged with. A <c>[LoggerMessage]</c> template's line carries one named
/// after its method; an ad hoc <c>Log*</c> call's carries none, though its <see cref="State"/> is the
/// same, so this is what tells the two apart (#540).
/// </param>
public sealed record CapturedLine(
    string Category,
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> State,
    EventId EventId)
{
    /// <summary>The message and the exception's full text, joined by a space.</summary>
    public string Text => Exception is null ? Message : Message + " " + Exception;

    public override string ToString() => $"{Level} {Category}: {Text}";
}

/// <summary>
/// Every line a test host or the gateway test harness logged (change request issue-145, section 4):
/// as <see cref="CapturedLine"/> records always, and as the lines stdout would carry when a console
/// formatter is configured, in <see cref="Rendered"/> and <see cref="Json"/>.
///
/// A <see cref="StageHost"/> registers one through <see cref="ProviderFor"/> after its test host's
/// <c>Services</c> delegate. A test with no host attaches one through <see cref="Provider"/> (records
/// only) or takes a logger from <see cref="CreateLogger{T}"/>.
/// </summary>
public sealed class CapturedLog
{
    private readonly object gate = new();
    private readonly List<CapturedLine> lines = [];
    private readonly List<string> rendered = [];

    public CapturedLog()
    {
        Provider = new CapturingProvider(this, formatter: null);
    }

    /// <summary>Every line so far, oldest first: a snapshot, so a test reads a list that cannot change under it.</summary>
    public IReadOnlyList<CapturedLine> Lines
    {
        get
        {
            lock (gate)
            {
                return [.. lines];
            }
        }
    }

    /// <summary>
    /// The lines as the configured console formatter wrote them, oldest first, one entry per output
    /// line. Empty when no console formatter is configured: a capture invents no rendering stdout
    /// would not carry.
    /// </summary>
    public IReadOnlyList<string> Rendered
    {
        get
        {
            lock (gate)
            {
                return [.. rendered];
            }
        }
    }

    /// <summary>The <see cref="Rendered"/> lines that are JSON objects, parsed. Any other line is skipped.</summary>
    public IReadOnlyList<JsonElement> Json
    {
        get
        {
            List<JsonElement> parsed = [];
            foreach (string line in Rendered)
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

    /// <summary>
    /// A provider that records only, for <c>AddSingleton&lt;ILoggerProvider&gt;</c> or
    /// <c>AddProvider</c> in a test with no stage host. Disposing it does nothing, so a container that
    /// owns it cannot end the capture.
    /// </summary>
    public ILoggerProvider Provider { get; }

    /// <summary>
    /// The lines logged under <paramref name="rule"/>, read from each line's <c>Rule</c> state
    /// property and never from its text: every rule-logging site names the property <c>Rule</c>, but
    /// the wording differs ("refused under {Rule}", "rule={Rule}"), and shared decision S1 leaves it
    /// each stage's own. <paramref name="level"/>, when given, keeps only lines at that level.
    /// </summary>
    public IReadOnlyList<CapturedLine> LinesFor(string rule, LogLevel? level = null) =>
        [.. Lines.Where(line =>
            (level is null || line.Level == level)
            && line.State.Any(pair => pair.Key == "Rule" && string.Equals(pair.Value?.ToString(), rule, StringComparison.Ordinal)))];

    /// <summary>Whether any line's <see cref="CapturedLine.Text"/> contains <paramref name="value"/>, ordinally.</summary>
    public bool Contains(string value) => Lines.Any(line => line.Text.Contains(value, StringComparison.Ordinal));

    /// <summary>
    /// A provider that records, and also renders through the console formatter
    /// <paramref name="services"/> configure. The formatter is resolved on first use, because the
    /// host's own composition registers it, and by <see cref="ConsoleLoggerOptions.FormatterName"/>,
    /// never by type: the platform's JSON formatter is internal to <c>Platform.Telemetry</c>.
    /// </summary>
    public ILoggerProvider ProviderFor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new CapturingProvider(this, () => ConfiguredFormatter(services));
    }

    /// <summary>A logger that writes here, at every level, for a test over one class with no host.</summary>
    public ILogger<T> CreateLogger<T>()
    {
        ILoggerFactory factory = LoggerFactory.Create(logging => logging
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(Provider));
        return factory.CreateLogger<T>();
    }

    /// <summary>
    /// The formatter stdout would be written with, or null when the host writes no console log. A
    /// console formatter counts as configured only when a console logger is among the host's
    /// providers: the console options and the built-in formatters outlive <c>ClearProviders</c>, so a
    /// name alone would render through a formatter no stdout uses.
    /// </summary>
    private static ConsoleFormatter? ConfiguredFormatter(IServiceProvider services)
    {
        if (!services.GetServices<ILoggerProvider>().OfType<ConsoleLoggerProvider>().Any())
        {
            return null;
        }

        string? name = services.GetService<IOptionsMonitor<ConsoleLoggerOptions>>()?.CurrentValue.FormatterName;
        return name is null
            ? null
            : services.GetServices<ConsoleFormatter>().FirstOrDefault(formatter => formatter.Name == name);
    }

    private void Record(CapturedLine line, IReadOnlyList<string> output)
    {
        lock (gate)
        {
            lines.Add(line);
            rendered.AddRange(output);
        }
    }

    private sealed class CapturingProvider(CapturedLog log, Func<ConsoleFormatter?>? formatter)
        : ILoggerProvider, ISupportExternalScope
    {
        private readonly Lazy<ConsoleFormatter?> resolved = new(formatter ?? (() => null));
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();

        private CapturedLog Log { get; } = log;

        // The factory hands over the scopes the request pipeline pushed, as it does to the console
        // logger, so the formatter is handed what the console logger would be.
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider.scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> render)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                IReadOnlyList<KeyValuePair<string, object?>> pairs =
                    state is IReadOnlyList<KeyValuePair<string, object?>> list ? [.. list] : [];
                CapturedLine line = new(category, logLevel, render(state, exception), exception, pairs, eventId);

                List<string> output = [];
                if (provider.resolved.Value is { } formatter)
                {
                    using StringWriter writer = new();
                    formatter.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, render),
                        provider.scopes, writer);
                    output.AddRange(writer.ToString()
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(text => text.TrimEnd('\r')));
                }

                provider.Log.Record(line, output);
            }
        }
    }
}

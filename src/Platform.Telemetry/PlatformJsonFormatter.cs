using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Platform.Telemetry;

/// <summary>
/// One JSON object per line, with the field names in <c>contracts/telemetry.md</c>. The framework's
/// own JSON formatter writes <c>Timestamp</c>, <c>LogLevel</c> and <c>Message</c> and puts scopes and
/// state under nested objects, so it cannot produce the contract's flat names.
///
/// Every line the gateway writes passes through <see cref="Write"/>, so this is where the never-logged
/// list is held: lines below a <see cref="LogFloors"/> floor are dropped, a property whose name says
/// it is a credential has its value replaced, and a query string is cut out of any text.
///
/// Scopes are not written. The framework's request scope carries the request path and its own
/// trace identifier; the trace and span ids come from <see cref="Activity.Current"/>, which is the
/// same context the spans are exported with (F-11).
/// </summary>
internal sealed class PlatformJsonFormatter : ConsoleFormatter
{
    public const string FormatterName = "platform-json";

    // The fields this formatter writes itself. A state property with one of these names is left out
    // rather than allowed to contradict them.
    private static readonly HashSet<string> Reserved =
        new(["ts", "level", "msg", "category", "trace_id", "span_id", "cell", "exception"], StringComparer.Ordinal);

    private readonly string? cell;

    public PlatformJsonFormatter(IConfiguration configuration)
        : base(FormatterName)
    {
        // The cell name, Platform:Cell, is part of every log line (ADR-0012, contracts/telemetry.md).
        cell = configuration["Platform:Cell"];
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        if (LogFloors.Drops(logEntry.Category, logEntry.LogLevel))
        {
            return;
        }

        IReadOnlyList<KeyValuePair<string, object?>>? properties =
            logEntry.State as IReadOnlyList<KeyValuePair<string, object?>>;
        string message = Redaction.Render(properties) ?? logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message.Length == 0 && logEntry.Exception is null)
        {
            return;
        }

        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteString("ts", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("level", LevelName(logEntry.LogLevel));
            json.WriteString("msg", Redaction.WithoutQueryStrings(message));
            json.WriteString("category", logEntry.Category);

            Activity? activity = Activity.Current;
            if (activity is not null)
            {
                json.WriteString("trace_id", activity.TraceId.ToHexString());
                json.WriteString("span_id", activity.SpanId.ToHexString());
            }

            if (cell is not null)
            {
                json.WriteString("cell", cell);
            }

            if (properties is not null)
            {
                foreach (KeyValuePair<string, object?> property in properties)
                {
                    WriteProperty(json, property.Key, property.Value);
                }
            }

            if (logEntry.Exception is not null)
            {
                // N-16 / contracts/telemetry.md: arbitrary exception messages, inner exceptions and
                // stack text may contain credentials. Keep only metadata whose value is not supplied
                // by the exception's caller; redacting structured properties cannot protect this text.
                json.WriteString("exception", logEntry.Exception.GetType().FullName);
            }

            json.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        textWriter.Write('\n');
    }

    private static void WriteProperty(Utf8JsonWriter json, string key, object? value)
    {
        // The message template itself: "{OriginalFormat}" in the framework's state.
        if (key.StartsWith('{'))
        {
            return;
        }

        string name = SnakeCase(key);
        if (Reserved.Contains(name))
        {
            return;
        }

        if (Redaction.IsSensitiveName(key))
        {
            json.WriteString(name, Redaction.Redacted);
            return;
        }

        switch (value)
        {
            case null:
                json.WriteNull(name);
                break;
            case bool b:
                json.WriteBoolean(name, b);
                break;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                json.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case double or float or decimal:
                json.WriteNumber(name, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteString(name, Redaction.WithoutQueryStrings(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
                break;
        }
    }

    /// <summary><c>AppId</c> and <c>appId</c> become <c>app_id</c>; <c>duration_ms</c> stays.</summary>
    internal static string SnakeCase(string key)
    {
        StringBuilder name = new(key.Length + 4);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && key[i - 1] != '_' && !char.IsUpper(key[i - 1]))
                {
                    name.Append('_');
                }

                name.Append(char.ToLowerInvariant(c));
            }
            else
            {
                name.Append(c is '-' or '.' or ' ' ? '_' : c);
            }
        }

        return name.ToString();
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "information",
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "none",
    };
}

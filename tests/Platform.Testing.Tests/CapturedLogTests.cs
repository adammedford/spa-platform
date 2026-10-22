using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The captured log (#140, #145): every line a host logged, as records and, when a console formatter
/// is configured, as the lines stdout would carry. Each test names the mutation that turns it red.
/// </summary>
public sealed class CapturedLogTests
{
    [Fact]
    public async Task LinesFor_reads_the_Rule_property_whatever_the_template_says()
    {
        // Mutation: match the rendered "rule=" text instead of the Rule state property. Hosting's
        // template says "refused under {Rule}", the rest "rule={Rule}", so a text search misses
        // hosting's line (shared decision S1 leaves the wording each stage's own).
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(context =>
            {
                ILogger logger = context.RequestServices.GetRequiredService<ILogger<CapturedLogTests>>();
                logger.LogWarning("refused under {Rule}", "host.unknown");
                logger.LogDebug("still refused under {Rule}", "host.unknown");
                logger.LogWarning("refused under {Rule}", "gate.origin");
                return Task.CompletedTask;
            }),
        }, Ct);

        (await host.Client.GetAsync("/", Ct)).Dispose();

        Assert.Equal(2, host.Log.LinesFor("host.unknown").Count);
        CapturedLine warning = Assert.Single(host.Log.LinesFor("host.unknown", LogLevel.Warning));
        Assert.Equal("refused under host.unknown", warning.Message);
    }

    [Fact]
    public async Task Lines_render_through_the_formatter_configured_by_name()
    {
        // Mutation: take the first registered formatter rather than the one ConsoleLoggerOptions
        // names. The platform's JSON formatter is internal to Platform.Telemetry, so the name is the
        // only lookup this project can make; two registered formatters make the shortcut visible.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Services = builder => builder.Logging
                .AddConsole(options => options.FormatterName = SecondFormatter.FormatterName)
                .AddConsoleFormatter<FirstFormatter, ConsoleFormatterOptions>()
                .AddConsoleFormatter<SecondFormatter, ConsoleFormatterOptions>(),
            Pipeline = app => app.Run(context =>
            {
                context.RequestServices.GetRequiredService<ILogger<CapturedLogTests>>().LogInformation("rendered-canary");
                return Task.CompletedTask;
            }),
        }, Ct);

        (await host.Client.GetAsync("/", Ct)).Dispose();

        Assert.Contains("second:rendered-canary", host.Log.Rendered);
        Assert.DoesNotContain(host.Log.Rendered, line => line.StartsWith("first:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_formatter_lines_are_records_and_Rendered_is_empty()
    {
        // Mutation: render with a formatter of the capture's own choosing when none is configured.
        // The plain record is the telemetry host's control, the N-16 case proving a canary reaches a
        // log with no telemetry, and an invented rendering would claim bytes no stdout carries.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(context =>
            {
                context.RequestServices.GetRequiredService<ILogger<CapturedLogTests>>().LogInformation("record-canary");
                return Task.CompletedTask;
            }),
        }, Ct);

        (await host.Client.GetAsync("/", Ct)).Dispose();

        Assert.True(host.Log.Contains("record-canary"));
        Assert.Empty(host.Log.Rendered);
        Assert.Empty(host.Log.Json);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FirstFormatter() : ConsoleFormatter("first")
    {
        public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter) =>
            textWriter.WriteLine("first:" + logEntry.Formatter(logEntry.State, logEntry.Exception));
    }

    private sealed class SecondFormatter() : ConsoleFormatter(FormatterName)
    {
        public const string FormatterName = "second";

        public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter) =>
            textWriter.WriteLine("second:" + logEntry.Formatter(logEntry.State, logEntry.Exception));
    }
}

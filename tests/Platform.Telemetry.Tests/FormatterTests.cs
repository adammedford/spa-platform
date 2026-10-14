using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Telemetry.Tests;

/// <summary>
/// The formatter's redaction on its own: the line the console logger writes for a given entry.
/// The end-to-end search is <see cref="LogCanaryTests"/>; these name each rule it relies on.
/// </summary>
public sealed class FormatterTests
{
    private static readonly PlatformJsonFormatter Formatter =
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:Cell"] = "poc" }).Build());

    private static string Write(string category, LogLevel level, string template, params object?[] args)
    {
        // The framework's own structured state for a template, as ILogger.Log builds it.
        ILogger capture = new CaptureLogger(category, level);
        capture.Log(level, template, args);
        return ((CaptureLogger)capture).Line;
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement;

    [Fact]
    public void A_query_string_is_cut_from_the_message_and_from_values()
    {
        string line = Write("Any", LogLevel.Information, "Called {Url} from /signin-oidc?code=abc&state=def",
            "https://h.example/p?x=secret-value");

        Assert.DoesNotContain("abc", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", line, StringComparison.Ordinal);
        Assert.Equal("https://h.example/p?[redacted]", Parse(line).GetProperty("url").GetString());
    }

    [Fact]
    public void A_query_string_logged_on_its_own_is_cut()
    {
        // HttpRequest.QueryString renders with its leading '?', so a query logged as a value of its
        // own, or after a space in the template, has no URL in front of it.
        string line = Write("Any", LogLevel.Information, "Query: ?code=abc and {Q}", "?code=ghi&state=jkl");

        Assert.DoesNotContain("abc", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ghi", line, StringComparison.Ordinal);
        Assert.DoesNotContain("jkl", line, StringComparison.Ordinal);
        Assert.Equal("?[redacted]", Parse(line).GetProperty("q").GetString());
    }

    [Fact]
    public void A_question_mark_in_prose_is_left_alone()
    {
        string line = Write("Any", LogLevel.Information, "Is the snapshot live? {Answer}", "yes");

        Assert.Equal("Is the snapshot live? yes", Parse(line).GetProperty("msg").GetString());
    }

    [Theory]
    [InlineData("AccessToken")]
    [InlineData("refresh_token")]
    [InlineData("ClientSecret")]
    [InlineData("Cookie")]
    [InlineData("SetCookie")]
    [InlineData("Authorization")]
    [InlineData("SessionKey")]
    [InlineData("code")]
    [InlineData("State")]
    public void A_property_named_as_a_credential_is_redacted_in_its_field_and_in_the_message(string name)
    {
        string line = Write("Any", LogLevel.Information, "Saw {" + name + "} once", "value-7d3e");

        Assert.DoesNotContain("value-7d3e", line, StringComparison.Ordinal);
        Assert.Equal("Saw [redacted] once", Parse(line).GetProperty("msg").GetString());
    }

    [Fact]
    public void Property_names_become_the_contract_s_snake_case_and_numbers_stay_numbers()
    {
        string line = Write("Any", LogLevel.Warning, "Refused {Rule} for {AppId} at {RegistryRev}", "gate.origin", "app1", 42L);

        JsonElement json = Parse(line);
        Assert.Equal("gate.origin", json.GetProperty("rule").GetString());
        Assert.Equal("app1", json.GetProperty("app_id").GetString());
        Assert.Equal(42, json.GetProperty("registry_rev").GetInt64());
        Assert.Equal("warning", json.GetProperty("level").GetString());
        Assert.Equal("poc", json.GetProperty("cell").GetString());
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler", LogLevel.Debug)]
    [InlineData("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler", LogLevel.Trace)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information)]
    [InlineData("Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.Information)]
    public void A_line_below_its_category_s_floor_is_not_written(string category, LogLevel level)
    {
        Assert.Equal(string.Empty, Write(category, level, "dropped"));
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler", LogLevel.Information)]
    [InlineData("Microsoft.AspNetCore.AuthenticationOther", LogLevel.Debug)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning)]
    public void A_line_at_or_above_its_floor_or_outside_every_floor_is_written(string category, LogLevel level)
    {
        Assert.Equal("kept", Parse(Write(category, level, "kept")).GetProperty("msg").GetString());
    }

    [Fact]
    public void N16_exception_and_inner_exception_text_never_reaches_the_log()
    {
        const string token = "CANARY-EXCEPTION-TOKEN";
        const string secret = "CANARY-INNER-SECRET";
        CaptureLogger logger = new("Platform.Identity.Example", LogLevel.Error);
        logger.LogError(new InvalidOperationException("access_token=" + token,
            new ArgumentException("client_secret=" + secret)), "Refresh failed for {AccessToken}", token);

        string line = logger.Line;
        Assert.DoesNotContain(token, line, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
        Assert.Equal(typeof(InvalidOperationException).FullName, Parse(line).GetProperty("exception").GetString());
        Assert.Equal("Refresh failed for [redacted]", Parse(line).GetProperty("msg").GetString());
    }

    private sealed class CaptureLogger(string category, LogLevel enabled) : ILogger
    {
        public string Line { get; private set; } = string.Empty;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            using StringWriter writer = new();
            Formatter.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter),
                new LoggerExternalScopeProvider(), writer);
            Line = writer.ToString().TrimEnd('\n');
        }
    }
}

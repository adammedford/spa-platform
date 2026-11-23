using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Testing;

namespace Platform.Registry.Tests;

/// <summary>
/// The start-up check of ADR-0042, issue #1400: <c>AddPlatformRegistry</c> rebuilds the JSON form of
/// the configuration, typed by <c>contracts/platform-config.schema.json</c>, validates it, logs one Error
/// line per finding under <c>Platform.Registry.Configuration</c> naming a key and never a value, and
/// throws. The passing case is <c>contracts/platform-config.example.json</c> in its environment form;
/// each failing case is that with one thing broken.
/// </summary>
public sealed class PlatformConfigurationTests
{
    private const string Category = "Platform.Registry.Configuration";

    /// <summary>
    /// The example as the task environment carries it: a colon for each double underscore, an array's
    /// items as numbered keys, and every value a string, as <c>tools/smoke.sh</c> turns it into
    /// environment variables.
    /// </summary>
    internal static Dictionary<string, string?> EnvironmentForm(Action<Dictionary<string, string?>>? change = null)
    {
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase);
        Flatten(Examples.Json("platform-config.example.json"), "", settings);
        change?.Invoke(settings);
        return settings;
    }

    private static void Flatten(JsonNode? node, string key, Dictionary<string, string?> settings)
    {
        switch (node)
        {
            case JsonObject value:
                foreach ((string name, JsonNode? item) in value)
                {
                    Flatten(item, key.Length == 0 ? name : key + ":" + name, settings);
                }

                break;
            case JsonArray value:
                for (int i = 0; i < value.Count; i++)
                {
                    Flatten(value[i], key + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), settings);
                }

                break;
            default:
                settings[key] = node?.ToString();
                break;
        }
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static (HostApplicationBuilder Builder, CapturedLog Log) Builder(Dictionary<string, string?> settings)
    {
        CapturedLog log = new();
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.AddProvider(log.Provider);
        return (builder, log);
    }

    /// <summary>Registers over <paramref name="settings"/>, which must be refused, and returns the Error lines' keys.</summary>
    private static (List<string> Keys, CapturedLog Log, InvalidOperationException Refusal) Refused(Dictionary<string, string?> settings)
    {
        (HostApplicationBuilder builder, CapturedLog log) = Builder(settings);
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => builder.AddPlatformRegistry());
        List<string> keys = [.. log.Lines
            .Where(line => line.Category == Category && line.Level == LogLevel.Error)
            .Select(line => line.State.Single(pair => pair.Key == "Key").Value?.ToString() ?? "")];
        return (keys, log, refusal);
    }

    [Fact]
    public void The_example_in_its_environment_form_validates_with_no_finding()
    {
        Assert.Empty(PlatformConfiguration.Findings(Configuration(EnvironmentForm())));
    }

    [Fact]
    public void The_rebuilt_document_is_the_example_itself_typed_by_the_schema()
    {
        // Every value went in as a string: PollSeconds and MaxSessions come back as integers and the
        // numbered keys as arrays only because the schema says so.
        JsonObject rebuilt = PlatformConfiguration.Document(Configuration(EnvironmentForm()));

        Assert.True(JsonNode.DeepEquals(Examples.Json("platform-config.example.json"), rebuilt), rebuilt.ToJsonString());
    }

    [Fact]
    public void Keys_outside_the_platform_sections_are_not_part_of_the_document()
    {
        JsonObject rebuilt = PlatformConfiguration.Document(Configuration(EnvironmentForm(settings =>
        {
            settings["Logging:LogLevel:Default"] = "Information";
            settings["ASPNETCORE_HTTP_PORTS"] = "8080";
        })));

        Assert.Equal(
            ["Assets", "ForwardedHeaders", "Issuers", "Platform", "Policy", "Registry", "Session"],
            rebuilt.Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_key_spelled_in_another_case_is_read_under_the_schemas_spelling()
    {
        // Configuration keys are case-insensitive; the schema's are not.
        Dictionary<string, string?> settings = EnvironmentForm(s => s.Remove("Session:MaxSessions"));
        settings["SESSION:maxsessions"] = "10000";

        Assert.Empty(PlatformConfiguration.Findings(Configuration(settings)));
    }

    [Fact]
    public void A_valid_configuration_registers()
    {
        (HostApplicationBuilder builder, CapturedLog log) = Builder(EnvironmentForm());

        builder.AddPlatformRegistry();

        Assert.DoesNotContain(log.Lines, line => line.Category == Category);
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(Platform.Abstractions.IRegistry));
    }

    public static TheoryData<string, string?, string> Refusals => new()
    {
        // Each: a key, the value it is given (null removes it), and the key the one Error line names.
        { "Session:MaxSessions", "0", "Session:MaxSessions" },
        { "Platform:Cell", null, "Platform:Cell" },
        { "ForwardedHeaders:TrustedNetworks:0", null, "ForwardedHeaders:TrustedNetworks" },
        { "Issuers:cognito-poc:Authority", null, "Issuers:cognito-poc:Authority" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_refused_configuration_stops_the_registration_with_one_error_line_naming_the_key(string key, string? value, string named)
    {
        (List<string> keys, _, InvalidOperationException refusal) = Refused(EnvironmentForm(settings =>
        {
            settings.Remove(key);
            if (value is not null)
            {
                settings[key] = value;
            }
        }));

        Assert.Equal([named], keys);
        Assert.Contains(named, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_trusted_networks_in_the_environment_form_names_the_key()
    {
        // ForwardedHeaders__TrustedNetworks= is how an environment writes an empty array.
        (List<string> keys, CapturedLog log, _) = Refused(EnvironmentForm(settings =>
        {
            settings.Remove("ForwardedHeaders:TrustedNetworks:0");
            settings["ForwardedHeaders:TrustedNetworks"] = "";
        }));

        Assert.Equal(["ForwardedHeaders:TrustedNetworks"], keys);
        Assert.Equal("minItems", log.Lines.Single(line => line.Category == Category).State.Single(pair => pair.Key == "Keyword").Value);
    }

    [Fact]
    public void An_empty_trusted_networks_in_a_json_file_names_the_key()
    {
        // The JSON provider stores an empty array as its key with an empty value, as the environment
        // form writes one, so both are the empty array the schema's minItems refuses.
        JsonObject document = Examples.Json("platform-config.example.json");
        document["ForwardedHeaders"]!["TrustedNetworks"] = new JsonArray();
        CapturedLog log = new();
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
        builder.Logging.AddProvider(log.Provider);

        Assert.Throws<InvalidOperationException>(() => builder.AddPlatformRegistry());

        CapturedLine line = Assert.Single(log.Lines, l => l.Category == Category && l.Level == LogLevel.Error);
        Assert.Equal("ForwardedHeaders:TrustedNetworks", line.State.Single(pair => pair.Key == "Key").Value);
        Assert.Equal("minItems", line.State.Single(pair => pair.Key == "Keyword").Value);
    }

    [Fact]
    public void A_missing_section_is_named_by_the_keys_it_requires()
    {
        (List<string> keys, _, _) = Refused(EnvironmentForm(settings =>
        {
            settings.Remove("Session:Store");
            settings.Remove("Session:MaxSessions");
        }));

        Assert.Equal(["Session:Store", "Session:MaxSessions"], keys);
    }

    [Fact]
    public void A_dynamodb_store_with_no_table_names_the_table()
    {
        (List<string> keys, _, _) = Refused(EnvironmentForm(settings => settings["Session:Store"] = "dynamodb"));

        Assert.Equal(["Session:TableName"], keys);
    }

    [Fact]
    public void An_integer_that_does_not_parse_is_refused_as_the_wrong_type()
    {
        (List<string> keys, CapturedLog log, _) = Refused(EnvironmentForm(settings => settings["Registry:PollSeconds"] = "fifteen"));

        Assert.Equal(["Registry:PollSeconds"], keys);
        Assert.Equal("type", log.Lines.Single(line => line.Category == Category).State.Single(pair => pair.Key == "Keyword").Value);
    }

    [Fact]
    public void A_key_a_closed_section_does_not_name_is_refused()
    {
        (List<string> keys, CapturedLog log, _) = Refused(EnvironmentForm(settings => settings["Session:MaxSesions"] = "10"));

        Assert.Equal(["Session:MaxSesions"], keys);
        Assert.Equal("additionalProperties", log.Lines.Single(line => line.Category == Category).State.Single(pair => pair.Key == "Keyword").Value);
    }

    [Fact]
    public void An_array_whose_keys_are_not_indices_is_refused()
    {
        (List<string> keys, _, _) = Refused(EnvironmentForm(settings =>
        {
            settings.Remove("Policy:AllowedHostSuffixes:0");
            settings["Policy:AllowedHostSuffixes:first"] = ".spa-poc.test:8443";
        }));

        Assert.Equal(["Policy:AllowedHostSuffixes"], keys);
    }

    [Fact]
    public void With_no_logger_registered_the_registration_still_throws()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(EnvironmentForm(settings => settings["Session:MaxSessions"] = "0"));

        Assert.Throws<InvalidOperationException>(() => builder.AddPlatformRegistry());
    }

    [Fact]
    public void No_error_line_and_no_refusal_carries_a_configured_value()
    {
        // Each refused value carries the canary, under a different keyword: a pattern, an enum, a type,
        // the issuer's off-machine Authority, and a minimum, whose engine message quotes the number it
        // refused ("-140098761 should be at least 1"), so logging the engine's text fails this test.
        const string Canary = "140098761";
        (List<string> keys, CapturedLog log, InvalidOperationException refusal) = Refused(EnvironmentForm(settings =>
        {
            settings["Platform:Cell"] = "Bad_" + Canary;
            settings["Session:Store"] = "s" + Canary;
            settings["Registry:PollSeconds"] = "x" + Canary;
            settings["Issuers:cognito-poc:Authority"] = $"http://c{Canary}.example.com";
            settings["Session:MaxSessions"] = "-" + Canary;
        }));

        Assert.Equal(
            ["Issuers:cognito-poc:Authority", "Platform:Cell", "Registry:PollSeconds", "Session:MaxSessions", "Session:Store"],
            keys.Order(StringComparer.Ordinal));
        Assert.False(log.Contains(Canary), string.Join("\n", log.Lines));
        Assert.DoesNotContain(log.Lines, line => line.State.Any(pair => pair.Value?.ToString()?.Contains(Canary, StringComparison.Ordinal) == true));
        Assert.DoesNotContain(Canary, refusal.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, bool> Origins => new()
    {
        { "http://127.0.0.1:5590", true },
        { "http://[::1]:5590", true },
        { "http://idp.example.com:5590", false },
        { "http://10.0.0.5:5590", false },
    };

    [Theory]
    [MemberData(nameof(Origins))]
    public void A_plain_http_issuer_validates_only_on_the_loopback_address(string origin, bool valid)
    {
        IConfiguration configuration = Configuration(EnvironmentForm(settings =>
        {
            settings["Issuers:cognito-poc:Authority"] = origin;
            settings["Issuers:cognito-poc:BrowserOrigin"] = origin;
        }));

        IEnumerable<string> findings = PlatformConfiguration.Findings(configuration).Select(finding => finding.ToString());

        // The schema's pattern is what refuses an off-machine plain http origin (ADR-0042 decision 3).
        Assert.Equal(
            valid ? [] : ["Issuers:cognito-poc:Authority (pattern)", "Issuers:cognito-poc:BrowserOrigin (pattern)"],
            findings.Order(StringComparer.Ordinal));
    }
}

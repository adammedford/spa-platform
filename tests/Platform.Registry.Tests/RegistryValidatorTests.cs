using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>
/// The validator of plan row 2.1, through its one public entry point. Each rule has a passing case
/// (the contract's example, or the example with a neighbouring value) and a failing case (the example
/// with that one rule broken).
/// </summary>
public sealed class RegistryValidatorTests
{
    private static RegistryValidation Validate(JsonNode document, PlatformPolicy? policy = null, long? liveRevision = null) =>
        RegistryValidator.Validate(document.ToJsonString(), policy ?? Examples.Policy(), liveRevision);

    private static void Accepts(JsonNode document, string because, PlatformPolicy? policy = null, long? liveRevision = null)
    {
        RegistryValidation result = Validate(document, policy, liveRevision);
        Assert.True(result.IsValid, $"should accept {because}, but reported:\n{string.Join('\n', result.Errors)}");
        Assert.NotEmpty(result.Apps);
    }

    private static RegistryValidation Rejects(JsonNode document, string because, PlatformPolicy? policy = null, long? liveRevision = null)
    {
        RegistryValidation result = Validate(document, policy, liveRevision);
        Assert.False(result.IsValid, $"should reject {because}, but accepted it.");
        // Rejected whole (ADR-0002): no entry of a rejected document reaches a snapshot.
        Assert.Empty(result.Apps);
        return result;
    }

    [Fact]
    public void The_contract_example_is_accepted_and_modelled()
    {
        RegistryValidation result = Validate(Examples.Registry());

        Assert.True(result.IsValid, string.Join('\n', result.Errors));
        Assert.Equal(42, result.Revision);
        AppDefinition app = Assert.Single(result.Apps);
        Assert.Equal("app1", app.AppId);
        Assert.Equal("app1.spa-poc.test:8443", app.Host);
        Assert.Equal("2026-10-10.1", app.Release);
        Assert.Equal("cognito-poc", app.OAuth.Issuer);
        Assert.Equal("1example23456789", app.OAuth.ClientId);
        Assert.Equal("ssm:/spa-platform/poc/apps/app1/client-secret:1", app.OAuth.ClientSecretRef);
        Assert.Equal(["openid", "profile", "https://orders.spa-poc.test/read"], app.OAuth.Scopes);
        Assert.Equal("https://orders.spa-poc.test", app.OAuth.Resource);
        ApiDefinition api = Assert.Single(app.Apis);
        Assert.Equal("orders", api.Name);
        Assert.Equal(new Uri("http://127.0.0.1:5080"), api.Upstream);
        Assert.Equal(TimeSpan.FromSeconds(30), api.Timeout);
        Assert.Equal(["data:"], app.Csp["img-src"]);
        Assert.Equal(TimeSpan.FromMinutes(480), app.SessionLifetime);
        Assert.StartsWith("sha256:", app.Fingerprint);
    }

    private static void RejectsFor(string rule, string path, JsonNode document, string because, PlatformPolicy? policy = null, long? liveRevision = null)
    {
        RegistryValidation result = Rejects(document, because, policy, liveRevision);
        Assert.Contains(result.Errors, error => error.Rule == rule && error.Path == path);
    }

    // ---- The schema, through the validator. Contracts.Tests covers every schema rule against the
    // schema itself; these prove the validator runs that schema, and runs it before anything else.

    [Fact]
    public void A_document_that_is_not_json_is_rejected()
    {
        RegistryValidation result = RegistryValidator.Validate("{\"schemaVersion\": 1,", Examples.Policy(), null);
        Assert.False(result.IsValid);
        Assert.Equal(RegistryRules.NotJson, Assert.Single(result.Errors).Rule);
    }

    [Fact]
    public void A_property_named_twice_is_rejected_rather_than_resolved()
    {
        // Two hosts in one entry: the schema engine and the model could each read a different one.
        string text = Examples.Registry().ToJsonString()
            .Replace("\"host\":\"app1.spa-poc.test:8443\"", "\"host\":\"app1.spa-poc.test:8443\",\"host\":\"evil.example:8443\"", StringComparison.Ordinal);
        Assert.Contains("evil.example", text, StringComparison.Ordinal);

        RegistryValidation result = RegistryValidator.Validate(text, Examples.Policy(), null);

        Assert.False(result.IsValid);
        Assert.Equal(RegistryRules.NotJson, Assert.Single(result.Errors).Rule);
    }

    [Fact]
    public void The_schema_is_checked()
    {
        RejectsFor(RegistryRules.Schema, "/schemaVersion", Examples.Registry(d => d["schemaVersion"] = 2), "schemaVersion 2");
        RejectsFor(RegistryRules.Schema, "/apps/0/hosts", Examples.RegistryEntry(e => e["hosts"] = "typo"), "an unknown property");
        RejectsFor(RegistryRules.Schema, "/apps/0/session/lifetimeMinutes",
            Examples.RegistryEntry(e => e["session"]!["lifetimeMinutes"] = 721), "a session longer than 720 minutes");
        RejectsFor(RegistryRules.Schema, "/apps/0/oauth/scopes",
            Examples.RegistryEntry(e => e["oauth"]!["scopes"] = new JsonArray("profile")), "scopes without openid");
    }

    [Fact]
    public void N14_a_forbidden_csp_source_is_rejected_whole()
    {
        Accepts(Examples.RegistryEntry(e => e["csp"] = new JsonObject { ["img-src"] = new JsonArray("data:") }), "data: in img-src");
        Accepts(Examples.RegistryEntry(e => e["csp"] = new JsonObject { ["script-src"] = new JsonArray("https://cdn.spa-poc.test") }),
            "an https origin in script-src");

        RejectsFor(RegistryRules.Schema, "/apps/0/csp/img-src/0",
            Examples.RegistryEntry(e => e["csp"] = new JsonObject { ["img-src"] = new JsonArray("*") }), "'*' as a source");
        RejectsFor(RegistryRules.Schema, "/apps/0/csp/connect-src/0",
            Examples.RegistryEntry(e => e["csp"] = new JsonObject { ["connect-src"] = new JsonArray("'unsafe-eval'") }), "'unsafe-eval'");
        RejectsFor(RegistryRules.Schema, "/apps/0/csp/script-src/0",
            Examples.RegistryEntry(e => e["csp"] = new JsonObject { ["script-src"] = new JsonArray("'unsafe-inline'") }), "'unsafe-inline' in script-src");
        RejectsFor(RegistryRules.Schema, "/apps/0/csp/script-src/0",
            Examples.RegistryEntry(e => e["csp"] = new JsonObject { ["script-src"] = new JsonArray("data:") }), "data: in script-src");
    }

    // ---- contracts/README.md, "Rules that JSON Schema cannot express", one row at a time.

    [Fact]
    public void Revision_must_be_greater_than_the_live_one()
    {
        Accepts(Examples.Registry(), "revision 42 over live 41", liveRevision: 41);
        Accepts(Examples.Registry(), "any revision at a cold start", liveRevision: null);

        RejectsFor(RegistryRules.RevisionNotGreater, "/revision", Examples.Registry(), "revision 42 over live 42", liveRevision: 42);
        RejectsFor(RegistryRules.RevisionNotGreater, "/revision", Examples.Registry(), "revision 42 over live 43", liveRevision: 43);
    }

    [Fact]
    public void AppId_is_unique_in_the_registry()
    {
        Accepts(Examples.RegistryWithSecondEntry(), "two entries with different appIds");
        RejectsFor(RegistryRules.DuplicateAppId, "/apps/1/appId",
            Examples.RegistryWithSecondEntry(e => e["appId"] = "app1"), "a second entry with appId app1");
    }

    [Fact]
    public void S_D8_and_N14_a_host_claimed_twice_is_rejected_whole()
    {
        Accepts(Examples.RegistryWithSecondEntry(), "two entries with different hosts");

        RegistryValidation result = Rejects(
            Examples.RegistryWithSecondEntry(e => e["host"] = "app1.spa-poc.test:8443"), "a second entry claiming app1's host");

        RegistryIssue error = Assert.Single(result.Errors);
        Assert.Equal(RegistryRules.DuplicateHost, error.Rule);
        Assert.Equal("/apps/1/host", error.Path);
        Assert.Contains("/apps/0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientId_is_unique_across_apps()
    {
        Accepts(Examples.RegistryWithSecondEntry(), "two entries with different client ids");
        RejectsFor(RegistryRules.DuplicateClientId, "/apps/1/oauth/clientId",
            Examples.RegistryWithSecondEntry(e => e["oauth"]!["clientId"] = "1example23456789"), "a second entry with app1's client id");
    }

    [Fact]
    public void Api_name_is_unique_within_the_app()
    {
        Accepts(Examples.RegistryEntry(e => e["apis"]!.AsArray().Add(Api("reports", "http://127.0.0.1:5080"))), "two apis with different names");
        Accepts(Examples.RegistryWithSecondEntry(), "the same api name in two different apps");

        RejectsFor(RegistryRules.DuplicateApiName, "/apps/0/apis/1/name",
            Examples.RegistryEntry(e => e["apis"]!.AsArray().Add(Api("orders", "http://127.0.0.1:5081"))), "two apis named orders in one app");
    }

    [Fact]
    public void Host_ends_with_an_allowed_suffix()
    {
        Accepts(Examples.RegistryEntry(e => e["host"] = "another-app.spa-poc.test:8443"), "a host under .spa-poc.test:8443");

        RejectsFor(RegistryRules.HostSuffixNotAllowed, "/apps/0/host",
            Examples.RegistryEntry(e => e["host"] = "app1.example.test:8443"), "a host under another domain");
        RejectsFor(RegistryRules.HostSuffixNotAllowed, "/apps/0/host",
            Examples.RegistryEntry(e => e["host"] = "app1.spa-poc.test"), "a host without the port the suffix carries");
        RejectsFor(RegistryRules.HostSuffixNotAllowed, "/apps/0/host",
            Examples.RegistryEntry(e => e["host"] = "app1.spa-poc.test:9443"), "a host on another port");
    }

    [Fact]
    public void A_suffix_without_a_leading_dot_still_meets_the_host_at_a_label()
    {
        PlatformPolicy policy = Examples.Policy(p => p with { AllowedHostSuffixes = ["spa-poc.test:8443"] });

        Accepts(Examples.RegistryEntry(e => e["host"] = "app1.spa-poc.test:8443"), "a host one label under the suffix", policy);
        RejectsFor(RegistryRules.HostSuffixNotAllowed, "/apps/0/host",
            Examples.RegistryEntry(e => e["host"] = "evilspa-poc.test:8443"), "a host that only shares the suffix's characters", policy);
        RejectsFor(RegistryRules.HostSuffixNotAllowed, "/apps/0/host",
            Examples.RegistryEntry(e => e["host"] = "spa-poc.test:8443"), "the suffix itself", policy);
    }

    [Fact]
    public void N14_an_upstream_outside_upstream_policy_is_rejected_whole()
    {
        Accepts(Examples.RegistryEntry(e => e["apis"]![0]!["upstream"] = "http://127.0.0.1:5081"), "the second allowed upstream");

        RejectsFor(RegistryRules.UpstreamNotAllowed, "/apps/0/apis/0/upstream",
            Examples.RegistryEntry(e => e["apis"]![0]!["upstream"] = "http://127.0.0.1:5082"), "an upstream on a port policy does not list");
        RejectsFor(RegistryRules.UpstreamNotAllowed, "/apps/0/apis/0/upstream",
            Examples.RegistryEntry(e => e["apis"]![0]!["upstream"] = "https://127.0.0.1:5080"), "an allowed upstream under another scheme");
        RejectsFor(RegistryRules.UpstreamNotAllowed, "/apps/0/apis/0/upstream",
            Examples.RegistryEntry(e => e["apis"]![0]!["upstream"] = "http://169.254.169.254"), "the instance metadata endpoint");
    }

    [Fact]
    public void A_default_port_matches_the_same_origin_without_it()
    {
        PlatformPolicy policy = Examples.Policy(p => p with { AllowedUpstreams = ["https://orders.internal"] });

        Accepts(Examples.RegistryEntry(e => e["apis"]![0]!["upstream"] = "https://orders.internal:443"), "the default https port spelled out", policy);
        RejectsFor(RegistryRules.UpstreamNotAllowed, "/apps/0/apis/0/upstream",
            Examples.RegistryEntry(e => e["apis"]![0]!["upstream"] = "https://orders.internal:8443"), "another port", policy);
    }

    [Fact]
    public void Issuer_names_an_issuer_in_platform_configuration()
    {
        Accepts(Examples.Registry(), "cognito-poc, which the example configuration names");
        RejectsFor(RegistryRules.UnknownIssuer, "/apps/0/oauth/issuer",
            Examples.RegistryEntry(e => e["oauth"]!["issuer"] = "primary"), "an issuer configuration does not name");
    }

    [Fact]
    public void A_port_above_65535_is_rejected()
    {
        PlatformPolicy policy = Examples.Policy(p => p with
        {
            AllowedHostSuffixes = [".spa-poc.test:65535", ".spa-poc.test:65536"],
            AllowedUpstreams = ["http://127.0.0.1:5080", "http://127.0.0.1:65535", "http://127.0.0.1:65536"],
        });

        Accepts(Examples.RegistryEntry(e => e["host"] = "app1.spa-poc.test:65535"), "host port 65535", policy);
        Accepts(Examples.RegistryEntry(e =>
        {
            e["host"] = "app1.spa-poc.test:65535";
            e["apis"]![0]!["upstream"] = "http://127.0.0.1:65535";
        }), "upstream port 65535", policy);

        RejectsFor(RegistryRules.PortOutOfRange, "/apps/0/host",
            Examples.RegistryEntry(e => e["host"] = "app1.spa-poc.test:65536"), "host port 65536", policy);
        RejectsFor(RegistryRules.PortOutOfRange, "/apps/0/apis/0/upstream", Examples.RegistryEntry(e =>
        {
            e["host"] = "app1.spa-poc.test:65535";
            e["apis"]![0]!["upstream"] = "http://127.0.0.1:65536";
        }), "upstream port 65536", policy);
        RejectsFor(RegistryRules.PortOutOfRange, "/apps/0/apis/0/upstream",
            Examples.RegistryEntry(e => e["apis"]![0]!["upstream"] = "http://127.0.0.1:99999999999"), "an upstream port too long to parse");
    }

    // ---- Integers the schema accepts and the model must hold. Before the review these threw out of
    // Validate instead of returning a result.

    [Theory]
    [InlineData("revision", "42.0")]
    [InlineData("timeoutSeconds", "3e1")]
    [InlineData("lifetimeMinutes", "480.0")]
    public void An_integer_spelled_with_a_fraction_or_exponent_is_accepted(string property, string spelling)
    {
        RegistryValidation result = RegistryValidator.Validate(Respell(property, spelling), Examples.Policy(), null);

        Assert.True(result.IsValid, string.Join('\n', result.Errors));
        Assert.Equal(42, result.Revision);
        AppDefinition app = Assert.Single(result.Apps);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(app.Apis).Timeout);
        Assert.Equal(TimeSpan.FromMinutes(480), app.SessionLifetime);
    }

    [Theory]
    [InlineData("revision", "9223372036854775808", "/revision")]
    [InlineData("revision", "1e30", "/revision")]
    public void An_integer_the_model_cannot_hold_is_an_error_not_an_exception(string property, string spelling, string path)
    {
        RegistryValidation result = RegistryValidator.Validate(Respell(property, spelling), Examples.Policy(), null);

        Assert.False(result.IsValid);
        Assert.Empty(result.Apps);
        RegistryIssue error = Assert.Single(result.Errors);
        Assert.Equal((RegistryRules.IntegerOutOfRange, path), (error.Rule, error.Path));
    }

    [Fact]
    public void The_G2_schema_accepts_the_proxy_ceiling_and_rejects_larger_timeouts()
    {
        RegistryValidation atCeiling = RegistryValidator.Validate(Respell("timeoutSeconds", "2147483"), Examples.Policy(), null);
        Assert.True(atCeiling.IsValid, string.Join('\n', atCeiling.Errors));
        Assert.Equal(TimeSpan.FromSeconds(2147483), Assert.Single(Assert.Single(atCeiling.Apps).Apis).Timeout);

        // Schema validation runs before model conversion, so it rejects even integers a TimeSpan
        // could hold. ADR-0019 makes applicability to the proxy the contract's bound.
        foreach (string spelling in new[] { "2147484", "922337203685", "922337203686" })
        {
            RegistryValidation above = RegistryValidator.Validate(Respell("timeoutSeconds", spelling), Examples.Policy(), null);
            Assert.False(above.IsValid);
            Assert.Empty(above.Apps);
            RegistryIssue error = Assert.Single(above.Errors);
            Assert.Equal((RegistryRules.Schema, "/apps/0/apis/0/timeoutSeconds"), (error.Rule, error.Path));
        }
    }

    /// <summary>The contract example's text with one integer's spelling changed and nothing else.</summary>
    private static string Respell(string property, string spelling)
    {
        string text = Examples.Text("registry.example.json");
        JsonObject example = Examples.Registry();
        long value = property switch
        {
            "revision" => example["revision"]!.GetValue<long>(),
            "timeoutSeconds" => Examples.Entry(example, 0)["apis"]![0]!["timeoutSeconds"]!.GetValue<long>(),
            _ => Examples.Entry(example, 0)["session"]!["lifetimeMinutes"]!.GetValue<long>(),
        };
        string from = $"\"{property}\": {value}";
        Assert.Contains(from, text, StringComparison.Ordinal);
        return text.Replace(from, $"\"{property}\": {spelling}", StringComparison.Ordinal);
    }

    // ---- Errors and warnings kept apart.

    [Fact]
    public void More_than_one_upstream_origin_is_a_warning_not_an_error()
    {
        RegistryValidation one = Validate(Examples.RegistryEntry(e => e["apis"]!.AsArray().Add(Api("reports", "http://127.0.0.1:5080"))));
        Assert.True(one.IsValid);
        Assert.Empty(one.Warnings);

        RegistryValidation two = Validate(Examples.RegistryEntry(e => e["apis"]!.AsArray().Add(Api("reports", "http://127.0.0.1:5081"))));
        Assert.True(two.IsValid, string.Join('\n', two.Errors));
        Assert.Single(two.Apps);
        RegistryIssue warning = Assert.Single(two.Warnings);
        Assert.Equal(RegistryRules.MultipleUpstreamOrigins, warning.Rule);
        Assert.Equal("/apps/0/apis", warning.Path);
    }

    [Fact]
    public void Warnings_survive_alongside_errors_and_stay_out_of_them()
    {
        RegistryValidation result = Validate(Examples.RegistryWithSecondEntry(e =>
        {
            e["host"] = "app1.spa-poc.test:8443";
            e["apis"]!.AsArray().Add(Api("reports", "http://127.0.0.1:5081"));
        }));

        Assert.All(result.Errors, error => Assert.NotEqual(RegistryRules.MultipleUpstreamOrigins, error.Rule));
        Assert.Equal(RegistryRules.DuplicateHost, Assert.Single(result.Errors).Rule);
        Assert.Equal(RegistryRules.MultipleUpstreamOrigins, Assert.Single(result.Warnings).Rule);
    }

    [Fact]
    public void Every_error_is_reported_not_only_the_first()
    {
        RegistryValidation result = Rejects(Examples.RegistryWithSecondEntry(e =>
        {
            e["host"] = "app1.spa-poc.test:8443";
            e["oauth"]!["issuer"] = "primary";
            e["apis"]![0]!["upstream"] = "http://127.0.0.1:5082";
        }), "three broken rules in one entry", liveRevision: 50);

        // A set: completeness is the contract, the order the rules run in is not.
        Assert.Equal(
            new[] { RegistryRules.RevisionNotGreater, RegistryRules.DuplicateHost, RegistryRules.UnknownIssuer, RegistryRules.UpstreamNotAllowed }
                .Order(StringComparer.Ordinal),
            result.Errors.Select(error => error.Rule).Order(StringComparer.Ordinal));
    }

    // ---- Documents the platform ships.

    [Fact]
    public void An_empty_apps_array_is_a_valid_document()
    {
        RegistryValidation result = Validate(Examples.Registry(d => d["apps"] = new JsonArray()));
        Assert.True(result.IsValid, string.Join('\n', result.Errors));
        Assert.Empty(result.Apps);
    }

    [Fact]
    public void An_entry_with_no_apis_and_no_csp_models_as_empty()
    {
        RegistryValidation result = Validate(Examples.RegistryEntry(e =>
        {
            e.Remove("apis");
            e.Remove("csp");
            e["oauth"]!.AsObject().Remove("resource");
        }));

        AppDefinition app = Assert.Single(result.Apps);
        Assert.Empty(app.Apis);
        Assert.Empty(app.Csp);
        Assert.Null(app.OAuth.Resource);
    }

    private static JsonObject Api(string name, string upstream) =>
        new() { ["name"] = name, ["upstream"] = upstream, ["timeoutSeconds"] = 30 };
}

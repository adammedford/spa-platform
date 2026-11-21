using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>
/// The registry validator's fragment rules (#992, part of the fragments spec #961): schemaVersion 1 and
/// 2, each fragment field rule, the cross-entry and per-entry rules the schema cannot express, the
/// fragment csp rule, the fragments-per-shell cap and the model. Each refusal has a passing neighbour,
/// so an assertion that a rule fires can fail when the rule stops firing.
///
/// <c>contracts/registry.example.json</c>'s app1 carries one fragment, <c>reports</c>: client id
/// <c>2example34567890</c>, issuer <c>cognito-poc</c>, one api <c>reports</c> on
/// <c>http://127.0.0.1:5081</c>, and the shell's one api <c>orders</c> on <c>http://127.0.0.1:5080</c>.
/// </summary>
public sealed class FragmentValidatorTests
{
    private const string At = "/apps/0/fragments/0";

    private static RegistryValidation Validate(JsonNode document, PlatformPolicy? policy = null) =>
        RegistryValidator.Validate(document.ToJsonString(), policy ?? Examples.Policy(), null);

    private static RegistryValidation Accepts(JsonNode document, string because, PlatformPolicy? policy = null)
    {
        RegistryValidation result = Validate(document, policy);
        Assert.True(result.IsValid, $"should accept {because}, but reported:\n{string.Join('\n', result.Errors)}");
        Assert.NotEmpty(result.Apps);
        return result;
    }

    private static RegistryValidation RejectsFor(string rule, string path, JsonNode document, string because, PlatformPolicy? policy = null)
    {
        RegistryValidation result = Validate(document, policy);
        Assert.False(result.IsValid, $"should reject {because}, but accepted it.");
        Assert.Empty(result.Apps);
        Assert.True(
            result.Errors.Any(error => error.Rule == rule && error.Path == path),
            $"should reject {because} under {rule} at {path}, but reported:\n{string.Join('\n', result.Errors)}");
        return result;
    }

    /// <summary>The example's one fragment, changed.</summary>
    private static JsonObject WithFragment(Action<JsonObject> change) =>
        Examples.RegistryEntry(entry => change(entry["fragments"]![0]!.AsObject()));

    /// <summary>The example's fragment and a copy of it under another id and client id, changed.</summary>
    private static JsonObject WithSecondFragment(Action<JsonObject>? change = null) =>
        Examples.RegistryEntry(entry =>
        {
            JsonArray fragments = entry["fragments"]!.AsArray();
            JsonObject second = fragments[0]!.DeepClone().AsObject();
            second["fragmentId"] = "audit";
            second["oauth"]!["clientId"] = "4example-audit";
            second["oauth"]!["clientSecretRef"] = "ssm:/spa-platform/poc/apps/app1/fragments/audit/client-secret:1";
            change?.Invoke(second);
            fragments.Add(second);
        });

    private static JsonObject Fragment(string fragmentId) => new()
    {
        ["fragmentId"] = fragmentId,
        ["oauth"] = new JsonObject
        {
            ["issuer"] = "cognito-poc",
            ["clientId"] = "client-" + fragmentId,
            ["clientSecretRef"] = $"ssm:/spa-platform/poc/apps/app1/fragments/{fragmentId}/client-secret:1",
            ["scopes"] = new JsonArray("openid"),
        },
        ["release"] = "2026-11-01.1",
    };

    private static JsonObject WithFragmentCount(int count) =>
        Examples.RegistryEntry(entry => entry["fragments"] = new JsonArray(
            [.. Enumerable.Range(1, count).Select(n => (JsonNode)Fragment($"frag{n}"))]));

    private static JsonObject Api(string name, string upstream) =>
        new() { ["name"] = name, ["upstream"] = upstream, ["timeoutSeconds"] = 30 };

    // ---- schemaVersion (ADR-0019 decisions 1 and 2).

    [Fact]
    public void A_version_1_document_with_no_fragments_validates_and_models_as_before()
    {
        RegistryValidation result = Accepts(Examples.Registry(d =>
        {
            d["schemaVersion"] = 1;
            Examples.Entry(d, 0).Remove("fragments");
        }), "schemaVersion 1 with no fragments");

        AppDefinition app = Assert.Single(result.Apps);
        Assert.Empty(app.Fragments);
        Assert.Equal("2026-10-10.1", app.Release);
    }

    [Fact]
    public void A_version_2_document_with_a_fragment_validates_and_models_it()
    {
        RegistryValidation result = Accepts(Examples.Registry(), "the contract example");

        FragmentDefinition fragment = Assert.Single(Assert.Single(result.Apps).Fragments);
        Assert.Equal("reports", fragment.FragmentId);
        Assert.Equal("2026-10-12.1", fragment.Release);
        Assert.Equal("entry.js", fragment.Entry);
        Assert.Equal("cognito-poc", fragment.OAuth.Issuer);
        Assert.Equal("2example34567890", fragment.OAuth.ClientId);
        Assert.Equal("ssm:/spa-platform/poc/apps/app1/fragments/reports/client-secret:1", fragment.OAuth.ClientSecretRef);
        Assert.Equal(["openid", "https://reports.spa-poc.test/read"], fragment.OAuth.Scopes);
        Assert.Equal("https://reports.spa-poc.test", fragment.OAuth.Resource);
        ApiDefinition api = Assert.Single(fragment.Apis);
        Assert.Equal(("reports", new Uri("http://127.0.0.1:5081"), TimeSpan.FromSeconds(20)), (api.Name, api.Upstream, api.Timeout));
        Assert.Equal(["https://images.spa-poc.test"], fragment.Csp["img-src"]);
    }

    [Fact]
    public void Fragments_model_in_registry_order_with_their_own_entry_and_no_resource_when_absent()
    {
        RegistryValidation result = Accepts(WithSecondFragment(second =>
        {
            second["entry"] = "remote.v2.js";
            second["oauth"]!.AsObject().Remove("resource");
            second.Remove("apis");
            second.Remove("csp");
        }), "two fragments");

        ImmutableArray<FragmentDefinition> fragments = Assert.Single(result.Apps).Fragments;
        Assert.Equal(["reports", "audit"], fragments.Select(fragment => fragment.FragmentId));
        Assert.Equal("remote.v2.js", fragments[1].Entry);
        Assert.Equal("4example-audit", fragments[1].OAuth.ClientId);
        Assert.Null(fragments[1].OAuth.Resource);
        Assert.Empty(fragments[1].Apis);
        Assert.Empty(fragments[1].Csp);
    }

    [Fact]
    public void A_version_1_document_carrying_a_fragment_is_refused() =>
        RejectsFor(RegistryRules.Schema, "/schemaVersion", Examples.Registry(d => d["schemaVersion"] = 1),
            "a fragment at schemaVersion 1");

    // ---- The fragment's own fields, which the schema holds.

    [Theory]
    [InlineData("abc")]
    [InlineData("a234567890123456789012345678901234567890")]
    [InlineData("orders-2")]
    public void A_fragmentId_of_the_appId_rule_is_accepted(string fragmentId) =>
        Accepts(WithFragment(f => f["fragmentId"] = fragmentId), $"fragmentId '{fragmentId}'");

    [Theory]
    [InlineData("ab")]
    [InlineData("a2345678901234567890123456789012345678901")]
    [InlineData("rep:orts")]
    [InlineData("Reports")]
    public void A_fragmentId_outside_the_appId_rule_is_refused(string fragmentId) =>
        RejectsFor(RegistryRules.Schema, $"{At}/fragmentId", WithFragment(f => f["fragmentId"] = fragmentId),
            $"fragmentId '{fragmentId}'");

    [Theory]
    [InlineData("entry.js")]
    [InlineData("remote-entry.v2.js")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901.js")]
    public void An_entry_at_the_release_root_is_accepted(string entry)
    {
        RegistryValidation result = Accepts(WithFragment(f => f["entry"] = entry), $"entry '{entry}'");
        Assert.Equal(entry, Assert.Single(Assert.Single(result.Apps).Fragments).Entry);
    }

    [Theory]
    [InlineData("assets/entry.js")]
    [InlineData("api/entry.js")]
    [InlineData("nested/dir/entry.js")]
    [InlineData("a2345678901234567890123456789012345678901234567890123456789012.js")]
    [InlineData("entry.mjs")]
    [InlineData("entry")]
    public void An_entry_off_the_release_root_or_not_a_js_file_is_refused(string entry)
    {
        Assert.True(entry.Length != 65 || entry.EndsWith(".js", StringComparison.Ordinal), "the long case is 65 characters");
        RejectsFor(RegistryRules.Schema, $"{At}/entry", WithFragment(f => f["entry"] = entry), $"entry '{entry}'");
    }

    [Fact]
    public void An_absent_entry_reads_as_entry_js()
    {
        RegistryValidation result = Accepts(WithFragment(f => f.Remove("entry")), "a fragment with no entry");
        Assert.Equal(PlatformPaths.DefaultEntry, Assert.Single(Assert.Single(result.Apps).Fragments).Entry);
        Assert.Equal("entry.js", PlatformPaths.DefaultEntry);
    }

    [Fact]
    public void A_fragment_naming_host_or_session_is_refused()
    {
        RejectsFor(RegistryRules.Schema, $"{At}/host", WithFragment(f => f["host"] = "reports.spa-poc.test:8443"),
            "a fragment with a host");
        RejectsFor(RegistryRules.Schema, $"{At}/session",
            WithFragment(f => f["session"] = new JsonObject { ["lifetimeMinutes"] = 60 }), "a fragment with a session");
    }

    // ---- clientId, fragmentId and issuer, which the validator holds.

    [Fact]
    public void A_fragment_clientId_equal_to_its_shells_is_a_duplicate_at_the_fragment() =>
        RejectsFor(RegistryRules.DuplicateClientId, $"{At}/oauth/clientId",
            WithFragment(f => f["oauth"]!["clientId"] = "1example23456789"), "a fragment with its shell's client id");

    [Fact]
    public void A_fragment_clientId_equal_to_another_fragments_is_a_duplicate_at_the_later_fragment()
    {
        Accepts(WithSecondFragment(), "two fragments with different client ids");
        RejectsFor(RegistryRules.DuplicateClientId, "/apps/0/fragments/1/oauth/clientId",
            WithSecondFragment(second => second["oauth"]!["clientId"] = "2example34567890"), "two fragments with one client id");
    }

    [Fact]
    public void A_fragment_clientId_equal_to_another_fragments_in_another_entry_is_a_duplicate()
    {
        Accepts(Examples.RegistryWithSecondEntry(), "two entries whose fragments have different client ids");
        RejectsFor(RegistryRules.DuplicateClientId, "/apps/1/fragments/0/oauth/clientId",
            Examples.RegistryWithSecondEntry(e => e["fragments"]![0]!["oauth"]!["clientId"] = "2example34567890"),
            "a second entry's fragment with app1's fragment's client id");
    }

    /// <summary>
    /// The path reaches the fragment whichever order the entries are in: a shell's client id is
    /// claimed before any fragment's, so a later shell does not take the path from an earlier fragment.
    /// </summary>
    [Fact]
    public void A_fragment_clientId_equal_to_another_shells_is_a_duplicate_at_the_fragment_in_either_order()
    {
        // app1's fragment carries app2's shell client id; app2 comes after app1.
        RegistryValidation later = RejectsFor(RegistryRules.DuplicateClientId, $"{At}/oauth/clientId",
            Examples.RegistryWithSecondEntry(e => e["oauth"]!["clientId"] = "2example34567890"),
            "a later shell with an earlier fragment's client id");
        Assert.Single(later.Errors);

        // app2's fragment carries app1's shell client id; app1 comes first.
        RegistryValidation earlier = RejectsFor(RegistryRules.DuplicateClientId, "/apps/1/fragments/0/oauth/clientId",
            Examples.RegistryWithSecondEntry(e => e["fragments"]![0]!["oauth"]!["clientId"] = "1example23456789"),
            "a later fragment with an earlier shell's client id");
        Assert.Single(earlier.Errors);
        Assert.Contains("/apps/0/oauth/clientId", earlier.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragmentId_repeated_within_one_entry_is_refused()
    {
        RejectsFor(RegistryRules.DuplicateFragmentId, "/apps/0/fragments/1/fragmentId",
            WithSecondFragment(second => second["fragmentId"] = "reports"), "two fragments named reports");

        // Unique within its shell only: another entry may reuse it.
        Accepts(Examples.RegistryWithSecondEntry(), "the same fragmentId in two entries");
    }

    [Fact]
    public void A_fragment_issuer_other_than_its_shells_is_refused()
    {
        PlatformPolicy twoIssuers = Examples.Policy(policy => policy with
        {
            Issuers = policy.Issuers.Add("second", new IssuerDefinition("https://idp.example", "cognito", new Uri("https://idp.example"))),
        });

        Accepts(Examples.Registry(), "a fragment on its shell's issuer", twoIssuers);
        RejectsFor(RegistryRules.FragmentIssuerMismatch, $"{At}/oauth/issuer",
            WithFragment(f => f["oauth"]!["issuer"] = "second"), "a fragment on a configured issuer its shell does not use", twoIssuers);
    }

    // ---- apis, per token set.

    [Fact]
    public void A_fragment_api_name_repeated_within_the_fragment_is_refused_and_the_shells_name_is_not()
    {
        Accepts(WithFragment(f => f["apis"]![0]!["name"] = "orders"), "a fragment api named as the shell's");
        RejectsFor(RegistryRules.DuplicateApiName, $"{At}/apis/1/name",
            WithFragment(f => f["apis"]!.AsArray().Add(Api("reports", "http://127.0.0.1:5081"))), "two fragment apis named reports");
    }

    [Fact]
    public void A_fragment_upstream_outside_upstream_policy_is_refused() =>
        RejectsFor(RegistryRules.UpstreamNotAllowed, $"{At}/apis/0/upstream",
            WithFragment(f => f["apis"]![0]!["upstream"] = "http://127.0.0.1:5099"), "a fragment upstream no policy allows");

    [Fact]
    public void A_fragment_timeout_the_model_cannot_hold_is_an_error_not_an_exception()
    {
        string text = Examples.Registry().ToJsonString().Replace("\"timeoutSeconds\":20", "\"timeoutSeconds\":20.0000000000000001", StringComparison.Ordinal);
        Assert.Contains("20.0000000000000001", text, StringComparison.Ordinal);

        RegistryValidation result = RegistryValidator.Validate(text, Examples.Policy(), null);

        Assert.Contains(result.Errors, error => error.Rule == RegistryRules.IntegerOutOfRange && error.Path == $"{At}/apis/0/timeoutSeconds");
    }

    [Fact]
    public void Multiple_upstream_origins_warn_per_token_set()
    {
        // The example: the shell on 5080, the fragment on 5081. One origin per token set.
        Assert.Empty(Accepts(Examples.Registry(), "the example").Warnings);

        // One upstream shared by the shell and a fragment does not warn.
        Assert.Empty(Accepts(WithFragment(f => f["apis"]![0]!["upstream"] = "http://127.0.0.1:5080"), "a shared upstream").Warnings);

        // Two origins in one fragment warn once, at that fragment's apis.
        RegistryValidation fragment = Accepts(
            WithFragment(f => f["apis"]!.AsArray().Add(Api("audit", "http://127.0.0.1:5080"))), "two origins in one fragment");
        RegistryIssue warning = Assert.Single(fragment.Warnings);
        Assert.Equal((RegistryRules.MultipleUpstreamOrigins, $"{At}/apis"), (warning.Rule, warning.Path));

        // Two origins on the shell warn at the shell's apis, not the fragment's.
        RegistryValidation shell = Accepts(
            Examples.RegistryEntry(e => e["apis"]!.AsArray().Add(Api("audit", "http://127.0.0.1:5081"))), "two origins on the shell");
        Assert.Equal((RegistryRules.MultipleUpstreamOrigins, "/apps/0/apis"), (Assert.Single(shell.Warnings).Rule, shell.Warnings[0].Path));
    }

    // ---- The fragment csp rule.

    [Theory]
    [InlineData("script-src")]
    [InlineData("style-src")]
    [InlineData("img-src")]
    [InlineData("font-src")]
    [InlineData("connect-src")]
    public void A_fragment_csp_may_extend_each_of_the_five_directives(string directive)
    {
        RegistryValidation result = Accepts(
            WithFragment(f => f["csp"] = new JsonObject { [directive] = new JsonArray("https://cdn.spa-poc.test") }), directive);
        Assert.Equal(["https://cdn.spa-poc.test"], Assert.Single(Assert.Single(result.Apps).Fragments).Csp[directive]);
    }

    [Theory]
    [InlineData("frame-ancestors")]
    [InlineData("default-src")]
    [InlineData("base-uri")]
    [InlineData("form-action")]
    [InlineData("object-src")]
    public void A_fragment_csp_never_widens_another_directive(string directive)
    {
        RegistryValidation result = Validate(WithFragment(f => f["csp"] = new JsonObject { [directive] = new JsonArray("https://cdn.spa-poc.test") }));
        Assert.False(result.IsValid, $"should refuse a fragment's {directive}");
        Assert.Contains(result.Errors, error => error.Rule == RegistryRules.Schema && error.Path.StartsWith($"{At}/csp", StringComparison.Ordinal));

        // The control: the shell's own csp may name it.
        Assert.True(Validate(Examples.RegistryEntry(e => e["csp"] = new JsonObject { [directive] = new JsonArray("'self'") })).IsValid,
            $"the shell's csp should accept {directive}");
    }

    [Theory]
    [InlineData("'strict-dynamic'")]
    [InlineData("'STRICT-DYNAMIC'")]
    [InlineData("'nonce-abc123'")]
    [InlineData("'sha256-B2yPHKaXnvFWtRChIbabYmUBFZdVfKKXHbWtWidDVF8='")]
    [InlineData("'sha384-abc'")]
    [InlineData("'sha512-abc'")]
    public void A_fragment_script_src_carries_no_strict_dynamic_nonce_or_hash(string source)
    {
        RejectsFor(RegistryRules.Schema, $"{At}/csp/script-src/0",
            WithFragment(f => f["csp"] = new JsonObject { ["script-src"] = new JsonArray(source) }), $"{source} in a fragment's script-src");

        // The control: the shell's script-src may carry it (the import map's hash is the shell's).
        Assert.True(Validate(Examples.RegistryEntry(e => e["csp"] = new JsonObject { ["script-src"] = new JsonArray(source) })).IsValid,
            $"the shell's script-src should accept {source}");
    }

    // ---- The fragments-per-shell cap, configuration only.

    [Fact]
    public void A_shell_at_the_cap_is_accepted_and_one_past_it_is_refused()
    {
        Assert.Equal(5, Examples.Policy().FragmentsPerShell);
        Accepts(WithFragmentCount(5), "five fragments at the placeholder cap");
        RejectsFor(RegistryRules.FragmentsPerShellExceeded, "/apps/0/fragments", WithFragmentCount(6), "six fragments");
    }

    [Fact]
    public void The_cap_is_the_policy_value_not_the_schemas()
    {
        PlatformPolicy two = Examples.Policy(policy => policy with { FragmentsPerShell = 2 });
        Accepts(WithFragmentCount(2), "two fragments under a cap of 2", two);
        RejectsFor(RegistryRules.FragmentsPerShellExceeded, "/apps/0/fragments", WithFragmentCount(3), "three under a cap of 2", two);

        PlatformPolicy seven = Examples.Policy(policy => policy with { FragmentsPerShell = 7 });
        Accepts(WithFragmentCount(7), "seven fragments under a cap of 7", seven);
    }

    [Fact]
    public void An_app_definition_meets_the_per_entry_fragment_rules()
    {
        // contracts/app-definition.example.json carries one fragment, reports, with no client fields.
        JsonObject definition = Examples.Json("app-definition.example.json");
        Assert.True(RegistryValidator.ValidateAppDefinition(definition.ToJsonString(), Examples.Policy()).IsValid);

        JsonArray fragments = definition["fragments"]!.AsArray();
        JsonObject second = fragments[0]!.DeepClone().AsObject();
        second.Remove("csp");
        fragments.Add(second);

        AppDefinitionValidation result = RegistryValidator.ValidateAppDefinition(definition.ToJsonString(), Examples.Policy());

        Assert.Contains(result.Errors, error => error.Rule == RegistryRules.DuplicateFragmentId && error.Path == "/fragments/1/fragmentId");
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>
/// The per-entry fingerprint decides eviction (docs/plan/01-plan.md, OptionsInvalidator; plan row 2.1:
/// "two loads of one document give equal fingerprints"). Equality alone passes for a constant, so each
/// test that expects equal fingerprints has a partner that expects them to differ.
/// </summary>
public sealed class FingerprintTests
{
    private static AppDefinition Only(string document) =>
        Assert.Single(RegistryValidator.Validate(document, Examples.Policy(), null).Apps);

    private static AppDefinition Only(JsonNode document) => Only(document.ToJsonString());

    [Fact]
    public void Two_loads_of_one_document_give_equal_fingerprints()
    {
        string text = Examples.Text("registry.example.json");

        Assert.Equal(Only(text).Fingerprint, Only(text).Fingerprint);
    }

    [Fact]
    public void Member_order_and_whitespace_do_not_change_the_fingerprint()
    {
        string reordered = """
            {"apps":[{"session":{"lifetimeMinutes":480},"csp":{"img-src":["data:"]},
              "apis":[{"timeoutSeconds":30,"upstream":"http://127.0.0.1:5080","name":"orders"}],
              "oauth":{"resource":"https://orders.spa-poc.test","scopes":["openid","profile","https://orders.spa-poc.test/read"],
                       "clientSecretRef":"ssm:/spa-platform/poc/apps/app1/client-secret:1","clientId":"1example23456789","issuer":"cognito-poc"},
              "release":"2026-10-10.1","host":"app1.spa-poc.test:8443","appId":"app1"}],
             "revision":42,"schemaVersion":1}
            """;

        Assert.Equal(Only(Examples.Text("registry.example.json")).Fingerprint, Only(reordered).Fingerprint);
    }

    [Fact]
    public void The_fingerprint_is_the_sha256_of_the_entry_with_sorted_members_and_no_whitespace()
    {
        // Written out by hand from contracts/registry.example.json, not produced by the code under test.
        const string canonical =
            """{"apis":[{"name":"orders","timeoutSeconds":30,"upstream":"http://127.0.0.1:5080"}],"appId":"app1","csp":{"img-src":["data:"]},"host":"app1.spa-poc.test:8443","oauth":{"clientId":"1example23456789","clientSecretRef":"ssm:/spa-platform/poc/apps/app1/client-secret:1","issuer":"cognito-poc","resource":"https://orders.spa-poc.test","scopes":["openid","profile","https://orders.spa-poc.test/read"]},"release":"2026-10-10.1","session":{"lifetimeMinutes":480}}""";
        string expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        Assert.Equal(expected, Only(Examples.Registry()).Fingerprint);
    }

    [Fact]
    public void An_escaped_spelling_of_the_same_string_gives_the_same_fingerprint()
    {
        string escaped = Examples.Text("registry.example.json").Replace("\"app1\"", "\"\\u0061pp1\"", StringComparison.Ordinal);
        Assert.Contains("\\u0061pp1", escaped, StringComparison.Ordinal);

        Assert.Equal(Only(Examples.Text("registry.example.json")).Fingerprint, Only(escaped).Fingerprint);
    }

    [Theory]
    [InlineData("30.0")]
    [InlineData("3e1")]
    public void Another_spelling_of_the_same_integer_gives_the_same_fingerprint(string spelling)
    {
        // Draft 2020-12 calls 30.0 and 3e1 integers, so the schema accepts them; the entry says the same thing.
        string text = Examples.Text("registry.example.json");
        string respelled = text.Replace("\"timeoutSeconds\": 30", $"\"timeoutSeconds\": {spelling}", StringComparison.Ordinal);
        Assert.Contains(spelling, respelled, StringComparison.Ordinal);

        Assert.Equal(Only(text).Fingerprint, Only(respelled).Fingerprint);
        Assert.NotEqual(Only(text).Fingerprint,
            Only(text.Replace("\"timeoutSeconds\": 30", "\"timeoutSeconds\": 31", StringComparison.Ordinal)).Fingerprint);
    }

    [Fact]
    public void A_changed_value_changes_the_fingerprint()
    {
        string original = Only(Examples.Registry()).Fingerprint;

        Assert.NotEqual(original, Only(Examples.RegistryEntry(e => e["release"] = "2026-10-10.2")).Fingerprint);
        Assert.NotEqual(original, Only(Examples.RegistryEntry(e => e["session"]!["lifetimeMinutes"] = 481)).Fingerprint);
        Assert.NotEqual(original, Only(Examples.RegistryEntry(e => e["oauth"]!["clientSecretRef"] = "ssm:/spa-platform/poc/apps/app1/client-secret:2")).Fingerprint);
    }

    [Fact]
    public void Array_order_is_the_authors_and_changes_the_fingerprint()
    {
        string original = Only(Examples.Registry()).Fingerprint;

        Assert.NotEqual(original, Only(Examples.RegistryEntry(e =>
            e["oauth"]!["scopes"] = new JsonArray("profile", "openid", "https://orders.spa-poc.test/read"))).Fingerprint);
    }

    [Fact]
    public void Changing_one_entry_leaves_the_other_entrys_fingerprint_alone()
    {
        RegistryValidation before = RegistryValidator.Validate(Examples.RegistryWithSecondEntry().ToJsonString(), Examples.Policy(), null);
        RegistryValidation after = RegistryValidator.Validate(
            Examples.RegistryWithSecondEntry(e => e["release"] = "2026-10-11.1").ToJsonString(), Examples.Policy(), null);

        Assert.Equal(before.Apps[0].Fingerprint, after.Apps[0].Fingerprint);
        Assert.NotEqual(before.Apps[1].Fingerprint, after.Apps[1].Fingerprint);
        // Not a fingerprint of the document: the revision is outside every entry.
        Assert.Equal(before.Apps[0].Fingerprint, Only(Examples.Registry(d => d["revision"] = 99)).Fingerprint);
    }
}

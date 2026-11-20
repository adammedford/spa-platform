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
        // contracts/registry.example.json's app1 with its fragment, members in another order at every level
        // (the fragment, its oauth, its apis[0] and its csp among them) and other whitespace.
        // Red if the canonical form stops sorting members inside fragments[].
        string reordered = """
            {"apps":[{"fragments":[
                {"entry":"entry.js","csp":{"img-src":["https://images.spa-poc.test"]},
                 "apis":[{"timeoutSeconds":20,"upstream":"http://127.0.0.1:5081","name":"reports"}],
                 "release":"2026-10-12.1",
                 "oauth":{"resource":"https://reports.spa-poc.test","scopes":["openid","https://reports.spa-poc.test/read"],
                          "clientSecretRef":"ssm:/spa-platform/poc/apps/app1/fragments/reports/client-secret:1",
                          "clientId":"2example34567890","issuer":"cognito-poc"},
                 "fragmentId":"reports"}],
              "session":{"lifetimeMinutes":480},"csp":{"img-src":["data:"]},
              "apis":[{"timeoutSeconds":30,"upstream":"http://127.0.0.1:5080","name":"orders"}],
              "oauth":{"resource":"https://orders.spa-poc.test","scopes":["openid","profile","https://orders.spa-poc.test/read"],
                       "clientSecretRef":"ssm:/spa-platform/poc/apps/app1/client-secret:1","clientId":"1example23456789","issuer":"cognito-poc"},
              "release":"2026-10-10.1","host":"app1.spa-poc.test:8443","appId":"app1"}],
             "revision":42,"schemaVersion":2}
            """;

        Assert.Equal(Only(Examples.Text("registry.example.json")).Fingerprint, Only(reordered).Fingerprint);
    }

    [Fact]
    public void The_fingerprint_is_the_sha256_of_the_entry_with_sorted_members_and_no_whitespace()
    {
        // Written out by hand from contracts/registry.example.json, not produced by the code under test:
        // app1 with its fragment, members sorted at every level, arrays in the author's order.
        // Red if the fingerprint drops or reorders fragments, or hashes anything but the whole entry.
        const string canonical =
            """{"apis":[{"name":"orders","timeoutSeconds":30,"upstream":"http://127.0.0.1:5080"}],"appId":"app1","csp":{"img-src":["data:"]},"fragments":[{"apis":[{"name":"reports","timeoutSeconds":20,"upstream":"http://127.0.0.1:5081"}],"csp":{"img-src":["https://images.spa-poc.test"]},"entry":"entry.js","fragmentId":"reports","oauth":{"clientId":"2example34567890","clientSecretRef":"ssm:/spa-platform/poc/apps/app1/fragments/reports/client-secret:1","issuer":"cognito-poc","resource":"https://reports.spa-poc.test","scopes":["openid","https://reports.spa-poc.test/read"]},"release":"2026-10-12.1"}],"host":"app1.spa-poc.test:8443","oauth":{"clientId":"1example23456789","clientSecretRef":"ssm:/spa-platform/poc/apps/app1/client-secret:1","issuer":"cognito-poc","resource":"https://orders.spa-poc.test","scopes":["openid","profile","https://orders.spa-poc.test/read"]},"release":"2026-10-10.1","session":{"lifetimeMinutes":480}}""";
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
    /// <summary>
    /// The whole-entry fingerprint (#992): a change to any one field of a fragment changes its entry's
    /// fingerprint, so every scheme of that entry is evicted together. Every leaf of the example's
    /// fragment is changed in turn, so a field the canonical form dropped would stay equal here.
    /// </summary>
    [Fact]
    public void A_change_to_any_one_field_of_a_fragment_changes_the_entrys_fingerprint()
    {
        JsonObject entry = Examples.Entry(Examples.Registry(), 0);
        string original = EntryFingerprint.Of(entry);
        List<string> leaves = [];

        foreach ((string path, Action<JsonObject> change) in FragmentLeaves(entry["fragments"]![0]!, "/fragments/0"))
        {
            JsonObject changed = entry.DeepClone().AsObject();
            change(changed);
            Assert.True(original != EntryFingerprint.Of(changed), $"a change at {path} left the fingerprint as it was");
            leaves.Add(path);
        }

        // Every field of the example fragment, its apis[0] and its csp included.
        Assert.Equal(12, leaves.Count);
    }

    [Fact]
    public void A_fragment_change_through_the_validator_changes_its_entry_and_no_other()
    {
        RegistryValidation before = RegistryValidator.Validate(Examples.RegistryWithSecondEntry().ToJsonString(), Examples.Policy(), null);

        foreach (Action<JsonObject> change in new Action<JsonObject>[]
        {
            fragment => fragment["release"] = "2026-10-13.1",
            fragment => fragment["entry"] = "remote.js",
            fragment => fragment["fragmentId"] = "reports-2",
            fragment => fragment["oauth"]!["clientSecretRef"] = "ssm:/spa-platform/poc/apps/app2/fragments/reports/client-secret:2",
            fragment => fragment["csp"] = new JsonObject { ["font-src"] = new JsonArray("https://fonts.spa-poc.test") },
            fragment => fragment["apis"]![0]!["timeoutSeconds"] = 21,
        })
        {
            RegistryValidation after = RegistryValidator.Validate(
                Examples.RegistryWithSecondEntry(e => change(e["fragments"]![0]!.AsObject())).ToJsonString(), Examples.Policy(), null);
            Assert.True(after.IsValid, string.Join('\n', after.Errors));

            Assert.Equal(before.Apps[0].Fingerprint, after.Apps[0].Fingerprint);
            Assert.NotEqual(before.Apps[1].Fingerprint, after.Apps[1].Fingerprint);
        }
    }

    /// <summary>Each leaf under <paramref name="node"/>, with a change that gives it another value.</summary>
    private static IEnumerable<(string Path, Action<JsonObject> Change)> FragmentLeaves(JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (KeyValuePair<string, JsonNode?> member in value)
                {
                    foreach ((string Path, Action<JsonObject> Change) leaf in FragmentLeaves(member.Value!, $"{path}/{member.Key}"))
                    {
                        yield return leaf;
                    }
                }

                break;
            case JsonArray value when value.All(item => item is JsonValue):
                // An array of strings is one field: its author's list.
                yield return (path, entry => At(entry, path).AsArray().Add("changed"));
                break;
            case JsonArray value:
                for (int i = 0; i < value.Count; i++)
                {
                    foreach ((string Path, Action<JsonObject> Change) leaf in FragmentLeaves(value[i]!, $"{path}/{i}"))
                    {
                        yield return leaf;
                    }
                }

                break;
            default:
                string parent = path[..path.LastIndexOf('/')];
                string name = path[(path.LastIndexOf('/') + 1)..];
                yield return (path, entry =>
                {
                    JsonNode owner = At(entry, parent);
                    owner[name] = node.GetValueKind() == System.Text.Json.JsonValueKind.Number
                        ? node.GetValue<int>() + 1
                        : node.GetValue<string>() + "-changed";
                });
                break;
        }
    }

    private static JsonNode At(JsonNode root, string path)
    {
        JsonNode node = root;
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]! : node[segment]!;
        }

        return node;
    }
}

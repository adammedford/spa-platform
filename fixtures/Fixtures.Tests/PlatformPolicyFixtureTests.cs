using System.Text.Json.Nodes;

namespace Fixtures.Tests;

/// <summary>
/// The three bounds a JSON schema cannot express, because a schema cannot see platform
/// configuration: a host ends with an allowed suffix, each upstream matches the allowed upstreams,
/// and the issuer names one of the configured issuers. Task 2.1's validator enforces them at run
/// time against the task environment; these assertions catch a fixture that would be refused, at the
/// only point where the two files are written by hand.
///
/// The configuration read here is <c>contracts/platform-config.example.json</c>, which is the
/// contract's own example of the task environment task 1.4 supplies.
/// </summary>
public sealed class PlatformPolicyFixtureTests
{
    [Fact]
    public void Every_host_ends_with_an_allowed_suffix()
    {
        string[] suffixes = [.. Strings(Policy["AllowedHostSuffixes"]!)];

        foreach (string path in Definitions)
        {
            string host = FixtureFiles.Read(path)["host"]!.GetValue<string>();
            Assert.True(
                suffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal)),
                $"{Path.GetFileName(path)} has host {host}, which ends with none of {string.Join(", ", suffixes)}.");
        }
    }

    [Fact]
    public void Every_upstream_is_an_allowed_upstream()
    {
        // Threat T5 again, one layer out: a registry writer cannot widen the set of origins the
        // proxy may reach, so a fixture that names one outside it is a fixture that never applies.
        string[] allowed = [.. Strings(Policy["AllowedUpstreams"]!)];

        foreach (string path in Definitions)
        {
            foreach (JsonNode? api in FixtureFiles.Read(path)["apis"]?.AsArray() ?? [])
            {
                Assert.Contains(api!["upstream"]!.GetValue<string>(), allowed);
            }
        }
    }

    [Fact]
    public void Every_issuer_names_a_configured_issuer()
    {
        IEnumerable<string> issuers = FixtureFiles.PlatformConfiguration["Issuers"]!.AsObject().Select(issuer => issuer.Key);

        foreach (string path in Definitions)
        {
            Assert.Contains(FixtureFiles.Read(path)["oauth"]!["issuer"]!.GetValue<string>(), issuers);
        }
    }

    private static IEnumerable<string> Definitions => FixtureFiles.AllDefinitions;

    private static JsonObject Policy => FixtureFiles.PlatformConfiguration["Policy"]!.AsObject();

    private static IEnumerable<string> Strings(JsonNode array) =>
        array.AsArray().Select(value => value!.GetValue<string>());
}

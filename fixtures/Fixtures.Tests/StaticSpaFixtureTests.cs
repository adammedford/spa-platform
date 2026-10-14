using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fixtures.Tests;

/// <summary>
/// Task 5.1: three static SPAs with no build step, each of which shows the session, calls its API,
/// posts with the CSRF header and logs out, with a visible marker and one hashed asset, and app 2
/// carrying one extra CSP source.
///
/// Each directory under <c>fixtures/spa/</c> is a release exactly as <c>spactl release publish</c>
/// uploads it (docs/plan/05 -> "Onboarding a third SPA", step 2), so the seam is the directory
/// itself, checked against the app's definition and contracts/http-contract.md. Every expected value
/// is read from one of those two, never restated.
///
/// Nothing here executes the script. These tests prove what the files say and that the policy lets
/// them say it; that the script then behaves in a browser is proved with task 5.2's headless browser.
/// </summary>
public sealed partial class StaticSpaFixtureTests
{
    [Fact]
    public void There_is_one_spa_per_app_definition()
    {
        // App 3 is published during F-07, so its release exists before its definition reaches the apps root.
        Assert.Equal(
            FixtureFiles.AllDefinitions.Select(Path.GetFileNameWithoutExtension),
            Directory.EnumerateDirectories(FixtureFiles.Path("fixtures", "spa")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_shell_shows_a_visible_marker_naming_its_app(string appId)
    {
        // F-01 tells two shells apart by what they show, so the marker is text, not an attribute.
        SpaRelease release = SpaRelease.Of(appId);

        Match marker = Regex.Match(release.IndexHtml, "<h1 id=\"marker\">([^<]*)</h1>");

        Assert.True(marker.Success, $"{appId}'s index.html has no <h1 id=\"marker\">.");
        Assert.Contains(AppIdOf(appId), marker.Groups[1].Value, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_release_has_one_hashed_asset_and_the_shell_loads_it(string appId)
    {
        // contracts/s3-layout.md caches assets/** for a year as immutable, which is safe only when a
        // change to the bytes changes the name. With no build step the name is set by hand, so the
        // test recomputes it from the bytes.
        SpaRelease release = SpaRelease.Of(appId);

        string asset = Assert.Single(release.Assets);
        Match name = Regex.Match(asset, "^assets/[a-z]+\\.(?<hash>[0-9a-f]{8})\\.js$");
        Assert.True(name.Success, $"{asset} is not named <name>.<8 hex digits>.js.");
        Assert.Equal(release.ContentHash(asset), name.Groups["hash"].Value);

        Assert.Equal(["/" + asset], release.ScriptSources);
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_release_has_an_index_and_nothing_under_a_reserved_path(string appId)
    {
        // "An SPA cannot serve content under ..." in the browser contract: the gateway answers those
        // paths itself, so a file there would be unreachable at best.
        SpaRelease release = SpaRelease.Of(appId);

        Assert.Contains("index.html", release.Files);
        foreach (string file in release.Files)
        {
            string top = "/" + file.Split('/')[0];
            Assert.DoesNotContain(top, ReservedPrefixes());
        }
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_shell_fits_its_apps_own_policy(string appId)
    {
        SpaRelease release = SpaRelease.Of(appId);
        IReadOnlyDictionary<string, IReadOnlyList<string>> policy =
            ContentSecurityPolicyFit.For(FixtureFiles.Definition(appId));

        Assert.Empty(ContentSecurityPolicyFit.Violations(release.IndexHtml, release.Scripts, policy));
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void An_app_needs_more_than_the_baseline_exactly_when_its_definition_adds_a_source(string appId)
    {
        // "App 2 needs one extra CSP source, to prove per-app policy": under the bare baseline app 2's
        // shell must be refused once, for the very source its definition adds, and apps 1 and 3,
        // which add nothing, must not be refused at all. Keyed off the definition, so moving the
        // addition to another app moves the expectation with it.
        SpaRelease release = SpaRelease.Of(appId);
        JsonObject? additions = FixtureFiles.Definition(appId)["csp"] as JsonObject;

        IReadOnlyList<string> refused = ContentSecurityPolicyFit.Violations(
            release.IndexHtml, release.Scripts, ContentSecurityPolicyFit.Baseline);

        if (additions is null)
        {
            Assert.Empty(refused);
        }
        else
        {
            (string directive, JsonNode? sources) = Assert.Single(additions);
            string source = Assert.Single(sources!.AsArray())!.GetValue<string>();
            string violation = Assert.Single(refused);
            Assert.StartsWith($"{directive}: {source}", violation, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_shell_names_its_app_and_its_first_api(string appId)
    {
        // The script builds /api/{name} from data-api, so the name must be one the definition lists,
        // or the gateway answers 404 unknown_api. The first is the app's own API; the second exists
        // for N-17 to refuse (task 5.5).
        SpaRelease release = SpaRelease.Of(appId);
        JsonNode definition = FixtureFiles.Definition(appId);

        Assert.Equal(AppIdOf(appId), release.MainAttribute("data-app"));
        Assert.Equal(definition["apis"]![0]!["name"]!.GetValue<string>(), release.MainAttribute("data-api"));
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_script_reads_the_session_and_signs_in_by_navigation(string appId)
    {
        string scripts = SpaRelease.Of(appId).Scripts;

        Assert.Contains("fetch(\"/bff/session\"", scripts, StringComparison.Ordinal);
        Assert.Contains("window.location.assign(\"/bff/login?returnUrl=\"", scripts, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(\"/bff/login", scripts, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_script_calls_its_api_and_sends_the_csrf_header_on_unsafe_methods_only(string appId)
    {
        // The header the contract names, on every unsafe method and nowhere else (the token travels
        // in this header and nowhere else). The routes are the mock API's, task 5.3: /whoami, /echo.
        string header = CsrfHeaderName();
        List<string> calls = FetchCalls(SpaRelease.Of(appId).Scripts);

        string read = Assert.Single(calls, call => call.StartsWith("api + \"/whoami\"", StringComparison.Ordinal));
        Assert.DoesNotContain(header, read, StringComparison.Ordinal);

        string post = Assert.Single(calls, call => call.StartsWith("api + \"/echo\"", StringComparison.Ordinal));
        Assert.Contains("method: \"POST\"", post, StringComparison.Ordinal);
        Assert.Contains($"\"{header}\": csrfToken", post, StringComparison.Ordinal);

        foreach (string call in calls)
        {
            bool unsafeMethod = Regex.IsMatch(call, "method:\\s*\"(?!GET\"|HEAD\"|OPTIONS\")");
            Assert.Equal(unsafeMethod, call.Contains(header, StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_shell_logs_out_by_form_post(string appId)
    {
        // "POST /bff/logout | Form navigation": a fetch would not follow the provider's logout redirect.
        SpaRelease release = SpaRelease.Of(appId);

        Assert.Matches("<form method=\"post\" action=\"/bff/logout\">", release.IndexHtml);
        Assert.DoesNotContain(FetchCalls(release.Scripts), call => call.Contains("/bff/logout", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void No_script_puts_anything_where_F_05_looks_for_tokens(string appId)
    {
        // F-05 dumps cookies, web storage and the page and expects nothing token-shaped. The CSRF
        // token stays in a variable; the page is written with textContent only.
        string scripts = SpaRelease.Of(appId).Scripts;

        foreach (string sink in new[] { "localStorage", "sessionStorage", "document.cookie", "innerHTML", "outerHTML", "document.write" })
        {
            Assert.DoesNotContain(sink, scripts, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch("show\\([^)]*csrfToken", scripts);
    }

    // The checker has to be able to refuse, or every "fits" above is a label (CODING_STANDARDS.md,
    // "An assertion must be able to fail"). Each case below breaks the baseline one way.

    [Theory]
    [InlineData("<script>alert(1)</script>", "", "script-src: an inline <script>")]
    [InlineData("<button onclick=\"go()\">x</button>", "", "script-src: an inline event-handler attribute")]
    [InlineData("<p style=\"color:red\">x</p>", "", "style-src: inline style")]
    [InlineData("<style>p{}</style>", "", "style-src: inline style")]
    [InlineData("<img src=\"data:image/gif;base64,AA\">", "", "img-src: data:image/gif;base64,AA")]
    [InlineData("<script src=\"https://cdn.example/x.js\"></script>", "", "script-src: https://cdn.example/x.js")]
    [InlineData("<form method=\"post\" action=\"https://elsewhere.example/\">", "", "form-action: https://elsewhere.example/")]
    [InlineData("", "eval(\"1\");", "script-src: eval or the Function constructor")]
    [InlineData("", "fetch(\"https://api.example/x\");", "connect-src: https://api.example/x")]
    public void The_policy_check_refuses_what_the_baseline_refuses(string html, string scripts, string expected)
    {
        Assert.Contains(expected, ContentSecurityPolicyFit.Violations(html, scripts, ContentSecurityPolicyFit.Baseline));
    }

    [Fact]
    public void The_baseline_is_read_from_the_contract_with_its_ten_directives()
    {
        // registry.schema.json lists exactly the directives an entry may add to, which are the
        // baseline's ten. A parse that lost one would make a fixture's addition land nowhere.
        JsonNode schema = FixtureFiles.Read(FixtureFiles.Path("contracts", "registry.schema.json"));
        IEnumerable<string> addable = Directives(schema);

        Assert.Equal(addable.Order(StringComparer.Ordinal), ContentSecurityPolicyFit.Baseline.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["'self'"], ContentSecurityPolicyFit.Baseline["script-src"]);
    }

    private static IEnumerable<string> Directives(JsonNode schema)
    {
        // The csp subschema closes its property names with an enum of the directives it may extend.
        if (Walk(schema).OfType<JsonObject>().FirstOrDefault(node => node["title"]?.GetValue<string>() == "csp")
            is { } csp && csp["propertyNames"]?["enum"] is JsonArray directives)
        {
            return directives.Select(d => d!.GetValue<string>());
        }

        throw new InvalidOperationException("registry.schema.json has no csp properties.");
    }

    private static IEnumerable<JsonNode?> Walk(JsonNode? node)
    {
        yield return node;
        IEnumerable<JsonNode?> children = node switch
        {
            JsonObject obj => obj.Select(p => p.Value),
            JsonArray array => array,
            _ => [],
        };
        foreach (JsonNode? child in children)
        {
            foreach (JsonNode? descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The argument text of each fetch call, up to the promise chain that follows it.</summary>
    private static List<string> FetchCalls(string scripts) =>
        [.. scripts.Split("fetch(").Skip(1).Select(call => call.Split(".then(")[0])];

    private static string CsrfHeaderName()
    {
        string contract = File.ReadAllText(FixtureFiles.Path("contracts", "http-contract.md"));
        Match row = Regex.Match(contract, "^\\| `([A-Za-z-]+)` \\| Every unsafe method", RegexOptions.Multiline);
        Assert.True(row.Success, "contracts/http-contract.md names no CSRF header.");
        return row.Groups[1].Value;
    }

    private static IEnumerable<string> ReservedPrefixes()
    {
        string contract = File.ReadAllText(FixtureFiles.Path("contracts", "http-contract.md"));
        Match sentence = Regex.Match(contract, "An SPA cannot serve content under (.*?)\\.\\s*\\n\\s*\\n", RegexOptions.Singleline);
        Assert.True(sentence.Success, "contracts/http-contract.md lists no reserved paths.");
        return [.. Regex.Matches(sentence.Groups[1].Value, "`(/[^`]+?)/?`").Select(m => m.Groups[1].Value)];
    }

    private static string AppIdOf(string appId) =>
        FixtureFiles.Definition(appId)["appId"]!.GetValue<string>();

    public static TheoryData<string> EveryApp => ["app1", "app2", "app3"];
}
